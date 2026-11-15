using System.Text;
using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Infrastructure;
using ClaudeTelegramAgent.Infrastructure.Reminders;
using ClaudeTelegramAgent.RemindersMcpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var dbPath = Environment.GetEnvironmentVariable("REMINDERS_DB")
    ?? throw new InvalidOperationException("Environment variable REMINDERS_DB is not set.");
var timeZoneId = Environment.GetEnvironmentVariable("AGENT_TIMEZONE") ?? "UTC";
var telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new InvalidOperationException("Environment variable TELEGRAM_BOT_TOKEN is not set.");

builder.Services.AddReminderPersistence(new SqliteReminderOptions(dbPath));
builder.Services.AddSystemClock(timeZoneId);
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddSingleton(CurrentChatContext.FromEnvironment());
builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(telegramBotToken));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReminderToolsAdapter>()
    .WithTools<MessagingToolsAdapter>();

var host = builder.Build();
await host.RunAsync();
