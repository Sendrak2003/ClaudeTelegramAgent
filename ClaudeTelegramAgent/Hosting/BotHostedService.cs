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
    LiveVoiceService liveVoice,
    IWebAppLauncher webApp,
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

        if (text.Equals("/live", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLiveAsync(message.ChatId, ct);
            return;
        }

        if (TryParseAddonCommand(text, out var addon, out var switchTo))
        {
            await HandleAddonAsync(message.ChatId, addon, switchTo, ct);
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

        await conversation.HandleMessageAsync(message.ChatId, message.Text, message.AudioPath, ct);
    }

    /// <summary>«/voice», «/voice on», «/voice off» (так же для любого дополнения); switchTo null — только показать состояние.</summary>
    private bool TryParseAddonCommand(string text, out IReplyAddon addon, out bool? switchTo)
    {
        addon = null!;
        switchTo = null;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || !parts[0].StartsWith('/'))
        {
            return false;
        }

        var found = conversation.Addons.FirstOrDefault(a => string.Equals(parts[0][1..], a.Name, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return false;
        }

        addon = found;
        if (parts.Length == 2)
        {
            switchTo = parts[1].ToLowerInvariant() switch
            {
                "on" or "вкл" => true,
                "off" or "выкл" => false,
                _ => null,
            };
        }

        return true;
    }

    /// <summary>«/live»: кнопка, открывающая плеер с живой озвучкой; пока он открыт, ответы озвучиваются в нём по мере написания.</summary>
    private async Task HandleLiveAsync(ChatId chatId, CancellationToken ct)
    {
        if (!liveVoice.Available)
        {
            await messageSender.SendAsync(chatId, "/live на сервере отключено.", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(liveVoice.PublicUrl))
        {
            await messageSender.SendAsync(chatId, "Адрес плеера не задан: нужен публичный HTTPS-адрес сервера в настройке Live__PublicUrl.", ct);
            return;
        }

        var state = conversation.IsAddonEnabled(chatId, liveVoice) ? "включена" : "выключена (/live on)";
        await webApp.SendWebAppButtonAsync(
            chatId,
            $"Живая озвучка {state}. Открой плеер и оставь его открытым: ответы будут звучать по мере написания. Выключить: /live off.",
            "Открыть плеер",
            liveVoice.PublicUrl.TrimEnd('/') + "/live/",
            ct);
    }

    private async Task HandleAddonAsync(ChatId chatId, IReplyAddon addon, bool? switchTo, CancellationToken ct)
    {
        if (!addon.Available)
        {
            await messageSender.SendAsync(chatId, $"/{addon.Name} на сервере отключено.", ct);
            return;
        }

        if (switchTo is { } enabled)
        {
            conversation.SetAddon(chatId, addon, enabled);
        }

        var state = conversation.IsAddonEnabled(chatId, addon) ? "включено" : "выключено";
        await messageSender.SendAsync(chatId, $"/{addon.Name}: {state}. Переключить: /{addon.Name} on или /{addon.Name} off.", ct);
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
