using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface ISessionStore
{
    string? Get(ChatId chatId);

    void Set(ChatId chatId, string sessionId);

    void Clear(ChatId chatId);
}
