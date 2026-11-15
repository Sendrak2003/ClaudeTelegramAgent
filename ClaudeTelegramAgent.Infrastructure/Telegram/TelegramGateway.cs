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
using CallbackQuery = Telegram.Bot.Types.CallbackQuery;
using FileBase = Telegram.Bot.Types.FileBase;
using InlineKeyboardButton = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton;
using InlineKeyboardMarkup = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
using InputFile = Telegram.Bot.Types.InputFile;
using Message = Telegram.Bot.Types.Message;
using WebAppInfo = Telegram.Bot.Types.WebAppInfo;
using MessageOrigin = Telegram.Bot.Types.MessageOrigin;
using MessageOriginChannel = Telegram.Bot.Types.MessageOriginChannel;
using MessageOriginChat = Telegram.Bot.Types.MessageOriginChat;
using MessageOriginHiddenUser = Telegram.Bot.Types.MessageOriginHiddenUser;
using MessageOriginUser = Telegram.Bot.Types.MessageOriginUser;

namespace ClaudeTelegramAgent.Infrastructure.Telegram;

public sealed class TelegramGateway : IMessageSender, IIncomingMessageListener, IWebAppLauncher
{
    private const int MaxMessageLength = 4000;

    private readonly TelegramBotClient _bot;
    private readonly HttpClient _http = new();
    private readonly string _token;
    private readonly bool _useCustomEmoji;
    private readonly ILogger<TelegramGateway> _logger;
    private readonly string _incomingFilesDirectory;
    private Func<IncomingMessage, CancellationToken, Task>? _handler;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string[]> _buttonLabels = new();

    public TelegramGateway(TelegramOptions options, ILogger<TelegramGateway> logger)
    {
        _bot = new TelegramBotClient(options.BotToken);
        _token = options.BotToken;
        _useCustomEmoji = options.UseCustomEmoji;
        _logger = logger;
        _incomingFilesDirectory = options.IncomingFilesDirectory;
    }

    public void OnMessageReceived(Func<IncomingMessage, CancellationToken, Task> handler) => _handler = handler;

