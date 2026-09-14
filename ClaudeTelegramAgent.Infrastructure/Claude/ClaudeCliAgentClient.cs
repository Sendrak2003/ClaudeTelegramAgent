using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Infrastructure.Claude;

// ВАЖНО: формат `claude -p "..." --output-format json` нужно сверить вживую
// (`claude -p "тест" --output-format json`) и поправить парсинг ниже при расхождении.
public sealed class ClaudeCliAgentClient(ClaudeCliOptions options) : IClaudeAgentClient
{
    public async Task<ClaudeAgentReply> SendAsync(ChatId chatId, string prompt, string? sessionId, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Bin,
            WorkingDirectory = options.WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // claude CLI пишет UTF-8 в stdout/stderr независимо от системной кодовой страницы —
            // без этого на Windows кириллица превращается в кракозябры (читается в codepage по умолчанию).
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");

        if (!string.IsNullOrEmpty(sessionId))
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(sessionId);
        }

        if (string.Equals(options.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--dangerously-skip-permissions");
        }
        else
        {
            startInfo.ArgumentList.Add("--permission-mode");
            startInfo.ArgumentList.Add(options.PermissionMode);
        }

        startInfo.Environment["REMINDER_CHAT_ID"] = chatId.ToString();

        if (options.Auth == ClaudeAuthMode.ApiKey && !string.IsNullOrEmpty(options.ApiKey))
        {
            startInfo.Environment["ANTHROPIC_API_KEY"] = options.ApiKey;
        }
        else
        {
            startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            return new ClaudeAgentReply(
                $"Claude CLI не ответил за {options.TimeoutSeconds} секунд и был остановлен.",
                sessionId,
                Success: false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();

        return ParseReply(stdoutText, stderrText, process.ExitCode, sessionId);
    }

    private static ClaudeAgentReply ParseReply(string stdoutText, string stderrText, int exitCode, string? previousSessionId)
    {
        if (string.IsNullOrWhiteSpace(stdoutText))
        {
            var errorText = string.IsNullOrWhiteSpace(stderrText)
                ? $"Claude CLI завершился с кодом {exitCode} и не вернул вывода."
                : stderrText.Trim();
            return new ClaudeAgentReply(errorText, previousSessionId, Success: false);
        }

        try
        {
            using var document = JsonDocument.Parse(stdoutText);
            var root = document.RootElement;

            var text = FindText(root);
            var newSessionId = FindSessionId(root) ?? previousSessionId;

            if (text is not null)
            {
                return new ClaudeAgentReply(text, newSessionId, Success: exitCode == 0);
            }
        }
        catch (JsonException)
        {
            // Вывод не JSON — возвращаем сырой текст как есть.
            return new ClaudeAgentReply(stdoutText.Trim(), previousSessionId, Success: exitCode == 0);
        }

        var fallback = string.IsNullOrWhiteSpace(stderrText) ? stdoutText.Trim() : stderrText.Trim();
        return new ClaudeAgentReply(fallback, previousSessionId, Success: false);
    }

    private static string? FindText(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "result", "response", "output", "text" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "text"
                        && block.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }

                if (builder.Length > 0)
                {
                    return builder.ToString();
                }
            }

            if (root.TryGetProperty("message", out var message))
            {
                var nested = FindText(message);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindSessionId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "session_id", "sessionId", "id" })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static void TryKillProcessTree(Process process)
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
            // Процесс уже мог завершиться — игнорируем.
        }
    }
}
