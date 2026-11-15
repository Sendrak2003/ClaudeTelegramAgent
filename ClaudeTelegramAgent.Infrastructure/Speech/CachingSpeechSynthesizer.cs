using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeTelegramAgent.Application.Abstractions;

namespace ClaudeTelegramAgent.Infrastructure.Speech;

/// <summary>
/// Кэш готовых голосовых на диске: повторяющиеся куски текста («Хорошо, напомню…») озвучиваются один раз,
/// потом отдаются сразу. Ключ включает голос и язык, поэтому смена настроек озвучки не отдаёт старые записи.
/// Старые записи вытесняются, когда файлов становится больше <paramref name="maxFiles"/>.
/// </summary>
public sealed class CachingSpeechSynthesizer(
    ISpeechSynthesizer inner,
    string cacheDirectory,
    string variant,
    int maxFiles) : ISpeechSynthesizer
{
    private readonly ConcurrentDictionary<string, Task<byte[]>> _inFlight = new();

    /// <summary>
    /// Один и тот же кусок, который просят одновременно (голосовые в Telegram и живой плеер), считается один раз:
    /// остальные ждут его результата. Если считавший отменён или упал, ждущий пробует сам.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        var key = KeyFor(text);

        while (true)
        {
            if (TryGetCached(text) is { } cached)
            {
                return cached;
            }

            var owner = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(key, owner.Task))
            {
                try
                {
                    var voice = await inner.SynthesizeAsync(text, ct);
                    Store(PathFor(text), voice);
                    owner.SetResult(voice);
                    return voice;
                }
                catch (Exception)
                {
                    owner.TrySetCanceled();
                    throw;
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }

            if (!_inFlight.TryGetValue(key, out var running))
            {
                continue;
            }

            try
            {
                return await running.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    public byte[]? TryGetCached(string text)
    {
        var path = PathFor(text);

        try
        {
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return File.ReadAllBytes(path);
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private string PathFor(string text) => Path.Combine(cacheDirectory, KeyFor(text) + ".ogg");

    private string KeyFor(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(variant + "\n" + text)));

    private void Store(string path, byte[] voice)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, voice);
            File.Move(temp, path, overwrite: true);

            var files = new DirectoryInfo(cacheDirectory).GetFiles("*.ogg");
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(Math.Max(0, files.Length - maxFiles)))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