    public async Task StartAsync(CancellationToken ct)
    {
        var me = await _bot.GetMe(ct);
        _logger.LogInformation("Connected to Telegram as @{Username}", me.Username);

        await _bot.SetMyCommands(
            [
                new BotCommand("start", "Начать общение с ботом"),
                new BotCommand("help", "Что умеет бот"),
                new BotCommand("new", "Создать новый чат"),
                new BotCommand("chats", "Список чатов"),
                new BotCommand("switch", "Переключиться на чат: /switch N"),
                new BotCommand("reset", "Сбросить контекст текущего чата"),
                new BotCommand("status", "Статус агента"),
                new BotCommand("live", "Живая озвучка: открыть плеер"),
            ],
            cancellationToken: ct);

        try
        {
            await _bot.SetMyDescription(
                "ИИ-ассистент в Telegram: отвечает на вопросы, пишет и правит тексты, ищет в интернете, читает файлы и страницы, понимает голосовые, ставит напоминания и озвучивает ответы.\n\nНажмите «Start», чтобы начать.",
                cancellationToken: ct);
            await _bot.SetMyShortDescription("ИИ-ассистент: вопросы, тексты, поиск, голос, напоминания", cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set bot description");
        }

        _bot.OnMessage += async (message, updateType) =>
        {
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

            var audioPath = filePath is not null
                && (message.Voice is not null || message.Audio is not null || message.VideoNote is not null)
                ? filePath
                : null;

            var incoming = new IncomingMessage(
                new ChatId(message.Chat.Id),
                message.From?.Id ?? 0,
                message.From?.Username,
                text,
                audioPath);

            await _handler(incoming, ct);
        };

        _bot.OnUpdate += async update =>
        {
            if (update.CallbackQuery is { } query)
            {
                await HandleCallbackAsync(query, ct);
            }
        };

        _bot.OnError += (exception, source) =>
        {
            _logger.LogError(exception, "Telegram polling error from {Source}", source);
            return Task.CompletedTask;
        };
    }

    private static readonly Regex TableRegex = new(@"^[ \t]*\|.+\|[ \t]*\r?\n[ \t]*\|?[ \t]*:?-{3,}:?[ \t]*(\|[ \t]*:?-{3,}:?[ \t]*)*\|?[ \t]*(\r?\n|$)", RegexOptions.Multiline | RegexOptions.Compiled);
    private const int MaxRichLength = 30000;

    /// <summary>
    /// Обычный sendMessage таблицы не рисует, их умеют только Rich Messages (Bot API 10.1+): весь ответ уходит как Rich Markdown.
    /// Возвращает false, если таблицы нет или Telegram отклонил запрос, тогда текст отправляется обычным способом.
    /// </summary>
    private async Task<bool> TrySendRichAsync(ChatId chatId, string text, IReadOnlyList<string>? buttons, CancellationToken ct)
    {
        if (text.Length > MaxRichLength || !TableRegex.IsMatch(text))
        {
            return false;
        }

        try
        {
            var fields = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.Value.ToString(),
                ["rich_message"] = System.Text.Json.JsonSerializer.Serialize(new { markdown = text }),
            };

            if (buttons is { Count: > 0 })
            {
                fields["reply_markup"] = System.Text.Json.JsonSerializer.Serialize(new
                {
                    inline_keyboard = buttons.Select((label, i) => new[] { new { text = label, callback_data = $"opt:{i}" } }),
                });
            }

            using var content = new FormUrlEncodedContent(fields);
            using var response = await _http.PostAsync($"https://api.telegram.org/bot{_token}/sendRichMessage", content, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("sendRichMessage rejected for chat {ChatId}: {Status} {Body}", chatId, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("sendRichMessage failed for chat {ChatId}: {ErrorType}", chatId, ex.GetType().Name);
        }

        return false;
    }

    public async Task SendAsync(ChatId chatId, string text, CancellationToken ct)
    {
        if (await TrySendRichAsync(chatId, text, null, ct))
        {
            return;
        }

        foreach (var chunk in SplitIntoChunks(text, MaxMessageLength))
        {
            try
            {
                var html = ConvertMarkdownToTelegramHtml(chunk);
                await _bot.SendMessage(chatId.Value, html, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(ex, "HTML parse failed for chat {ChatId}, falling back to plain text", chatId);
                await _bot.SendMessage(chatId.Value, chunk, cancellationToken: ct);
            }
        }
    }

    public async Task SendWithButtonsAsync(ChatId chatId, string text, IReadOnlyList<string> buttons, CancellationToken ct)
    {
        _buttonLabels[chatId.Value] = buttons.ToArray();

        if (await TrySendRichAsync(chatId, text, buttons, ct))
        {
            return;
        }

        var chunks = SplitIntoChunks(text, MaxMessageLength).ToList();
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            await SendAsync(chatId, chunks[i], ct);
        }

        var keyboard = new InlineKeyboardMarkup(buttons.Select((label, index) =>
            new[] { InlineKeyboardButton.WithCallbackData(label, $"opt:{index}") }));
        var last = chunks[^1];

        try
        {
            await _bot.SendMessage(chatId.Value, ConvertMarkdownToTelegramHtml(last), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "HTML parse failed for chat {ChatId}, falling back to plain text", chatId);
            await _bot.SendMessage(chatId.Value, last, replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    public async Task SendWithActionsAsync(ChatId chatId, string text, IReadOnlyList<IReadOnlyList<(string Label, string Action, string Argument)>> rows, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(rows.Select(row =>
            row.Select(b => InlineKeyboardButton.WithCallbackData(b.Label, $"act:{b.Action}:{b.Argument}")).ToArray()));

        try
        {
            await _bot.SendMessage(chatId.Value, ConvertMarkdownToTelegramHtml(text), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
        {
            await _bot.SendMessage(chatId.Value, text, replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    /// <summary>Нажатие кнопки: убираем клавиатуру и передаём подпись агенту как обычное сообщение пользователя.</summary>
    private async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message?.Chat.Id;
        string? label = null;

        if (query.Data is { } actData && actData.StartsWith("act:", StringComparison.Ordinal))
        {
            var parts = actData.Split(':', 3);
            if (parts.Length == 3 && chatId is not null)
            {
                label = $"/{parts[1]} {parts[2].Replace(':', ' ')}";
            }
        }
        else if (chatId is { } id
            && query.Data is { } data && data.StartsWith("opt:", StringComparison.Ordinal)
            && int.TryParse(data.AsSpan(4), out var index)
            && _buttonLabels.TryGetValue(id, out var labels)
            && index >= 0 && index < labels.Length)
        {
            label = labels[index];
        }

        try
        {
            await _bot.AnswerCallbackQuery(query.Id, label is null ? "Кнопка устарела, напишите текстом." : null, cancellationToken: ct);
            if (label is not null && query.Message is { } message)
            {
                await _bot.EditMessageReplyMarkup(message.Chat.Id, message.Id, replyMarkup: null, cancellationToken: ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to acknowledge button press in chat {ChatId}", chatId);
        }

        if (label is null || _handler is null || chatId is null)
        {
            return;
        }

        await _handler(new IncomingMessage(new ChatId(chatId.Value), query.From.Id, query.From.Username, label, null), ct);
    }

    public async Task StreamDraftAsync(ChatId chatId, int draftId, string text, CancellationToken ct, string? iconEmojiId = null)
    {
        try
        {
            var fields = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.Value.ToString(),
                ["draft_id"] = draftId.ToString(),
                ["text"] = text.Length <= MaxMessageLength ? text : text[..MaxMessageLength],
            };

            if (_useCustomEmoji && iconEmojiId is not null)
            {
                fields["text"] = "💭 " + fields["text"];
                fields["entities"] = $"[{{\"type\":\"custom_emoji\",\"offset\":0,\"length\":2,\"custom_emoji_id\":\"{iconEmojiId}\"}}]";
            }

            using var content = new FormUrlEncodedContent(fields);

            using var response = await _http.PostAsync($"https://api.telegram.org/bot{_token}/sendMessageDraft", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Draft update rejected for chat {ChatId}: {Status}", chatId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Draft update failed for chat {ChatId}: {ErrorType}", chatId, ex.GetType().Name);
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

    public async Task SendVoiceAsync(ChatId chatId, byte[] oggOpus, CancellationToken ct)
    {
        using var stream = new MemoryStream(oggOpus);
        await _bot.SendVoice(chatId.Value, InputFile.FromStream(stream, "voice.ogg"), cancellationToken: ct);
    }

    public async Task SendWebAppButtonAsync(ChatId chatId, string text, string buttonText, string url, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp(buttonText, new WebAppInfo(url)));
        await _bot.SendMessage(chatId.Value, text, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task<string?> DownloadAttachmentAsync(Message message, CancellationToken ct)
    {
        FileBase? file;
        string extension;

        if (message.Photo is { Length: > 0 } photoSizes)
        {
            file = photoSizes[^1];
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
