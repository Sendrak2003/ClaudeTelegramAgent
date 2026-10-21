namespace ClaudeTelegramAgent.Application.Abstractions;

public interface ISpeechTranscriber
{
    /// <summary>
    /// Распознаёт речь из аудиофайла. Возвращает пустую строку, если речи в записи нет;
    /// при сбое распознавания бросает исключение с понятным текстом причины.
    /// </summary>
    Task<string> TranscribeAsync(string audioPath, CancellationToken ct);
}
