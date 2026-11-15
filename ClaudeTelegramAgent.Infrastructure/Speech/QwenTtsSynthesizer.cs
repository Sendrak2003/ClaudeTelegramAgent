using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;

namespace ClaudeTelegramAgent.Infrastructure.Speech;

/// <summary>
/// Озвучка голосом Qwen3-TTS (Vivian): запрос к локальному серверу qwen3-tts.cpp, затем ffmpeg упаковывает
/// WAV в OGG/Opus для Telegram. Модель держится в памяти сервера, поэтому запросы идут без загрузки.
/// </summary>
public sealed class QwenTtsSynthesizer(QwenTtsOptions options) : ISpeechSynthesizer, ISpeechStreamer, IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["speaker"] = options.Speaker,
                ["language"] = options.Language,
            };

            if (options.Temperature is { } temperature)
            {
                request["temperature"] = temperature;
            }

            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _http.PostAsync("/v1/tts", content, timeoutCts.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300));
                throw new InvalidOperationException($"Сервер озвучки ответил {(int)response.StatusCode}: {detail}");
            }

            return await ToOggOpusAsync(body, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Озвучка не уложилась в {options.TimeoutSeconds} секунд.");
        }
    }

    /// <summary>Потоковый эндпоинт сервера: первые отсчёты приходят меньше чем через секунду, остальные по мере генерации.</summary>
    public async Task StreamAsync(string text, Func<ReadOnlyMemory<byte>, Task> onPcm, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["speaker"] = options.Speaker,
                ["language"] = options.Language,
            };

            if (options.Temperature is { } temperature)
            {
                request["temperature"] = temperature;
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/tts/stream")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                throw new InvalidOperationException($"Сервер озвучки ответил {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var buffer = new byte[9600];
            var carried = 0;

            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(carried), timeoutCts.Token);
                if (read == 0)
                {
                    break;
                }

                var total = carried + read;
                var whole = total & ~1;
                if (whole > 0)
                {
                    await onPcm(buffer.AsMemory(0, whole));
                }

                carried = total - whole;
                if (carried > 0)
                {
                    buffer[0] = buffer[whole];
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Озвучка не уложилась в {options.TimeoutSeconds} секунд.");
        }
    }

    public async Task<byte[]> SynthesizePcmAsync(string text, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["speaker"] = options.Speaker,
                ["language"] = options.Language,
            };

            if (options.Temperature is { } temperature)
            {
                request["temperature"] = temperature;
            }

            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _http.PostAsync("/v1/tts", content, timeoutCts.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300));
                throw new InvalidOperationException($"Сервер озвучки ответил {(int)response.StatusCode}: {detail}");
            }

            return WavToPcm(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Озвучка не уложилась в {options.TimeoutSeconds} секунд.");
        }
    }

    /// <summary>Достаёт сырые отсчёты из WAV: ищет блок data после заголовка, длину берёт из него или до конца файла.</summary>
    private static byte[] WavToPcm(byte[] wav)
    {
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BitConverter.ToUInt32(wav, pos + 4);
            if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
            {
                var start = pos + 8;
                var length = size == 0 || size == uint.MaxValue || start + size > wav.Length ? wav.Length - start : (int)size;
                return wav.AsSpan(start, length & ~1).ToArray();
            }

            pos += 8 + (int)size + (int)(size & 1);
        }

        throw new InvalidOperationException("В ответе сервера озвучки нет звука (блок data не найден).");
    }

    private async Task<byte[]> ToOggOpusAsync(byte[] wav, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var args = new List<string> { "-loglevel", "error", "-f", "wav", "-i", "pipe:0", "-ac", "1" };
        if (!string.IsNullOrWhiteSpace(options.AudioFilter))
        {
            args.AddRange(["-af", options.AudioFilter]);
        }

        args.AddRange(["-c:a", "libopus", "-b:a", "48k", "-f", "ogg", "pipe:1"]);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = ReadAllAsync(process.StandardOutput.BaseStream, ct);

            await process.StandardInput.BaseStream.WriteAsync(wav, ct);
            process.StandardInput.Close();

            var ogg = await stdoutTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || ogg.Length == 0)
            {
                throw new InvalidOperationException($"ffmpeg не смог упаковать голосовое: {await stderrTask}");
            }

            return ogg;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public byte[]? TryGetCached(string text) => null;

    public void Dispose() => _http.Dispose();
}
