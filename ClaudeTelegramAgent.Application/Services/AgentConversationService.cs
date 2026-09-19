using System.Globalization;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Services;

public sealed class AgentConversationService(
    IClaudeAgentClient claude,
    ISessionStore sessions,
    IMessageSender messageSender,
    IAgentClock clock)
{
    public async Task HandleMessageAsync(ChatId chatId, string text, CancellationToken ct)
    {
        await messageSender.SendTypingAsync(chatId, ct);

        var prompt = $"[Текущее время: {clock.NowLocal.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}]\n{text}";
        var sessionId = sessions.GetActiveSessionId(chatId);

        var reply = await claude.SendAsync(chatId, prompt, sessionId, ct);

        if (!string.IsNullOrEmpty(reply.SessionId))
        {
            sessions.SetActiveSessionId(chatId, reply.SessionId);
        }

        await messageSender.SendAsync(chatId, reply.Text, ct);
    }

    public void Reset(ChatId chatId) => sessions.ClearActiveSession(chatId);

    public string? GetActiveSessionId(ChatId chatId) => sessions.GetActiveSessionId(chatId);

    public int NewChat(ChatId chatId) => sessions.CreateChat(chatId);

    public bool SwitchChat(ChatId chatId, int slotNumber) => sessions.SwitchChat(chatId, slotNumber);

    public IReadOnlyList<ChatSlotInfo> ListChats(ChatId chatId) => sessions.ListChats(chatId);
}
