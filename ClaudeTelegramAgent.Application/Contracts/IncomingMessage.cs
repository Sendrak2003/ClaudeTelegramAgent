using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Contracts;

/// <param name="AudioPath">Путь к скачанному голосовому/аудио, если в сообщении есть речь для распознавания.</param>
public sealed record IncomingMessage(ChatId ChatId, long UserId, string? Username, string Text, string? AudioPath = null);
