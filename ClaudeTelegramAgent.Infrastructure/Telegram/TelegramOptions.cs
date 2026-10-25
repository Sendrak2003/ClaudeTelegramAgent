namespace ClaudeTelegramAgent.Infrastructure.Telegram;

/// <param name="UseCustomEmoji">
/// Показывать в живом статусе анимированные кастомные эмодзи. Telegram разрешает их только ботам с купленным
/// на Fragment дополнительным именем — у остальных они молча превращаются в обычный символ, поэтому по умолчанию выключено.
/// </param>
public sealed record TelegramOptions(string BotToken, string IncomingFilesDirectory, bool UseCustomEmoji = false);
