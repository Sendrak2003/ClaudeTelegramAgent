using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <param name="Enabled">Озвучивать ли ответы вообще.</param>
/// <param name="MaxChars">Ответы длиннее (после очистки от разметки) не озвучиваются: голосом их слишком долго готовить и слушать.</param>
/// <param name="OnlyAfterVoiceInput">Озвучивать только когда пользователь сам написал голосовым.</param>
/// <param name="BatchFromChunks">Ответ из стольких кусков и более озвучивается так: первый кусок сам и сразу, остальные пачкой: уходят на сервер разом, он считает их вместе (--batch-size); 0 — всегда по одному.</param>
/// <param name="BatchParallelism">Сколько кусков пачки отправляется на сервер одновременно (не больше его --batch-size).</param>
/// <param name="DefaultOn">Включена ли озвучка у пользователя, который ещё не выбирал (/voice on|off).</param>
public sealed record VoiceReplyOptions(
    bool Enabled,
    int MaxChars,
    bool OnlyAfterVoiceInput,
    int BatchFromChunks = 0,
    int BatchParallelism = 1,
    bool DefaultOn = true);

/// <summary>
/// Голосовая копия ответа: текст уже отправлен, а голосовые приходят следом по предложениям, по мере готовности.
/// Короткий ответ озвучивается по одному куску, длинный — пачкой, несколько кусков сразу;
/// в обоих случаях голосовые уходят строго по порядку и в фоне, не задерживая основной ответ.
/// </summary>
public sealed class VoiceReplyService(
    ISpeechSynthesizer synthesizer,
    IMessageSender sender,
    LiveVoiceHub liveHub,
    VoiceReplyOptions options,
    ILogger<VoiceReplyService> logger) : IReplyAddon
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "voice";

    public bool EnabledByDefault => options.DefaultOn;

    public bool Available => options.Enabled;

    /// <summary>Пока у пользователя открыт живой плеер, голосовые в чат не отправляются: тот же голос звучит в плеере, а процессор один.</summary>
    private bool Allowed(ChatId chatId, bool afterVoiceInput) =>
        options.Enabled && (!options.OnlyAfterVoiceInput || afterVoiceInput) && !liveHub.HasListeners(chatId);

    /// <summary>Заготовка для одного ответа: озвучивает законченные куски, пока ответ ещё пишется. null — голос выключен.</summary>
    public IAddonRun? Begin(ChatId chatId, bool afterVoiceInput) =>
        Allowed(chatId, afterVoiceInput) ? new VoiceDraft(this, options.MaxChars, chatId, afterVoiceInput) : null;

    internal async Task PreSynthesizeAsync(string chunk, CancellationToken ct)
    {
        logger.LogInformation("Voice draft chunk: {Length} chars, cached={Cached}", chunk.Length, synthesizer.TryGetCached(chunk) is not null);

        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                await synthesizer.SynthesizeAsync(chunk, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Voice pre-synthesis failed");
        }
    }

    public void SpeakInBackground(ChatId chatId, string replyText, bool afterVoiceInput)
    {
        if (!Allowed(chatId, afterVoiceInput))
        {
            return;
        }

        var chunks = SpeechText.ToChunks(replyText, options.MaxChars);
        if (chunks.Count == 0)
        {
            return;
        }

        logger.LogInformation("Voice reply: {Count} chunks, lengths [{Lengths}]", chunks.Count, string.Join(", ", chunks.Select(c => c.Length)));

        _ = Task.Run(() => SpeakAsync(chatId, chunks));
    }

    private async Task SpeakAsync(ChatId chatId, IReadOnlyList<string> chunks)
    {
        try
        {
            if (options.BatchFromChunks > 0 && options.BatchParallelism > 1 && chunks.Count >= options.BatchFromChunks)
            {
                await SpeakBatchAsync(chatId, chunks);
                return;
            }

            foreach (var chunk in chunks)
            {
                var voice = await GetVoiceAsync(chunk);
                await sender.SendVoiceAsync(chatId, voice, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice reply failed for chat {ChatId}", chatId);
        }
    }

    /// <summary>Готовый кусок из кэша отдаётся сразу, без очереди; очередь нужна только тем, что ещё надо озвучить.</summary>
    private async Task<byte[]> GetVoiceAsync(string chunk)
    {
        if (synthesizer.TryGetCached(chunk) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync();
        try
        {
            return await synthesizer.SynthesizeAsync(chunk, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SpeakBatchAsync(ChatId chatId, IReadOnlyList<string> chunks)
    {
        var first = await GetVoiceAsync(chunks[0]);
        await sender.SendVoiceAsync(chatId, first, CancellationToken.None);

        using var limiter = new SemaphoreSlim(options.BatchParallelism);
        var voices = chunks.Skip(1).Select(chunk => SynthesizeLimitedAsync(chunk, limiter)).ToList();

        try
        {
            foreach (var voice in voices)
            {
                await sender.SendVoiceAsync(chatId, await voice, CancellationToken.None);
            }
        }
        finally
        {
            await Task.WhenAll(voices).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task<byte[]> SynthesizeLimitedAsync(string chunk, SemaphoreSlim limiter)
    {
        await limiter.WaitAsync();
        try
        {
            return await synthesizer.SynthesizeAsync(chunk, CancellationToken.None);
        }
        finally
        {
            limiter.Release();
        }
    }
}
