namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IAgentClock
{
    DateTimeOffset UtcNow { get; }

    DateTimeOffset NowLocal { get; }
}
