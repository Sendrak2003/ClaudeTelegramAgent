namespace ClaudeTelegramAgent.Options;

public sealed record AccessControlOptions(IReadOnlySet<long> AllowedUserIds);
