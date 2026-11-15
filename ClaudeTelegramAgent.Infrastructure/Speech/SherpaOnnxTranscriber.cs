using System.ComponentModel;
using System.Diagnostics;
using ClaudeTelegramAgent.Application.Abstractions;
using SherpaOnnx;

namespace ClaudeTelegramAgent.Infrastructure.Speech;

/// <summary>
/// Локальное распознавание речи: ffmpeg декодирует аудио, Silero VAD режет по паузам,
/// Whisper (sherpa-onnx, int8) расшифровывает. Без Python и без внешних сервисов.
/// </summary>
public sealed class SherpaOnnxTranscriber(SpeechOptions options) : ISpeechTranscriber, IDisposable
{
    private const int SampleRate = 16000;

    private const int MaxWindowSamples = 28 * SampleRate;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken ct)
    {
        if (!File.Exists(audioPath))
        {
            throw new InvalidOperationException($"Аудиофайл не найден: {audioPath}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            var samples = await DecodeAsync(audioPath, timeoutCts.Token);

            await _gate.WaitAsync(timeoutCts.Token);
            try
            {
                return await Task.Run(() => Recognize(samples, timeoutCts.Token), timeoutCts.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Распознавание не уложилось в {options.TimeoutSeconds} секунд.");
        }
    }

    private async Task<float[]> DecodeAsync(string audioPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in new[]
        {
            "-nostdin", "-loglevel", "error", "-i", Path.GetFullPath(audioPath),
            "-f", "f32le", "-ac", "1", "-ar", SampleRate.ToString(), "-",
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось запустить ffmpeg ({options.FfmpegPath}). Он нужен для чтения аудио: {ex.Message}", ex);
        }

        process.StandardInput.Close();

        try
        {
            using var buffer = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = (await errorTask).Trim();
                throw new InvalidOperationException(
                    $"ffmpeg не смог прочитать аудио (код {process.ExitCode}): {(error.Length > 400 ? error[^400..] : error)}");
            }

            var bytes = buffer.GetBuffer();
            var samples = new float[(int)buffer.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
            return samples;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private string Recognize(float[] samples, CancellationToken ct)
    {
        var recognizer = _recognizer ??= CreateRecognizer();

        var windows = FindSpeechWindows(samples, ct);
        var parts = new List<string>();

        foreach (var (start, end) in windows)
        {
            ct.ThrowIfCancellationRequested();

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples[start..end]);
            recognizer.Decode(stream);

            var text = stream.Result.Text.Trim();
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        return string.Join(" ", parts);
    }

    private List<(int Start, int End)> FindSpeechWindows(float[] samples, CancellationToken ct)
    {
        if (!File.Exists(options.VadModelPath))
        {
            throw new InvalidOperationException($"Модель VAD не найдена: {options.VadModelPath}");
        }

        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = options.VadModelPath;
        vadConfig.SileroVad.MinSilenceDuration = 0.5f;
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SileroVad.MaxSpeechDuration = 25f;
        vadConfig.SampleRate = SampleRate;

        using var vad = new VoiceActivityDetector(vadConfig, 120);
        var window = vadConfig.SileroVad.WindowSize;

        for (var i = 0; i + window <= samples.Length; i += window)
        {
            ct.ThrowIfCancellationRequested();
            vad.AcceptWaveform(samples[i..(i + window)]);
        }

        vad.Flush();

        var windows = new List<(int Start, int End)>();
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();

            var start = segment.Start;
            var end = segment.Start + segment.Samples.Length;

            if (windows.Count > 0 && end - windows[^1].Start <= MaxWindowSamples)
            {
                windows[^1] = (windows[^1].Start, end);
            }
            else
            {
                windows.Add((start, end));
            }
        }

        return windows;
    }

    private OfflineRecognizer CreateRecognizer()
    {
        if (!Directory.Exists(options.ModelDir))
        {
            throw new InvalidOperationException($"Папка с моделью распознавания не найдена: {options.ModelDir}");
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Whisper.Encoder = FindModelFile("*encoder*.int8.onnx", "*encoder*.onnx");
        config.ModelConfig.Whisper.Decoder = FindModelFile("*decoder*.int8.onnx", "*decoder*.onnx");
        config.ModelConfig.Tokens = FindModelFile("*tokens.txt");
        config.ModelConfig.Whisper.Language = options.Language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : options.Language;
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.NumThreads = options.NumThreads;
        config.ModelConfig.Provider = "cpu";

        return new OfflineRecognizer(config);
    }

    private string FindModelFile(params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(options.ModelDir, pattern);
            if (files.Length > 0)
            {
                return files[0];
            }
        }

        throw new InvalidOperationException(
            $"В {options.ModelDir} нет файла модели по шаблону {string.Join(" или ", patterns)}.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _gate.Dispose();
    }
}
