namespace ClaudeTelegramAgent.Application.Contracts;

public sealed record ClaudeAgentReply(string Text, string? SessionId, bool Success);
