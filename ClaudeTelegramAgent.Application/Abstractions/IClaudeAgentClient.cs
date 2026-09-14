using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IClaudeAgentClient
{
    Task<ClaudeAgentReply> SendAsync(ChatId chatId, string prompt, string? sessionId, CancellationToken ct);
}
