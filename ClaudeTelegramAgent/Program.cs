using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Hosting;
using ClaudeTelegramAgent.Infrastructure;
using ClaudeTelegramAgent.Infrastructure.Claude;
using ClaudeTelegramAgent.Infrastructure.Reminders;
using ClaudeTelegramAgent.Infrastructure.Speech;
using ClaudeTelegramAgent.Infrastructure.Telegram;
using ClaudeTelegramAgent.Live;
using ClaudeTelegramAgent.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();


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
int? claudeMaxTurns = int.TryParse(configuration["Claude:MaxTurns"], out var maxTurnsParsed) && maxTurnsParsed > 0
    ? maxTurnsParsed
    : null;

var telegramBotToken = configuration["Telegram:BotToken"];
if (string.IsNullOrWhiteSpace(telegramBotToken))
{
    throw new InvalidOperationException("Configuration value 'Telegram:BotToken' is required.");
}

var reminderDbPathConfigured = configuration["Reminders:DbPath"];
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
builder.Services.AddSingleton<AccessList>();
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

double? voiceTemperature = double.TryParse(configuration["Voice:Temperature"], System.Globalization.CultureInfo.InvariantCulture, out var temperatureParsed)
    ? temperatureParsed
    : null;
builder.Services.AddSpeechSynthesis(new QwenTtsOptions(
    SpeechSetting("Voice:BaseUrl", "http://vivian:8099"),
    SpeechSetting("Voice:Speaker", "vivian"),
    SpeechSetting("Voice:Language", "Russian"),
    voiceTemperature,
    configuration.GetValue("Voice:TimeoutSeconds", 120),
    SpeechSetting("Voice:Ffmpeg", "ffmpeg"),
    configuration["Voice:AudioFilter"]),
    Path.Combine(agentDataDir, "voice-cache"),
    configuration.GetValue("Voice:CacheMaxFiles", 500));
builder.Services.AddSingleton(new VoiceReplyOptions(
    configuration.GetValue("Voice:Enabled", false),
    configuration.GetValue("Voice:MaxChars", 700),
    configuration.GetValue("Voice:OnlyAfterVoiceInput", false),
    configuration.GetValue("Voice:BatchFromChunks", 0),
    configuration.GetValue("Voice:BatchParallelism", 4),
    configuration.GetValue("Voice:DefaultOn", true)));
builder.Services.AddSingleton<VoiceReplyService>();
builder.Services.AddSingleton<IReplyAddon>(sp => sp.GetRequiredService<VoiceReplyService>());

builder.Services.AddSingleton<LiveVoiceHub>();
builder.Services.AddSingleton(sp => new LiveFillerCache(
    sp.GetRequiredService<ISpeechStreamer>(),
    Path.Combine(agentDataDir, "voice-fillers"),
    $"{SpeechSetting("Voice:Speaker", "vivian")}|{SpeechSetting("Voice:Language", "Russian")}|{voiceTemperature}",
    sp.GetRequiredService<ILogger<LiveFillerCache>>()));
builder.Services.AddSingleton(new LiveVoiceOptions(
    configuration.GetValue("Live:Enabled", true),
    configuration.GetValue("Live:MaxChars", 1500),
    configuration["Live:PublicUrl"],
    configuration.GetValue("Live:BatchMax", 3)));
SpeechText.ConfigureChunks(configuration.GetValue("Voice:ChunkTarget", 100), configuration.GetValue("Voice:ChunkMax", 160));
builder.Services.AddSingleton<LiveVoiceService>();
builder.Services.AddSingleton<IReplyAddon>(sp => sp.GetRequiredService<LiveVoiceService>());

builder.Services.AddSingleton<AgentConversationService>();
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddSingleton<ReminderDeliveryService>();

builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<ReminderPollingHostedService>();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapLive(telegramBotToken, allowedUserIds, configuration["Live:DevKey"]);

app.MapGet("/status", (IConfiguration config) => Results.Ok(new
{
    WorkDir = claudeWorkDir,
    PermissionMode = claudePermissionMode,
    AuthMode = claudeAuthMode.ToString(),
    AllowedUserCount = allowedUserIds.Count,
}));

app.Run();
