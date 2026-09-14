using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Infrastructure.Claude;
using ClaudeTelegramAgent.Infrastructure.Reminders;
using ClaudeTelegramAgent.Infrastructure.Sessions;
using ClaudeTelegramAgent.Infrastructure.Telegram;
using ClaudeTelegramAgent.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeTelegramAgent.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeAgentClient(this IServiceCollection services, ClaudeCliOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IClaudeAgentClient, ClaudeCliAgentClient>();
        return services;
    }

    public static IServiceCollection AddReminderPersistence(this IServiceCollection services, SqliteReminderOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IReminderRepository, SqliteReminderRepository>();
        return services;
    }

    public static IServiceCollection AddSessionStore(this IServiceCollection services, string sessionsFilePath)
    {
        services.AddSingleton<ISessionStore>(new JsonFileSessionStore(sessionsFilePath));
        return services;
    }

    public static IServiceCollection AddTelegramMessaging(this IServiceCollection services, TelegramOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<TelegramGateway>();
        services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<TelegramGateway>());
        services.AddSingleton<IIncomingMessageListener>(sp => sp.GetRequiredService<TelegramGateway>());
        return services;
    }

    public static IServiceCollection AddSystemClock(this IServiceCollection services, string timeZoneId)
    {
        services.AddSingleton<IAgentClock>(sp =>
            new SystemAgentClock(timeZoneId, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SystemAgentClock>>()));
        return services;
    }
}
