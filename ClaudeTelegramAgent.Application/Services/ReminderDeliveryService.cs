using ClaudeTelegramAgent.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

public sealed class ReminderDeliveryService(
    IReminderRepository repository,
    IMessageSender messageSender,
    IAgentClock clock,
    ILogger<ReminderDeliveryService> logger)
{
    public async Task DeliverDueAsync(CancellationToken ct)
    {
        var nowUtc = clock.UtcNow;
        var due = await repository.PopDueAsync(nowUtc, ct);
        logger.LogInformation("DeliverDueAsync at {NowUtc:O}: {Count} due reminder(s)", nowUtc, due.Count);

        foreach (var reminder in due)
        {
            try
            {
                await messageSender.SendAsync(reminder.ChatId, reminder.Message, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deliver reminder {ReminderId} to chat {ChatId}", reminder.Id, reminder.ChatId);
            }
        }
    }
}
