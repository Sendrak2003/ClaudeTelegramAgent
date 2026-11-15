using System.Collections.Concurrent;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>Событие ответа для плеера: номер по порядку и JSON.</summary>
public readonly record struct LiveEvent(long Seq, string Json);

/// <summary>
/// Канал между озвучкой и открытым мини-приложением: события ответа (JSON) копятся по чатам, плеер забирает их
/// длинными запросами (они проходят через любой прокси и туннель, в отличие от SSE); звук идёт теми же событиями.
/// События текущего ответа запоминаются, чтобы приложение, открытое посреди ответа, увидело его с начала.
/// Плеер считается открытым, пока недавно забирал события.
/// </summary>
public sealed class LiveVoiceHub(ILogger<LiveVoiceHub> logger)
{
    private static readonly TimeSpan WatchWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, Feed> _feeds = new();

    /// <summary>Меняется при каждом запуске сервера: плеер по нему понимает, что номера событий начались заново.</summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    public bool HasListeners(ChatId chatId) => _feeds.TryGetValue(chatId.Value, out var feed) && feed.IsWatched(WatchWindow);

    /// <summary>События чата после <paramref name="after"/>; если их нет, ждёт новых до <paramref name="timeout"/>.</summary>
    public async Task<IReadOnlyList<LiveEvent>> WaitAsync(ChatId chatId, long after, TimeSpan timeout, CancellationToken ct)
    {
        var feed = _feeds.GetOrAdd(chatId.Value, _ => new Feed());
        if (!feed.IsWatched(WatchWindow))
        {
            logger.LogInformation("Live player connected for chat {ChatId}", chatId);
        }

        return await feed.WaitAsync(after, timeout, ct);
    }

    /// <summary>Новый ответ начинает историю заново (<paramref name="startsReply"/>); остальные события дописываются к ней.</summary>
    public void Publish(ChatId chatId, string json, bool startsReply = false) =>
        _feeds.GetOrAdd(chatId.Value, _ => new Feed()).Publish(json, startsReply);

    private sealed class Feed
    {
        private readonly object _gate = new();
        private readonly List<LiveEvent> _history = [];
        private long _seq;
        private DateTime _lastSeen = DateTime.MinValue;
        private TaskCompletionSource _signal = NewSignal();

        public bool IsWatched(TimeSpan window)
        {
            lock (_gate)
            {
                return DateTime.UtcNow - _lastSeen < window;
            }
        }

        public async Task<IReadOnlyList<LiveEvent>> WaitAsync(long after, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    _lastSeen = DateTime.UtcNow;

                    var pending = _history.Where(e => e.Seq > after).ToList();
                    if (pending.Count > 0)
                    {
                        return pending;
                    }

                    signal = _signal.Task;
                }

                var left = deadline - DateTime.UtcNow;
                try
                {
                    if (left <= TimeSpan.Zero)
                    {
                        return [];
                    }

                    await signal.WaitAsync(left, ct);
                }
                catch (TimeoutException)
                {
                    return [];
                }
                catch (OperationCanceledException)
                {
                    return [];
                }
                finally
                {
                    lock (_gate)
                    {
                        _lastSeen = DateTime.UtcNow;
                    }
                }
            }
        }

        public void Publish(string json, bool startsReply)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                if (startsReply)
                {
                    _history.Clear();
                }

                _history.Add(new LiveEvent(++_seq, json));
                signal = _signal;
                _signal = NewSignal();
            }

            signal.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
