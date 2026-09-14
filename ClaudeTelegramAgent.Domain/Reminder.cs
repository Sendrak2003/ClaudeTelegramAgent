namespace ClaudeTelegramAgent.Domain;

public sealed class Reminder
{
    public long Id { get; private set; }
    public ChatId ChatId { get; }
    public DateTimeOffset RemindAtUtc { get; }
    public string Message { get; }
    public bool Fired { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }

    private Reminder(ChatId chatId, DateTimeOffset remindAtUtc, string message, bool fired, DateTimeOffset createdAtUtc)
    {
        ChatId = chatId;
        RemindAtUtc = remindAtUtc;
        Message = message;
        Fired = fired;
        CreatedAtUtc = createdAtUtc;
    }

    public static Reminder Create(ChatId chatId, DateTimeOffset remindAtUtc, string message, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Reminder message must not be empty.", nameof(message));
        }

        return new Reminder(chatId, remindAtUtc, message, fired: false, createdAtUtc);
    }

    public static Reminder Reconstruct(long id, ChatId chatId, DateTimeOffset remindAtUtc, string message, bool fired, DateTimeOffset createdAtUtc)
    {
        var reminder = new Reminder(chatId, remindAtUtc, message, fired, createdAtUtc);
        reminder.Id = id;
        return reminder;
    }

    public void AssignId(long id)
    {
        if (Id != 0)
        {
            throw new InvalidOperationException($"Reminder already has an assigned id ({Id}).");
        }

        Id = id;
    }

    public void MarkFired() => Fired = true;
}
