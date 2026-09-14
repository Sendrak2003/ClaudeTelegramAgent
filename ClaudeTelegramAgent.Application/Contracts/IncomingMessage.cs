using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Contracts;

public sealed record IncomingMessage(ChatId ChatId, long UserId, string? Username, string Text);
