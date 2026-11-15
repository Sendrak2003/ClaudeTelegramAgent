namespace ClaudeTelegramAgent.Application.Abstractions;

/// <summary>Потоковая озвучка: звук отдаётся кусками по мере генерации, не дожидаясь конца фразы.</summary>
public interface ISpeechStreamer
{
    /// <summary>
    /// Озвучивает текст и вызывает <paramref name="onPcm"/> с каждым куском сырого звука (s16le, 24 кГц, моно, целое число отсчётов).
    /// Память куска действует только до возврата из вызова: нужное надо скопировать.
    /// </summary>
    Task StreamAsync(string text, Func<ReadOnlyMemory<byte>, Task> onPcm, CancellationToken ct);

    /// <summary>
    /// Озвучивает текст целиком и отдаёт сырой звук (s16le, 24 кГц, моно). Одновременные вызовы сервер считает пачкой,
    /// это быстрее, чем по очереди, но звук приходит только после конца расчёта.
    /// </summary>
    Task<byte[]> SynthesizePcmAsync(string text, CancellationToken ct);
}
