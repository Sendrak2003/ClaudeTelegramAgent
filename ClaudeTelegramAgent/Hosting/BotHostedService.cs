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
    AccessList accessList,
    LiveVoiceService liveVoice,
    IWebAppLauncher webApp,
    ILogger<BotHostedService> logger) : BackgroundService
{
    private const string HelpText =
        "Привет! Я ИИ-ассистент: отвечаю на вопросы, работаю с файлами и страницами, ставлю напоминания и могу озвучивать ответы.\n\n" +
        "*Что умею*\n" +
        "— писать и править тексты, объяснять, считать, сравнивать (в том числе таблицами)\n" +
        "— искать в интернете и читать страницы\n" +
        "— принимать голосовые, фото и файлы\n" +
        "— напоминать о делах в нужное время\n\n" +
        "*Команды*\n" +
        "/new — новый чат, /chats — список, /switch N — переключиться\n" +
        "/reset — сбросить контекст, /status — состояние\n" +
        "/voice — голосовые ответы, /live — живая озвучка в плеере\n\n" +
        "Просто напишите, что нужно, или выберите пример ниже.";

    private static readonly string[] HelpButtons = ["Покажи, что ты умеешь, на примерах", "Напомни через час выпить воды", "Сравни Python и C# таблицей"];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTimeOffset> _deniedAt = new();

    /// <summary>Чужому пользователю отвечаем один раз в час: что доступ закрыт и какой у него id, чтобы владелец мог его добавить.</summary>
    private async Task ReplyAccessClosedAsync(IncomingMessage message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_deniedAt.TryGetValue(message.UserId, out var last) && now - last < TimeSpan.FromHours(1))
        {
            return;
        }

        _deniedAt[message.UserId] = now;
        try
        {
            await messageSender.SendAsync(
                message.ChatId,
                $"Привет! Это приватный ИИ-ассистент, доступ пока закрыт.\nЧтобы получить доступ, отправьте владельцу ваш id: `{message.UserId}`",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send access-closed reply to {UserId}", message.UserId);
        }

        var who = message.Username is { Length: > 0 } name ? $"@{name}" : "без username";
        var preview = message.Text.Length > 100 ? message.Text[..100] + "…" : message.Text;
        IReadOnlyList<IReadOnlyList<(string Label, string Action, string Argument)>> rows =
        [
            [("✅ Разрешить", "access", $"allow:{message.UserId}"), ("🚫 Заблокировать", "access", $"deny:{message.UserId}")],
        ];
        foreach (var ownerId in accessList.Owners)
        {
            try
            {
                await messageSender.SendWithActionsAsync(
                    new ChatId(ownerId),
                    $"Новый пользователь пытается написать боту.\nid: `{message.UserId}`\nusername: {who}\nСообщение: {preview}",
                    rows,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to notify owner {OwnerId} about {UserId}", ownerId, message.UserId);
            }
        }
    }

    /// <summary>«/access allow ID» и «/access deny ID» (приходят от кнопок в уведомлении): только владельцы из Agent__AllowedUserIds.</summary>
    private async Task HandleAccessAsync(IncomingMessage message, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!accessList.IsOwner(message.UserId) || parts.Length != 3 || !long.TryParse(parts[2], out var targetId))
        {
            return;
        }

        if (parts[1].Equals("allow", StringComparison.OrdinalIgnoreCase))
        {
            accessList.Allow(targetId);
            await messageSender.SendAsync(message.ChatId, $"✅ Доступ выдан: `{targetId}`.", ct);
            await TrySendAsync(new ChatId(targetId), "Доступ открыт! Нажмите /start, чтобы начать.", ct);
        }
        else if (parts[1].Equals("deny", StringComparison.OrdinalIgnoreCase))
        {
            accessList.Block(targetId);
            await messageSender.SendAsync(message.ChatId, $"🚫 Заблокирован: `{targetId}`. Его сообщения игнорируются.", ct);
        }
    }

    private async Task TrySendAsync(ChatId chatId, string text, CancellationToken ct)
    {
        try
        {
            await messageSender.SendAsync(chatId, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send message to {ChatId}", chatId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        listener.OnMessageReceived(HandleMessageAsync);
        await listener.StartAsync(stoppingToken);
    }

    private async Task HandleMessageAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!accessList.IsAllowed(message.UserId))
        {
            logger.LogWarning("Ignoring message from unauthorized user {UserId} ({Username})", message.UserId, message.Username);
            if (!accessList.IsBlocked(message.UserId))
            {
                await ReplyAccessClosedAsync(message, ct);
            }

            return;
        }

        var text = message.Text.Trim();

        if (text.StartsWith("/access ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAccessAsync(message, text, ct);
            return;
        }

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
            case "/help":
                await messageSender.SendWithButtonsAsync(message.ChatId, HelpText, HelpButtons, ct);
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
