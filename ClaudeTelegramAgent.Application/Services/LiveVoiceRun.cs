using System.Diagnostics;
using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Один ответ в живом плеере: законченные куски текста озвучиваются по очереди, как только дописаны,
/// и каждый уходит плееру событием с временем (когда текст был готов, сколько считалось аудио) — по ним плеер
/// показывает, успевает ли озвучка за ответом. Последний, ещё растущий кусок озвучивается, когда ответ закончен.
/// </summary>
public sealed class LiveVoiceRun : IAddonRun
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const long MinFillerGapMs = 6000;
    private const int ThinkingFillerDelayMs = 1500;

    private readonly ISpeechStreamer _streamer;
    private readonly LiveFillerCache _fillers;
    private readonly LiveVoiceHub _hub;
    private readonly ILogger _logger;
    private readonly ChatId _chatId;
    private readonly int _maxChars;
    private readonly int _batchMax;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<string> _enqueued = [];
    private readonly List<(int Seq, string Text, long ReadyMs)> _pending = [];

    private CancellationTokenSource _cts = new();
    private bool _working;
    private bool _delivered;
    private bool _doneSent;
    private long _lastFillerMs = -MinFillerGapMs;

    public LiveVoiceRun(ISpeechStreamer streamer, LiveFillerCache fillers, LiveVoiceHub hub, ILogger logger, ChatId chatId, int maxChars, int batchMax = 3)
    {
        _streamer = streamer;
        _fillers = fillers;
        _hub = hub;
        _logger = logger;
        _chatId = chatId;
        _maxChars = maxChars;
        _batchMax = batchMax;

        Publish(new { type = "start", restarted = false }, startsReply: true);
    }

    public void OnAnswerGrowing(string textSoFar)
    {
        var chunks = SpeechText.ToChunks(WithoutUnclosedCode(textSoFar), _maxChars);

        lock (_gate)
        {
            for (var i = _enqueued.Count; i < chunks.Count - 1; i++)
            {
                Enqueue(chunks[i]);
            }
        }
    }

    /// <summary>
    /// Пока ответ ещё не пошёл в озвучку, этап работы агента проговаривается готовой короткой фразой: она уже в кэше
    /// и звучит сразу. «Думаю» проговаривается, только если ответа нет дольше полутора секунд; фразы не чаще, чем раз в 6 секунд.
    /// </summary>
    public void OnStage(string headline)
    {
        if (_fillers.PhraseFor(headline) is not { } phrase)
        {
            return;
        }

        CancellationToken ct;
        lock (_gate)
        {
            var now = _clock.ElapsedMilliseconds;
            if (_enqueued.Count > 0 || now - _lastFillerMs < MinFillerGapMs)
            {
                return;
            }

            _lastFillerMs = now;
            ct = _cts.Token;
        }

        _ = Task.Run(() => SpeakFillerAsync(phrase, headline == "Думаю" ? ThinkingFillerDelayMs : 0, ct));
    }

    private async Task SpeakFillerAsync(string phrase, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            lock (_gate)
            {
                if (_enqueued.Count > 0 || ct.IsCancellationRequested)
                {
                    return;
                }
            }

            if (_fillers.TryGet(phrase) is { } pcm)
            {
                Publish(new { type = "filler", text = phrase, pcm = Convert.ToBase64String(pcm), atMs = _clock.ElapsedMilliseconds });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Restart()
    {
        lock (_gate)
        {
            ResetUnsafe();
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts.Cancel();
        }

        Publish(new { type = "cancel" });
    }

    public void Deliver(string finalText)
    {
        var chunks = SpeechText.ToChunks(finalText, _maxChars);

        lock (_gate)
        {
            if (!_enqueued.SequenceEqual(chunks.Take(_enqueued.Count)))
            {
                _logger.LogWarning("Live voice for chat {ChatId}: final text differs from the already voiced chunks, voicing again from the start", _chatId);
                ResetUnsafe();
            }

            for (var i = _enqueued.Count; i < chunks.Count; i++)
            {
                Enqueue(chunks[i]);
            }

            _delivered = true;
            SendDoneIfFinishedUnsafe();
        }
    }

    private void ResetUnsafe()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _enqueued.Clear();
        _pending.Clear();
        _working = false;
        _delivered = false;
        _doneSent = false;
        Publish(new { type = "start", restarted = true }, startsReply: true);
    }

    private void Enqueue(string text)
    {
        var seq = _enqueued.Count;
        _enqueued.Add(text);
        _pending.Add((seq, text, _clock.ElapsedMilliseconds));

        if (!_working)
        {
            _working = true;
            var ct = _cts.Token;
            _ = Task.Run(() => WorkAsync(ct));
        }
    }

    private void SendDoneIfFinishedUnsafe()
    {
        if (_delivered && !_working && _pending.Count == 0 && !_doneSent)
        {
            _doneSent = true;
            Publish(new { type = "done", totalMs = _clock.ElapsedMilliseconds });
        }
    }

    /// <summary>
    /// Куски озвучиваются по очереди. Один готовый кусок идёт потоком, и звук начинается сразу. Если к моменту, когда сервер
    /// освободился, готовых кусков уже несколько (агент пишет быстрее, чем идёт речь), их считают пачкой: так каждый секунды речи
    /// обходится дешевле, и звук успевает за речью, но пачка приходит целиком в конце расчёта.
    /// </summary>
    private async Task WorkAsync(CancellationToken ct)
    {
        while (true)
        {
            List<(int Seq, string Text, long ReadyMs)> take;
            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (_pending.Count == 0)
                {
                    _working = false;
                    SendDoneIfFinishedUnsafe();
                    return;
                }

                var count = _batchMax > 1 && _pending.Count >= 2 ? Math.Min(_pending.Count, _batchMax) : 1;
                take = _pending.Take(count).ToList();
                _pending.RemoveRange(0, count);
            }

            if (take.Count == 1)
            {
                await StreamOneAsync(take[0], ct);
            }
            else
            {
                await BatchAsync(take, ct);
            }
        }
    }

    private async Task StreamOneAsync((int Seq, string Text, long ReadyMs) item, CancellationToken ct)
    {
        var (seq, text, readyMs) = item;
        var startMs = _clock.ElapsedMilliseconds;
        try
        {
            Publish(new { type = "chunk", seq, text, readyMs, startMs });

            long bytes = 0;
            await _streamer.StreamAsync(
                text,
                pcm =>
                {
                    bytes += pcm.Length;
                    if (!ct.IsCancellationRequested)
                    {
                        Publish(new { type = "audio", seq, pcm = Convert.ToBase64String(pcm.Span), atMs = _clock.ElapsedMilliseconds });
                    }

                    return Task.CompletedTask;
                },
                ct);

            if (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Live voice chunk {Seq} streamed for chat {ChatId}: {Chars} chars, {Ms} ms", seq, _chatId, text.Length, _clock.ElapsedMilliseconds - startMs);
                Publish(new { type = "chunkEnd", seq, doneMs = _clock.ElapsedMilliseconds, bytes });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live voice chunk {Seq} failed for chat {ChatId}", seq, _chatId);
            Publish(new { type = "error", seq, message = ex.Message });
        }
    }

    private async Task BatchAsync(List<(int Seq, string Text, long ReadyMs)> items, CancellationToken ct)
    {
        var startMs = _clock.ElapsedMilliseconds;
        foreach (var (seq, text, readyMs) in items)
        {
            Publish(new { type = "chunk", seq, text, readyMs, startMs });
        }

        var tasks = items.Select(item => _streamer.SynthesizePcmAsync(item.Text, ct)).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var seq = items[i].Seq;
            try
            {
                var pcm = await tasks[i];
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                const int pieceBytes = 48000;
                for (var offset = 0; offset < pcm.Length; offset += pieceBytes)
                {
                    var length = Math.Min(pieceBytes, pcm.Length - offset);
                    Publish(new { type = "audio", seq, pcm = Convert.ToBase64String(pcm.AsSpan(offset, length)), atMs = _clock.ElapsedMilliseconds });
                }

                Publish(new { type = "chunkEnd", seq, doneMs = _clock.ElapsedMilliseconds, bytes = (long)pcm.Length });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live voice chunk {Seq} failed for chat {ChatId}", seq, _chatId);
                Publish(new { type = "error", seq, message = ex.Message });
            }
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Live voice batch of {Count} chunks done for chat {ChatId}: {Ms} ms", items.Count, _chatId, _clock.ElapsedMilliseconds - startMs);
        }
    }

    private void Publish(object evt, bool startsReply = false) =>
        _hub.Publish(_chatId, JsonSerializer.Serialize(evt, Json), startsReply);

    private static string WithoutUnclosedCode(string text)
    {
        var count = 0;
        for (var index = text.IndexOf("```", StringComparison.Ordinal); index >= 0; index = text.IndexOf("```", index + 3, StringComparison.Ordinal))
        {
            count++;
        }

        return count % 2 == 0 ? text : text[..text.LastIndexOf("```", StringComparison.Ordinal)];
    }
}
