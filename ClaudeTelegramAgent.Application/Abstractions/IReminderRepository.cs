using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IReminderRepository
{
    Task<Reminder> AddAsync(Reminder reminder, CancellationToken ct);

    Task<IReadOnlyList<Reminder>> ListActiveAsync(ChatId chatId, CancellationToken ct);

    Task<bool> CancelAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<Reminder>> PopDueAsync(DateTimeOffset nowUtc, CancellationToken ct);
}
