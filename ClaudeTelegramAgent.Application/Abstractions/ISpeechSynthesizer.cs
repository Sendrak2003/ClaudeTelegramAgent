namespace ClaudeTelegramAgent.Application.Abstractions;

public interface ISpeechSynthesizer
{
    /// <summary>
    /// Озвучивает текст и возвращает голосовое сообщение в формате OGG/Opus (его принимает Telegram sendVoice).
    /// При сбое (сервер озвучки недоступен, не уложился во время) бросает исключение с понятным текстом причины.
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, CancellationToken ct);

    /// <summary>Готовая озвучка этого текста, если она уже есть в кэше; иначе null. Не запускает синтез.</summary>
    byte[]? TryGetCached(string text);
}
