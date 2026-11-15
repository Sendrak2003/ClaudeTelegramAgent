using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <param name="Enabled">Мини-приложение с живой озвучкой доступно вообще.</param>
/// <param name="MaxChars">Ответы длиннее (после очистки от разметки) не озвучиваются.</param>
/// <param name="PublicUrl">Адрес мини-приложения по HTTPS, который Telegram откроет кнопкой; пусто — кнопки нет.</param>
/// <param name="BatchMax">Сколько готовых к озвучке кусков (кроме первого и одиночных) считается пачкой вместе; 1 — всегда по одному.</param>
public sealed record LiveVoiceOptions(bool Enabled, int MaxChars, string? PublicUrl, int BatchMax = 3);

/// <summary>
/// Живая озвучка в мини-приложении Telegram: пока агент пишет ответ, законченные куски озвучиваются и сразу
/// уходят в открытый плеер, не дожидаясь конца. Работает, только когда приложение открыто: иначе считать незачем.
/// </summary>
public sealed class LiveVoiceService(
    ISpeechStreamer streamer,
    LiveFillerCache fillers,
    LiveVoiceHub hub,
    LiveVoiceOptions options,
    ILogger<LiveVoiceService> logger) : IReplyAddon
{
    public string Name => "live";

    public bool EnabledByDefault => true;

    public bool Available => options.Enabled;

    public string? PublicUrl => options.PublicUrl;

    public IAddonRun? Begin(ChatId chatId, bool afterVoiceInput)
    {
        if (!Available)
        {
            return null;
        }

        if (!hub.HasListeners(chatId))
        {
            logger.LogInformation("Live voice skipped for chat {ChatId}: the player is not open", chatId);
            return null;
        }

        logger.LogInformation("Live voice started for chat {ChatId}", chatId);
        return Start(chatId);
    }

    /// <summary>Начинает ответ независимо от того, открыт ли плеер: так проверяют озвучку без самого бота.</summary>
    public LiveVoiceRun Start(ChatId chatId) => new(streamer, fillers, hub, logger, chatId, options.MaxChars, options.BatchMax);

    /// <summary>Готовит в фоне фразы-заполнители, которых ещё нет; вызывается, пока открыт плеер.</summary>
    public void WarmUp() => fillers.WarmUp();
}
