using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Infrastructure.Claude;

public sealed class ClaudeCliAgentClient(ClaudeCliOptions options) : IClaudeAgentClient
{
    public async Task<ClaudeAgentReply> SendAsync(
        ChatId chatId,
        string prompt,
        string? sessionId,
        CancellationToken ct,
        Action<AgentProgress>? onProgress = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Bin,
            WorkingDirectory = options.WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--include-partial-messages");

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(options.Model);
        }

        if (options.MaxTurns is > 0)
        {
            startInfo.ArgumentList.Add("--max-turns");
            startInfo.ArgumentList.Add(options.MaxTurns.Value.ToString());
        }

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
        string? resultLine = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (HandleStreamLine(e.Data, onProgress))
            {
                resultLine = e.Data;
            }
            else if (!e.Data.StartsWith('{'))
            {
                stdout.AppendLine(e.Data);
            }
        };
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

        var stdoutText = resultLine ?? stdout.ToString();
        var stderrText = stderr.ToString();

        return ParseReply(stdoutText, stderrText, process.ExitCode, sessionId, options.MaxTurns);
    }

    private static bool HandleStreamLine(string line, Action<AgentProgress>? onProgress)
    {
        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type)
            {
                return false;
            }

            if (type == "result")
            {
                return true;
            }

            if (onProgress is null
                || (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String))
            {
                return false;
            }

            switch (type)
            {
                case "stream_event" when root.TryGetProperty("event", out var streamEvent):
                    HandleStreamEvent(streamEvent, onProgress);
                    break;

                case "user" when TryGetToolResult(root, out var failed):
                    onProgress(new AgentProgress(AgentProgressKind.ToolFinished, failed ? "error" : null));
                    break;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static void HandleStreamEvent(JsonElement streamEvent, Action<AgentProgress> onProgress)
    {
        switch (GetString(streamEvent, "type"))
        {
            case "content_block_start" when streamEvent.TryGetProperty("content_block", out var block):
                switch (GetString(block, "type"))
                {
                    case "text":
                        onProgress(new AgentProgress(AgentProgressKind.TextStarted));
                        break;
                    case "tool_use":
                        onProgress(new AgentProgress(AgentProgressKind.ToolStarted, GetString(block, "name")));
                        break;
                    case "thinking":
                        onProgress(new AgentProgress(AgentProgressKind.Thinking));
                        break;
                }

                break;

            case "content_block_delta" when streamEvent.TryGetProperty("delta", out var delta)
                                             && GetString(delta, "type") == "text_delta"
                                             && GetString(delta, "text") is { Length: > 0 } text:
                onProgress(new AgentProgress(AgentProgressKind.TextDelta, text));
                break;
        }
    }

    private static bool TryGetToolResult(JsonElement root, out bool isError)
    {
        isError = false;

        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var found = false;
        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_result")
            {
                continue;
            }

            found = true;
            if (block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                isError = true;
            }
        }

        return found;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ClaudeAgentReply ParseReply(string stdoutText, string stderrText, int exitCode, string? previousSessionId, int? maxTurns)
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

            var errorReply = DescribeErrorResult(root, newSessionId, maxTurns);
            if (errorReply is not null)
            {
                return errorReply;
            }
        }
        catch (JsonException)
        {
            return new ClaudeAgentReply(stdoutText.Trim(), previousSessionId, Success: exitCode == 0);
        }

        var fallback = string.IsNullOrWhiteSpace(stderrText) ? stdoutText.Trim() : stderrText.Trim();
        return new ClaudeAgentReply(fallback, previousSessionId, Success: false);
    }

    private static ClaudeAgentReply? DescribeErrorResult(JsonElement root, string? sessionId, int? maxTurns)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("subtype", out var subtypeElement)
            || subtypeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var subtype = subtypeElement.GetString();
        if (subtype is null || !subtype.StartsWith("error", StringComparison.Ordinal))
        {
            return null;
        }

        if (subtype == "error_max_turns")
        {
            var limit = maxTurns is > 0 ? $" ({maxTurns})" : string.Empty;
            return new ClaudeAgentReply(
                $"Агент упёрся в лимит шагов{limit} и остановился, не закончив. Напиши «продолжай», и он продолжит с этого места.",
                sessionId,
                Success: false);
        }

        var details = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? string.Join("; ", errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
            : string.Empty;

        return new ClaudeAgentReply(
            $"Claude завершил запуск с ошибкой ({subtype}){(details.Length > 0 ? ": " + details : string.Empty)}.",
            sessionId,
            Success: false);
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
                string? lastText = null;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "text"
                        && block.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        lastText = text.GetString();
                    }
                }

                if (lastText is not null)
                {
                    return lastText;
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
        }
    }
}
