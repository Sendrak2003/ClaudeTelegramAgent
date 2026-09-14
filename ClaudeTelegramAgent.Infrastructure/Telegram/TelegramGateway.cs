using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using MessageOrigin = Telegram.Bot.Types.MessageOrigin;
using MessageOriginChannel = Telegram.Bot.Types.MessageOriginChannel;
using MessageOriginChat = Telegram.Bot.Types.MessageOriginChat;
using MessageOriginHiddenUser = Telegram.Bot.Types.MessageOriginHiddenUser;
using MessageOriginUser = Telegram.Bot.Types.MessageOriginUser;

namespace ClaudeTelegramAgent.Infrastructure.Telegram;

public sealed class TelegramGateway : IMessageSender, IIncomingMessageListener
{
    private const int MaxMessageLength = 4000;

    private readonly TelegramBotClient _bot;
    private readonly ILogger<TelegramGateway> _logger;
    private Func<IncomingMessage, CancellationToken, Task>? _handler;

    public TelegramGateway(TelegramOptions options, ILogger<TelegramGateway> logger)
    {
        _bot = new TelegramBotClient(options.BotToken);
        _logger = logger;
    }

    public void OnMessageReceived(Func<IncomingMessage, CancellationToken, Task> handler) => _handler = handler;

    public async Task StartAsync(CancellationToken ct)
    {
        var me = await _bot.GetMe(ct);
        _logger.LogInformation("Connected to Telegram as @{Username}", me.Username);

        _bot.OnMessage += async (message, updateType) =>
        {
            // Пересланные фото/видео/документы несут текст в Caption, а не в Text —
            // без этого такие сообщения молча игнорировались.
            var text = message.Text ?? message.Caption;
            if (text is null)
            {
                return;
            }

            if (_handler is null)
            {
                return;
            }

            var forwardLabel = DescribeForwardOrigin(message.ForwardOrigin);
            if (forwardLabel is not null)
            {
                text = $"[Переслано от: {forwardLabel}]\n{text}";
            }

            var incoming = new IncomingMessage(
                new ChatId(message.Chat.Id),
                message.From?.Id ?? 0,
                message.From?.Username,
                text);

            await _handler(incoming, ct);
        };

        _bot.OnError += (exception, source) =>
        {
            _logger.LogError(exception, "Telegram polling error from {Source}", source);
            return Task.CompletedTask;
        };
    }

    public async Task SendAsync(ChatId chatId, string text, CancellationToken ct)
    {
        foreach (var chunk in SplitIntoChunks(text, MaxMessageLength))
        {
            try
            {
                // Legacy Markdown, а не MarkdownV2: не требует экранирования обычной
                // пунктуации (. - ( ) ! и т.д.), которой полно в обычном тексте ответа.
                await _bot.SendMessage(chatId.Value, chunk, parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                // Модель могла сгенерировать несбалансированную markdown-разметку
                // (одиночная * или ` и т.п.) — не теряем сообщение, шлём как есть.
                _logger.LogWarning(ex, "Markdown parse failed for chat {ChatId}, falling back to plain text", chatId);
                await _bot.SendMessage(chatId.Value, chunk, cancellationToken: ct);
            }
        }
    }

    public async Task SendTypingAsync(ChatId chatId, CancellationToken ct)
    {
        try
        {
            await _bot.SendChatAction(chatId.Value, ChatAction.Typing, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to send typing indicator to chat {ChatId}", chatId);
        }
    }

    private static string? DescribeForwardOrigin(MessageOrigin? origin) => origin switch
    {
        MessageOriginUser u => u.SenderUser.Username is { } username
            ? $"@{username}"
            : $"{u.SenderUser.FirstName} {u.SenderUser.LastName}".Trim(),
        MessageOriginHiddenUser hu => hu.SenderUserName,
        MessageOriginChat c => c.SenderChat.Title ?? c.SenderChat.Username ?? "чат",
        MessageOriginChannel ch => ch.Chat.Title ?? ch.Chat.Username ?? "канал",
        _ => null,
    };

    private static IEnumerable<string> SplitIntoChunks(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        for (var offset = 0; offset < text.Length; offset += maxLength)
        {
            var length = Math.Min(maxLength, text.Length - offset);
            yield return text.Substring(offset, length);
        }
    }
}
