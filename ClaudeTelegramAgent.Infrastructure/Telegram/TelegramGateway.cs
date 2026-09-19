using System.Text;
using System.Text.RegularExpressions;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using BotCommand = Telegram.Bot.Types.BotCommand;
using FileBase = Telegram.Bot.Types.FileBase;
using Message = Telegram.Bot.Types.Message;
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
    private readonly string _incomingFilesDirectory;
    private Func<IncomingMessage, CancellationToken, Task>? _handler;

    public TelegramGateway(TelegramOptions options, ILogger<TelegramGateway> logger)
    {
        _bot = new TelegramBotClient(options.BotToken);
        _logger = logger;
        _incomingFilesDirectory = options.IncomingFilesDirectory;
    }

    public void OnMessageReceived(Func<IncomingMessage, CancellationToken, Task> handler) => _handler = handler;

    public async Task StartAsync(CancellationToken ct)
    {
        var me = await _bot.GetMe(ct);
        _logger.LogInformation("Connected to Telegram as @{Username}", me.Username);

        // Регистрация команд включает у Telegram кнопку меню рядом с полем ввода —
        // без этого вызова список команд нигде в интерфейсе не появляется.
        await _bot.SetMyCommands(
            [
                new BotCommand("start", "Начать общение с ботом"),
                new BotCommand("new", "Создать новый чат"),
                new BotCommand("chats", "Список чатов"),
                new BotCommand("switch", "Переключиться на чат: /switch N"),
                new BotCommand("reset", "Сбросить контекст текущего чата"),
                new BotCommand("status", "Статус агента"),
            ],
            cancellationToken: ct);

        _bot.OnMessage += async (message, updateType) =>
        {
            // Пересланные фото/видео/документы несут текст в Caption, а не в Text —
            // без этого такие сообщения молча игнорировались.
            var text = message.Text ?? message.Caption;

            string? filePath = null;
            try
            {
                filePath = await DownloadAttachmentAsync(message, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download attachment from chat {ChatId}", message.Chat.Id);
            }

            if (text is null && filePath is null)
            {
                return;
            }

            if (_handler is null)
            {
                return;
            }

            text ??= string.Empty;

            if (filePath is not null)
            {
                text = $"[Пользователь прислал файл: {filePath}]\n{text}";
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
                // HTML, не Markdown/MarkdownV2: только так Telegram даёт блокам кода
                // подсветку языка и кнопку "Copy" (```lang ... ``` → <pre><code class="language-lang">).
                // Конвертируем сами — MarkdownV2 потребовал бы экранировать почти всю пунктуацию
                // в произвольном тексте модели, что ненадёжно.
                var html = ConvertMarkdownToTelegramHtml(chunk);
                await _bot.SendMessage(chatId.Value, html, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                // Конвертер мог собрать некорректный HTML из кривой разметки модели —
                // не теряем сообщение, шлём как есть без форматирования.
                _logger.LogWarning(ex, "HTML parse failed for chat {ChatId}, falling back to plain text", chatId);
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

    private async Task<string?> DownloadAttachmentAsync(Message message, CancellationToken ct)
    {
        FileBase? file;
        string extension;

        if (message.Photo is { Length: > 0 } photoSizes)
        {
            file = photoSizes[^1]; // последний элемент — самое большое разрешение
            extension = ".jpg";
        }
        else if (message.Document is { } document)
        {
            file = document;
            extension = Path.GetExtension(document.FileName ?? string.Empty) is { Length: > 1 } ext
                ? ext
                : GuessExtensionFromMimeType(document.MimeType);
        }
        else if (message.Voice is { } voice)
        {
            file = voice;
            extension = ".ogg";
        }
        else if (message.VideoNote is { } videoNote)
        {
            file = videoNote;
            extension = ".mp4";
        }
        else if (message.Video is { } video)
        {
            file = video;
            extension = ".mp4";
        }
        else if (message.Audio is { } audio)
        {
            file = audio;
            extension = Path.GetExtension(audio.FileName ?? string.Empty) is { Length: > 1 } ext
                ? ext
                : GuessExtensionFromMimeType(audio.MimeType);
        }
        else
        {
            return null;
        }

        Directory.CreateDirectory(_incomingFilesDirectory);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{file.FileUniqueId}{extension}";
        var path = Path.Combine(_incomingFilesDirectory, fileName);

        await using var stream = File.Create(path);
        await _bot.GetInfoAndDownloadFile(file.FileId, stream, ct);

        return path;
    }

    private static string GuessExtensionFromMimeType(string? mimeType) =>
        mimeType?.Split('/') is [_, { Length: > 0 } subtype] ? $".{subtype}" : "";

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

    private static readonly Regex CodeBlockRegex = new("```(\\w*)\r?\n([\\s\\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new("`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex BoldDoubleRegex = new(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldSingleRegex = new(@"\*([^*\n]+)\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new("_([^_\n]+)_", RegexOptions.Compiled);

    private static string ConvertMarkdownToTelegramHtml(string text)
    {
        var result = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in CodeBlockRegex.Matches(text))
        {
            result.Append(ConvertInlineMarkdown(text[lastIndex..match.Index]));

            var language = match.Groups[1].Value;
            var code = EscapeHtml(match.Groups[2].Value.TrimEnd('\n'));
            result.Append(string.IsNullOrEmpty(language)
                ? $"<pre>{code}</pre>"
                : $"<pre><code class=\"language-{EscapeHtml(language)}\">{code}</code></pre>");

            lastIndex = match.Index + match.Length;
        }

        result.Append(ConvertInlineMarkdown(text[lastIndex..]));
        return result.ToString();
    }

    private static string ConvertInlineMarkdown(string text)
    {
        // Прячем inline-код за плейсхолдерами, чтобы */_ внутри него не считались
        // разметкой жирного/курсива на следующих шагах.
        var codeSpans = new List<string>();
        text = InlineCodeRegex.Replace(text, m =>
        {
            codeSpans.Add($"<code>{EscapeHtml(m.Groups[1].Value)}</code>");
            return $" {codeSpans.Count - 1} ";
        });

        text = EscapeHtml(text);

        text = LinkRegex.Replace(text, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        text = BoldDoubleRegex.Replace(text, "<b>$1</b>");
        text = BoldSingleRegex.Replace(text, "<b>$1</b>");
        text = ItalicRegex.Replace(text, "<i>$1</i>");

        for (var i = 0; i < codeSpans.Count; i++)
        {
            text = text.Replace($" {i} ", codeSpans[i]);
        }

        return text;
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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
