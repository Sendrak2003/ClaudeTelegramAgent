using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.RemindersMcpServer;

public sealed class CurrentChatContext
{
    public ChatId ChatId { get; }

    private CurrentChatContext(ChatId chatId)
    {
        ChatId = chatId;
    }

    public static CurrentChatContext FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("REMINDER_CHAT_ID");
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out var value))
        {
            throw new InvalidOperationException(
                "Environment variable REMINDER_CHAT_ID is not set or is not a valid chat id.");
        }

        return new CurrentChatContext(new ChatId(value));
    }
}
