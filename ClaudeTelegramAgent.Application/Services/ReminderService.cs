using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Services;

public sealed class ReminderService(IReminderRepository repository)
{
    public Task<Reminder> AddAsync(ChatId chatId, DateTimeOffset remindAtUtc, string message, CancellationToken ct)
    {
        var reminder = Reminder.Create(chatId, remindAtUtc, message, DateTimeOffset.UtcNow);
        return repository.AddAsync(reminder, ct);
    }

    public Task<IReadOnlyList<Reminder>> ListActiveAsync(ChatId chatId, CancellationToken ct) =>
        repository.ListActiveAsync(chatId, ct);

    public Task<bool> CancelAsync(long id, CancellationToken ct) =>
        repository.CancelAsync(id, ct);
}
