namespace ClaudeTelegramAgent.Infrastructure.Speech;

/// <param name="ModelDir">Папка с моделью Whisper в формате sherpa-onnx (файлы *encoder*.onnx, *decoder*.onnx, *tokens.txt).</param>
/// <param name="VadModelPath">Путь к silero_vad.onnx — по нему запись режется по паузам.</param>
/// <param name="FfmpegPath">ffmpeg для декодирования OGG/MP3/WAV в 16 кГц моно.</param>
/// <param name="Language">Язык речи (ru, en, ...) или auto.</param>
/// <param name="NumThreads">Потоков процессора для распознавания.</param>
/// <param name="TimeoutSeconds">Сколько ждать распознавание одного файла.</param>
public sealed record SpeechOptions(
    string ModelDir,
    string VadModelPath,
    string FfmpegPath,
    string Language,
    int NumThreads,
    int TimeoutSeconds);
