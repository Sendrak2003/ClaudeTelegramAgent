using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Hosting;
using ClaudeTelegramAgent.Infrastructure;
using ClaudeTelegramAgent.Infrastructure.Claude;
using ClaudeTelegramAgent.Infrastructure.Reminders;
using ClaudeTelegramAgent.Infrastructure.Speech;
using ClaudeTelegramAgent.Infrastructure.Telegram;
using ClaudeTelegramAgent.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.

var configuration = builder.Configuration;

var claudeWorkDir = configuration["Claude:WorkDir"];
if (string.IsNullOrWhiteSpace(claudeWorkDir))
{
    throw new InvalidOperationException("Configuration value 'Claude:WorkDir' is required.");
}

var claudeBin = configuration["Claude:Bin"] ?? "claude";
var claudePermissionMode = configuration["Claude:PermissionMode"] ?? "acceptEdits";
var claudeTimeoutSeconds = configuration.GetValue("Claude:TimeoutSeconds", 300);
var claudeAuthModeRaw = configuration["Claude:AuthMode"] ?? "subscription";
var claudeAuthMode = claudeAuthModeRaw.Equals("apikey", StringComparison.OrdinalIgnoreCase)
    ? ClaudeAuthMode.ApiKey
    : ClaudeAuthMode.Subscription;
var claudeApiKey = configuration["Claude:ApiKey"];
var claudeModelConfigured = configuration["Claude:Model"];
var claudeModel = string.IsNullOrWhiteSpace(claudeModelConfigured) ? null : claudeModelConfigured.Trim();
// Пустое или нулевое значение = без ограничения.
int? claudeMaxTurns = int.TryParse(configuration["Claude:MaxTurns"], out var maxTurnsParsed) && maxTurnsParsed > 0
    ? maxTurnsParsed
    : null;

var telegramBotToken = configuration["Telegram:BotToken"];
if (string.IsNullOrWhiteSpace(telegramBotToken))
{
    throw new InvalidOperationException("Configuration value 'Telegram:BotToken' is required.");
}

var reminderDbPathConfigured = configuration["Reminders:DbPath"];
// "" в appsettings.json — не null, поэтому обычный ?? тут не сработал бы:
// пустая строка воспринималась бы SQLite как одноразовая анонимная БД в памяти,
// из-за чего напоминания молча "терялись" при каждом новом подключении.
var reminderDbPath = string.IsNullOrWhiteSpace(reminderDbPathConfigured)
    ? Path.Combine(claudeWorkDir, ".agent-data", "reminders.db")
    : reminderDbPathConfigured;
var reminderPollSeconds = configuration.GetValue("Reminders:PollSeconds", 30);

var agentTimeZone = configuration["Agent:TimeZone"] ?? "Europe/Moscow";

var allowedUserIdsRaw = configuration["Agent:AllowedUserIds"];
if (string.IsNullOrWhiteSpace(allowedUserIdsRaw))
{
    throw new InvalidOperationException("Configuration value 'Agent:AllowedUserIds' is required and must not be empty.");
}

var allowedUserIds = allowedUserIdsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(long.Parse)
    .ToHashSet();

if (allowedUserIds.Count == 0)
{
    throw new InvalidOperationException("Configuration value 'Agent:AllowedUserIds' must contain at least one user id.");
}

var sessionsFilePath = Path.Combine(claudeWorkDir, ".agent-data", "sessions.json");
var incomingFilesDirectory = Path.Combine(claudeWorkDir, ".agent-data", "incoming");

// Распознавание голосовых: Whisper через sherpa-onnx, модели лежат в .agent-data/models (вне git).
// Всё переопределяется через конфиг Speech:*, значения по умолчанию подходят для локального запуска.
var agentDataDir = Path.Combine(claudeWorkDir, ".agent-data");
string SpeechSetting(string key, string fallback) =>
    string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!;

var speechModelsDir = Path.Combine(agentDataDir, "models");
var speechOptions = new SpeechOptions(
    SpeechSetting("Speech:ModelDir", Path.Combine(speechModelsDir, "sherpa-onnx-whisper-turbo")),
    SpeechSetting("Speech:VadModel", Path.Combine(speechModelsDir, "silero_vad.onnx")),
    SpeechSetting("Speech:Ffmpeg", "ffmpeg"),
    SpeechSetting("Speech:Language", "ru"),
    configuration.GetValue("Speech:Threads", Math.Min(8, Environment.ProcessorCount)),
    configuration.GetValue("Speech:TimeoutSeconds", 600));

builder.Services.AddSingleton(new AccessControlOptions(allowedUserIds));
builder.Services.AddSingleton(new ReminderPollingOptions(reminderPollSeconds));

builder.Services.AddClaudeAgentClient(new ClaudeCliOptions(
    claudeWorkDir,
    claudeBin,
    claudePermissionMode,
    claudeTimeoutSeconds,
    claudeAuthMode,
    claudeApiKey,
    claudeModel,
    claudeMaxTurns));

builder.Services.AddReminderPersistence(new SqliteReminderOptions(reminderDbPath));
builder.Services.AddSessionStore(sessionsFilePath);
builder.Services.AddTelegramMessaging(new TelegramOptions(
    telegramBotToken,
    incomingFilesDirectory,
    configuration.GetValue("Telegram:UseCustomEmoji", false)));
builder.Services.AddSpeechTranscription(speechOptions);
builder.Services.AddSystemClock(agentTimeZone);

builder.Services.AddSingleton<AgentConversationService>();
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddSingleton<ReminderDeliveryService>();

builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<ReminderPollingHostedService>();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/status", (IConfiguration config) => Results.Ok(new
{
    WorkDir = claudeWorkDir,
    PermissionMode = claudePermissionMode,
    AuthMode = claudeAuthMode.ToString(),
    AllowedUserCount = allowedUserIds.Count,
}));

app.Run();
