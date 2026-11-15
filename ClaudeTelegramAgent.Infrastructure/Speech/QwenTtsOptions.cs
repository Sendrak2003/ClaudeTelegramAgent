namespace ClaudeTelegramAgent.Infrastructure.Speech;

/// <param name="BaseUrl">Адрес сервера озвучки (qwen3-tts.cpp в режиме --serve), например http://vivian:8099.</param>
/// <param name="Speaker">Голос из набора модели CustomVoice (vivian).</param>
/// <param name="Language">Язык, как его называет модель (Russian).</param>
/// <param name="Temperature">Температура выбора звуков; null — значение сервера по умолчанию.</param>
/// <param name="TimeoutSeconds">Сколько ждать озвучку одного куска текста.</param>
/// <param name="FfmpegPath">ffmpeg для упаковки WAV в OGG/Opus.</param>
/// <param name="AudioFilter">Необязательная цепочка фильтров ffmpeg (-af), например срез верхних частот; null — без обработки.</param>
public sealed record QwenTtsOptions(
    string BaseUrl,
    string Speaker,
    string Language,
    double? Temperature,
    int TimeoutSeconds,
    string FfmpegPath,
    string? AudioFilter = null);
