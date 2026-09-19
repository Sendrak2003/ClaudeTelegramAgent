namespace ClaudeTelegramAgent.Application.Contracts;

public sealed record ChatSlotInfo(int Number, bool IsActive, bool HasSession, DateTimeOffset CreatedAtUtc);
