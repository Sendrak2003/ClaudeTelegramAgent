using System.ComponentModel;
using System.Diagnostics;
using ClaudeTelegramAgent.Infrastructure.Speech;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types;
using System.Text.Json;

namespace ClaudeTelegramAgent.RemindersMcpServer;

[McpServerToolType]
public sealed class MessagingToolsAdapter
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    [McpServerTool(Name = "send_file")]
    [Description("Отправляет локальный файл (например, скриншот) пользователю в текущий Telegram-чат.")]
    public static async Task<string> SendFile(
        ITelegramBotClient bot,
        CurrentChatContext chatContext,
        [Description("Путь к файлу на диске (абсолютный или относительно рабочей папки)")] string path,
        [Description("Необязательная подпись к файлу")] string? caption = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return $"Файл не найден: {path}";
        }

        await using var stream = File.OpenRead(path);
        var inputFile = InputFile.FromStream(stream, Path.GetFileName(path));
        var extension = Path.GetExtension(path);

        if (ImageExtensions.Contains(extension))
        {
            await bot.SendPhoto(chatContext.ChatId.Value, inputFile, caption: caption, cancellationToken: ct);
        }
        else
        {
            await bot.SendDocument(chatContext.ChatId.Value, inputFile, caption: caption, cancellationToken: ct);
        }

        return $"Файл {Path.GetFileName(path)} отправлен пользователю в Telegram.";
    }
    [McpServerTool(Name = "save_voice_from_message")]
    [Description("Сохраняет голосовое сообщение из Telegram на диск по file_id.")]
    public static async Task<string> SaveVoiceFromMessage(
    ITelegramBotClient bot,
    [Description("file_id голосового сообщения из Telegram")] string fileId,
    [Description("Папка для сохранения файла (по умолчанию — текущая рабочая)")] string? saveDirectory = null,
    CancellationToken ct = default)
    {
        try
        {
            var telegramClient = bot as TelegramBotClient;
            var token = telegramClient?.Token;
            if (string.IsNullOrEmpty(token))
            {
                return "Cannot access bot token to download file.";
            }

            var httpClient = new HttpClient();
            var getFileUrl = $"https://api.telegram.org/bot{token}/getFile?file_id={Uri.EscapeDataString(fileId)}";
            using var getResp = await httpClient.GetAsync(getFileUrl, ct);
            var getJson = await getResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(getJson);
            if (!doc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
            {
                return $"Telegram API getFile failed: {getJson}";
            }

            var result = doc.RootElement.GetProperty("result");
            var filePath = result.GetProperty("file_path").GetString();
            if (string.IsNullOrEmpty(filePath))
            {
                return $"Telegram returned empty file_path: {getJson}";
            }

            saveDirectory ??= Directory.GetCurrentDirectory();
            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            string fileName = $"voice_{fileId}_{DateTime.Now:yyyyMMdd_HHmmss}.ogg";
            string fullPath = Path.Combine(saveDirectory, fileName);

            var downloadUrl = $"https://api.telegram.org/file/bot{token}/{filePath}";
            using var downloadResp = await httpClient.GetAsync(downloadUrl, ct);
            downloadResp.EnsureSuccessStatusCode();
            await using var responseStream = await downloadResp.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(fullPath);
            await responseStream.CopyToAsync(fileStream, ct);

            return $"Голосовое сообщение сохранено: {fullPath}";
        }
        catch (Exception ex)
        {
            return $"Ошибка при сохранении голосового сообщения: {ex.Message}";
        }
    }

    [McpServerTool(Name = "convert_voice_to_wav")]
    [Description("Конвертирует OGG-файл (от Telegram) в WAV для распознавания речи.")]
    public static async Task<string> ConvertVoiceToWav(
        [Description("Путь к исходному OGG-файлу")] string inputPath,
        [Description("Путь для выходного WAV-файла (включая имя файла)")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return $"Исходный файл не найден: {inputPath}";

        var outputDir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            return $"Папка для выходного файла не существует: {outputDir}";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[] { "-y", "-i", inputPath, "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1", outputPath })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return "Не удалось запустить ffmpeg. Убедитесь, что он установлен и добавлен в PATH.";

            var errorTask = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && File.Exists(outputPath))
                return $"Конвертация успешна: {outputPath}";

            var error = await errorTask;
            return $"Ошибка конвертации (код {process.ExitCode}): {error}";
        }
        catch (OperationCanceledException)
        {
            return "Конвертация отменена.";
        }
        catch (Exception ex)
        {
            return $"Исключение при конвертации: {ex.Message}";
        }
    }

    [McpServerTool(Name = "transcribe_voice")]
    [Description("Распознаёт речь из аудиофайла (OGG, WAV, MP3 и др.) локально и возвращает текст. Нужен для аудио, которое пришло не голосовым сообщением: расшифровку голосовых бот делает сам. Модель грузится несколько секунд.")]
    public static async Task<string> TranscribeVoice(
        [Description("Путь к аудиофайлу")] string audioPath,
        [Description("Язык речи (ru, en, ...) или auto для автоопределения")] string language = "ru",
        CancellationToken ct = default)
    {
        var dataDir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("REMINDERS_DB") ?? "");
        if (string.IsNullOrEmpty(dataDir))
            return "Не удалось определить папку данных (нет REMINDERS_DB).";

        var modelsDir = Path.Combine(dataDir, "models");
        var options = new SpeechOptions(
            Path.Combine(modelsDir, "sherpa-onnx-whisper-turbo"),
            Path.Combine(modelsDir, "silero_vad.onnx"),
            "ffmpeg",
            string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim(),
            Math.Min(8, Environment.ProcessorCount),
            TimeoutSeconds: 600);

        try
        {
            using var transcriber = new SherpaOnnxTranscriber(options);
            var text = await transcriber.TranscribeAsync(audioPath, ct);
            return string.IsNullOrWhiteSpace(text) ? "Речь в записи не обнаружена." : text;
        }
        catch (OperationCanceledException)
        {
            return "Распознавание отменено.";
        }
        catch (Exception ex)
        {
            return $"Ошибка распознавания: {ex.Message}";
        }
    }
}
