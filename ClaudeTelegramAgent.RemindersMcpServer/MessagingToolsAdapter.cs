using System.ComponentModel;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace ClaudeTelegramAgent.RemindersMcpServer;

[McpServerToolType]
public sealed class MessagingToolsAdapter
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    [McpServerTool(Name = "send_file")]
    [Description("Отправляет локальный файл (например, скриншот) пользователю в текущий Telegram-чат.")]
    public static async Task<string> SendFile(
        ITelegramBotClient bot,
        CurrentChatContext chatContext,
        [Description("Путь к файлу на диске (абсолютный или относительно рабочей папки)")] string path,
        [Description("Необязательная подпись к файлу")] string? caption = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return $"Файл не найден: {path}";
        }

        await using var stream = File.OpenRead(path);
        var inputFile = InputFile.FromStream(stream, Path.GetFileName(path));
        var extension = Path.GetExtension(path);

        if (ImageExtensions.Contains(extension))
        {
            await bot.SendPhoto(chatContext.ChatId.Value, inputFile, caption: caption, cancellationToken: ct);
        }
        else
        {
            await bot.SendDocument(chatContext.ChatId.Value, inputFile, caption: caption, cancellationToken: ct);
        }

        return $"Файл {Path.GetFileName(path)} отправлен пользователю в Telegram.";
    }
}
