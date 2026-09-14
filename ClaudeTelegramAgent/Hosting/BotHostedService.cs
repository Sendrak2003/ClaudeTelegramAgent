using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Infrastructure.Claude;
using ClaudeTelegramAgent.Options;

namespace ClaudeTelegramAgent.Hosting;

public sealed class BotHostedService(
    IIncomingMessageListener listener,
    IMessageSender messageSender,
    AgentConversationService conversation,
    ClaudeCliOptions claudeOptions,
    AccessControlOptions access,
    ILogger<BotHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        listener.OnMessageReceived(HandleMessageAsync);
        await listener.StartAsync(stoppingToken);
    }

    private async Task HandleMessageAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!access.AllowedUserIds.Contains(message.UserId))
        {
            logger.LogWarning("Ignoring message from unauthorized user {UserId} ({Username})", message.UserId, message.Username);
            return;
        }

        switch (message.Text.Trim())
        {
            case "/start":
                await messageSender.SendAsync(message.ChatId, "Привет! Я на связи и готов помочь.", ct);
                return;
            case "/reset":
                conversation.Reset(message.ChatId);
                await messageSender.SendAsync(message.ChatId, "Контекст диалога сброшен.", ct);
                return;
            case "/status":
                var sessionId = conversation.GetActiveSessionId(message.ChatId);
                var statusText = $"Рабочая папка: {claudeOptions.WorkDir}\nРежим доступа: {claudeOptions.PermissionMode}\nАктивная сессия: {sessionId ?? "нет"}";
                await messageSender.SendAsync(message.ChatId, statusText, ct);
                return;
        }

        await conversation.HandleMessageAsync(message.ChatId, message.Text, ct);
    }
}
