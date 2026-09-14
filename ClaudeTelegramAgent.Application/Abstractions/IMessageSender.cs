using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IMessageSender
{
    Task SendAsync(ChatId chatId, string text, CancellationToken ct);

    Task SendTypingAsync(ChatId chatId, CancellationToken ct);
}
