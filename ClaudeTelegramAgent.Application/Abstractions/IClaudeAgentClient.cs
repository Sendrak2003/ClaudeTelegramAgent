using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IClaudeAgentClient
{
    /// <param name="onProgress">Вызывается по мере работы агента (из фонового потока): текст ответа, вызовы инструментов.</param>
    Task<ClaudeAgentReply> SendAsync(
        ChatId chatId,
        string prompt,
        string? sessionId,
        CancellationToken ct,
        Action<AgentProgress>? onProgress = null);
}
