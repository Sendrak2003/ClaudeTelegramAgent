using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ClaudeTelegramAgent.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Короткие фразы для этапов работы агента («Ищу в интернете», «Секунду, думаю»): озвучиваются один раз,
/// хранятся в памяти и на диске и потом отдаются мгновенно, пока агент ещё готовит ответ.
/// Недостающие фразы готовятся в фоне по одной (при открытии плеера), поэтому сами ничего не задерживают:
/// если фразы ещё нет, в этот раз её просто не будет.
/// </summary>
public sealed class LiveFillerCache(ISpeechStreamer streamer, string directory, string variant, ILogger<LiveFillerCache> logger)
{
    private static readonly Dictionary<string, string[]> Phrases = new()
    {
        ["Думаю"] = ["Секунду, думаю.", "Так, сейчас подумаю.", "Минутку, соображаю."],
        ["Расшифровываю аудио"] = ["Слушаю голосовое."],
        ["Ищу в интернете"] = ["Ищу в интернете."],
        ["Читаю страницу"] = ["Читаю страницу."],
        ["Открываю страницу"] = ["Открываю страницу."],
        ["Работаю в браузере"] = ["Работаю в браузере."],
        ["Смотрю файлы"] = ["Смотрю файлы."],
        ["Пишу файл"] = ["Пишу файл."],
        ["Выполняю команду"] = ["Выполняю команду."],
        ["Создаю напоминание"] = ["Создаю напоминание."],
        ["Смотрю напоминания"] = ["Смотрю напоминания."],
        ["Отменяю напоминание"] = ["Отменяю напоминание."],
        ["Отправляю файл"] = ["Отправляю файл."],
    };

    private readonly ConcurrentDictionary<string, byte[]> _pcm = new();
    private readonly HashSet<string> _queued = [];
    private readonly Channel<string> _work = Channel.CreateUnbounded<string>();
    private readonly object _gate = new();
    private int _rotation;
    private bool _workerStarted;

    /// <summary>Фраза для этапа; null — этот этап не озвучивается. Варианты одного этапа чередуются.</summary>
    public string? PhraseFor(string headline)
    {
        if (!Phrases.TryGetValue(headline, out var variants))
        {
            return null;
        }

        return variants[Interlocked.Increment(ref _rotation) % variants.Length];
    }

    /// <summary>Готовый звук фразы (s16le, 24 кГц, моно); если его ещё нет, ставит в очередь на фоновую подготовку и возвращает null.</summary>
    public byte[]? TryGet(string phrase)
    {
        if (_pcm.TryGetValue(phrase, out var cached))
        {
            return cached;
        }

        var path = PathFor(phrase);
        try
        {
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                _pcm[phrase] = bytes;
                return bytes;
            }
        }
        catch (IOException)
        {
        }

        Queue(phrase);
        return null;
    }

    /// <summary>Готовит все фразы, которых ещё нет; ничего не делает, если все уже есть.</summary>
    public void WarmUp()
    {
        foreach (var phrase in Phrases.Values.SelectMany(v => v))
        {
            if (!_pcm.ContainsKey(phrase))
            {
                TryGet(phrase);
            }
        }
    }

    private void Queue(string phrase)
    {
        lock (_gate)
        {
            if (!_queued.Add(phrase))
            {
                return;
            }

            _work.Writer.TryWrite(phrase);
            if (!_workerStarted)
            {
                _workerStarted = true;
                _ = Task.Run(WorkAsync);
            }
        }
    }

    private async Task WorkAsync()
    {
        await foreach (var phrase in _work.Reader.ReadAllAsync())
        {
            try
            {
                using var pcm = new MemoryStream();
                await streamer.StreamAsync(
                    phrase,
                    chunk =>
                    {
                        pcm.Write(chunk.Span);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                var bytes = pcm.ToArray();
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(PathFor(phrase), bytes);
                _pcm[phrase] = bytes;
                logger.LogInformation("Live filler ready: \"{Phrase}\", {Seconds:0.0} s of speech", phrase, bytes.Length / 48000.0);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live filler failed: \"{Phrase}\"", phrase);
            }
            finally
            {
                lock (_gate)
                {
                    _queued.Remove(phrase);
                }
            }
        }
    }

    private string PathFor(string phrase) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(variant + "\n" + phrase)))[..24] + ".pcm");
}
