using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Domain;
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

        var text = message.Text.Trim();

        if (text.StartsWith("/switch", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSwitchAsync(message.ChatId, text, ct);
            return;
        }

        switch (text)
        {
            case "/start":
                await messageSender.SendAsync(message.ChatId, "Привет! Я на связи и готов помочь.", ct);
                return;
            case "/reset":
                conversation.Reset(message.ChatId);
                await messageSender.SendAsync(message.ChatId, "Контекст текущего чата сброшен.", ct);
                return;
            case "/new":
                var newSlot = conversation.NewChat(message.ChatId);
                await messageSender.SendAsync(message.ChatId, $"Создан новый чат #{newSlot}, переключился на него.", ct);
                return;
            case "/chats":
                await HandleListChatsAsync(message.ChatId, ct);
                return;
            case "/status":
                var sessionId = conversation.GetActiveSessionId(message.ChatId);
                var statusText = $"Рабочая папка: {claudeOptions.WorkDir}\nРежим доступа: {claudeOptions.PermissionMode}\nАктивная сессия: {sessionId ?? "нет"}";
                await messageSender.SendAsync(message.ChatId, statusText, ct);
                return;
        }

        await conversation.HandleMessageAsync(message.ChatId, message.Text, ct);
    }

    private async Task HandleListChatsAsync(ChatId chatId, CancellationToken ct)
    {
        var chats = conversation.ListChats(chatId);
        var lines = chats.Select(c =>
            $"{(c.IsActive ? "→ " : "  ")}#{c.Number}{(c.IsActive ? " (текущий)" : "")} — {(c.HasSession ? "есть контекст" : "пусто")}");

        await messageSender.SendAsync(chatId, string.Join("\n", lines), ct);
    }

    private async Task HandleSwitchAsync(ChatId chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var slotNumber))
        {
            await messageSender.SendAsync(chatId, "Использование: /switch <номер чата>. Список чатов — /chats.", ct);
            return;
        }

        if (conversation.SwitchChat(chatId, slotNumber))
        {
            await messageSender.SendAsync(chatId, $"Переключился на чат #{slotNumber}.", ct);
        }
        else
        {
            await messageSender.SendAsync(chatId, $"Чат #{slotNumber} не найден. Список чатов — /chats.", ct);
        }
    }
}
