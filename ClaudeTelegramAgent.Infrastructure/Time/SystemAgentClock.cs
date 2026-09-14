using ClaudeTelegramAgent.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Infrastructure.Time;

public sealed class SystemAgentClock : IAgentClock
{
    private readonly TimeZoneInfo _timeZone;

    public SystemAgentClock(string timeZoneId, ILogger<SystemAgentClock> logger)
    {
        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning("Time zone '{TimeZoneId}' was not found. Falling back to UTC.", timeZoneId);
            _timeZone = TimeZoneInfo.Utc;
        }
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset NowLocal => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);
}
