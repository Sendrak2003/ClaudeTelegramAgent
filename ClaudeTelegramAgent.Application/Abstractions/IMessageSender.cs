using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IMessageSender
{
    Task SendAsync(ChatId chatId, string text, CancellationToken ct);

    Task SendTypingAsync(ChatId chatId, CancellationToken ct);

    /// <summary>Отправляет обычный текст без разметки и возвращает id сообщения — его потом правят через <see cref="EditDraftAsync"/>.</summary>
    Task<int> SendDraftAsync(ChatId chatId, string text, CancellationToken ct);

    /// <summary>Заменяет текст черновика. Сбои вроде лимита запросов или «не изменено» не бросают исключение.</summary>
    Task EditDraftAsync(ChatId chatId, int messageId, string text, CancellationToken ct);

    /// <summary>Превращает черновик в готовый ответ с форматированием; текст сверх лимита Telegram уходит следующими сообщениями.</summary>
    Task FinalizeDraftAsync(ChatId chatId, int messageId, string text, CancellationToken ct);
}
