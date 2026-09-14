using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Options;

namespace ClaudeTelegramAgent.Hosting;

public sealed class ReminderPollingHostedService(
    ReminderDeliveryService delivery,
    ReminderPollingOptions options,
    ILogger<ReminderPollingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reminder polling started, interval {PollSeconds}s", options.PollSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Reminder polling tick");
                await delivery.DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Reminder polling iteration failed");
            }
        }

        logger.LogInformation("Reminder polling loop exited (stoppingToken cancelled: {Cancelled})", stoppingToken.IsCancellationRequested);
    }
}
