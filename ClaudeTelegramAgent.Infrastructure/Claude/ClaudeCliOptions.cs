namespace ClaudeTelegramAgent.Infrastructure.Claude;

public sealed record ClaudeCliOptions(
    string WorkDir,
    string Bin,
    string PermissionMode,
    int TimeoutSeconds,
    ClaudeAuthMode Auth,
    string? ApiKey);
