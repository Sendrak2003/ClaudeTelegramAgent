using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface ISessionStore
{
    string? GetActiveSessionId(ChatId chatId);

    void SetActiveSessionId(ChatId chatId, string sessionId);

    void ClearActiveSession(ChatId chatId);

    int CreateChat(ChatId chatId);

    bool SwitchChat(ChatId chatId, int slotNumber);

    IReadOnlyList<ChatSlotInfo> ListChats(ChatId chatId);
}
