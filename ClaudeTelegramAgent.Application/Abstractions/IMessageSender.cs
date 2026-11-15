using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IMessageSender
{
    Task SendAsync(ChatId chatId, string text, CancellationToken ct);

    Task SendTypingAsync(ChatId chatId, CancellationToken ct);

    /// <summary>Отправляет голосовое сообщение (OGG/Opus).</summary>
    Task SendVoiceAsync(ChatId chatId, byte[] oggOpus, CancellationToken ct);

    /// <summary>
    /// Показывает частичный текст как живой черновик (sendMessageDraft): Telegram плавно дорисовывает его по мере обновлений.
    /// Повторные вызовы с тем же <paramref name="draftId"/> обновляют один и тот же черновик; итоговое сообщение
    /// отправляется отдельно через <see cref="SendAsync"/>. Сбой (сеть, лимиты) не бросает исключение.
    /// </summary>
    /// <param name="iconEmojiId">Id анимированного кастомного эмодзи перед текстом статуса; игнорируется, если отправка кастомных эмодзи выключена.</param>
    Task StreamDraftAsync(ChatId chatId, int draftId, string text, CancellationToken ct, string? iconEmojiId = null);
}
