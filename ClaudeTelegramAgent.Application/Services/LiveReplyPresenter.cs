using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Живое отображение работы агента в чате: сообщение-статус с этапами и таймерами
/// («Расшифровываю аудио», «Думаю», вызовы инструментов) и сообщение с ответом,
/// которое растёт по мере генерации. По завершении ответ заменяется итоговым текстом с форматированием.
/// </summary>
public sealed class LiveReplyPresenter(IMessageSender sender, ChatId chatId, ILogger logger)
{
    // Telegram терпит около одной правки в секунду на чат; статус и ответ правятся по очереди раз в интервал.
    private static readonly TimeSpan FirstFlushDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private const int MaxVisibleSteps = 8;
    private const int MaxStreamedLength = 3800;
    private const string Cursor = " ▍";

    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private readonly StringBuilder _answer = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();

    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private int? _statusMessageId;
    private int? _answerMessageId;
    private string _lastStatus = string.Empty;
    private string _lastAnswer = string.Empty;

    public void Start(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(_loopCts.Token);
    }

    public void StartStep(string title)
    {
        lock (_gate)
        {
            CompleteRunningUnsafe();
            _steps.Add(new Step(title));
        }
    }

    public void CompleteRunning()
    {
        lock (_gate)
        {
            CompleteRunningUnsafe();
        }
    }

    // Вызывается из потока чтения stdout claude — поэтому всё под замком.
    public void OnProgress(AgentProgress progress)
    {
        lock (_gate)
        {
            switch (progress.Kind)
            {
                case AgentProgressKind.Thinking:
                    if (!_steps.Any(s => !s.Done))
                    {
                        _steps.Add(new Step("Думаю"));
                    }

                    break;

                case AgentProgressKind.ToolStarted:
                    CompleteRunningUnsafe();
                    _steps.Add(new Step(DescribeTool(progress.Text), isTool: true));
                    break;

                case AgentProgressKind.ToolFinished:
                    CompleteRunningUnsafe();
                    _steps.Add(new Step("Думаю"));
                    break;

                case AgentProgressKind.TextStarted:
                    CompleteRunningUnsafe();
                    // В итоговый ответ идёт только последний текстовый блок, поэтому новый блок начинает поток заново.
                    _answer.Clear();
                    break;

                case AgentProgressKind.TextDelta:
                    _answer.Append(progress.Text);
                    break;
            }
        }
    }

    public async Task FinishAsync(string finalText, bool success, CancellationToken ct)
    {
        await StopLoopAsync();

        string? statusText = null;
        lock (_gate)
        {
            CompleteRunningUnsafe();
            if (_statusMessageId is not null)
            {
                var seconds = _total.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
                var steps = _steps.Count(s => s.IsTool);
                var tools = steps > 0 ? $" · инструментов: {steps}" : string.Empty;
                statusText = success ? $"✅ Готово · {seconds}с{tools}" : $"⚠️ Завершено с ошибкой · {seconds}с";
            }
        }

        if (statusText is not null && _statusMessageId is { } statusId)
        {
            await SafeAsync(() => sender.EditDraftAsync(chatId, statusId, statusText, ct));
        }

        if (_answerMessageId is { } answerId)
        {
            await sender.FinalizeDraftAsync(chatId, answerId, finalText, ct);
        }
        else
        {
            await sender.SendAsync(chatId, finalText, ct);
        }
    }

    // Запуск упал или отменён: убираем «крутящийся» статус, чтобы он не висел вечно.
    public async Task FailAsync(string reason)
    {
        await StopLoopAsync();

        if (_statusMessageId is { } statusId)
        {
            await SafeAsync(() => sender.EditDraftAsync(chatId, statusId, $"⚠️ {reason}", CancellationToken.None));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstFlushDelay, ct);

            while (!ct.IsCancellationRequested)
            {
                await SafeAsync(() => FlushAsync(ct));
                await Task.Delay(RefreshInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальная остановка: ответ готов.
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        string status;
        string answer;
        lock (_gate)
        {
            status = RenderStatusUnsafe();
            answer = RenderAnswerUnsafe();
        }

        if (status.Length > 0 && status != _lastStatus)
        {
            if (_statusMessageId is { } id)
            {
                await sender.EditDraftAsync(chatId, id, status, ct);
            }
            else
            {
                _statusMessageId = await sender.SendDraftAsync(chatId, status, ct);
            }

            _lastStatus = status;
        }

        if (answer.Length > 0 && answer != _lastAnswer)
        {
            if (_answerMessageId is { } id)
            {
                await sender.EditDraftAsync(chatId, id, answer, ct);
            }
            else
            {
                _answerMessageId = await sender.SendDraftAsync(chatId, answer, ct);
            }

            _lastAnswer = answer;
        }
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is null)
        {
            return;
        }

        await _loopCts.CancelAsync();
        await _loop;
        _loopCts.Dispose();
        _loopCts = null;
    }

    private string RenderStatusUnsafe()
    {
        if (_steps.Count == 0)
        {
            return string.Empty;
        }

        var visible = _steps.Count > MaxVisibleSteps ? _steps.Skip(_steps.Count - MaxVisibleSteps) : _steps;
        var lines = new List<string>();
        if (_steps.Count > MaxVisibleSteps)
        {
            lines.Add($"… ещё {_steps.Count - MaxVisibleSteps} шагов выше");
        }

        lines.AddRange(visible.Select(s => s.Render()));
        return string.Join('\n', lines);
    }

    private string RenderAnswerUnsafe()
    {
        var text = _answer.ToString().TrimEnd();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.Length > MaxStreamedLength)
        {
            text = "…" + text[^MaxStreamedLength..];
        }

        return text + Cursor;
    }

    private void CompleteRunningUnsafe()
    {
        foreach (var step in _steps.Where(s => !s.Done))
        {
            step.Complete();
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сбой живого отображения (сеть, лимиты Telegram) не должен ломать сам ответ.
            logger.LogWarning(ex, "Live reply update failed for chat {ChatId}", chatId);
        }
    }

    private static string DescribeTool(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Инструмент";
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = name.Split("__");
            return parts.Length >= 3 ? $"{parts[1]}: {parts[2]}" : name;
        }

        return name switch
        {
            "WebSearch" => "Поиск в интернете",
            "WebFetch" => "Читаю страницу",
            "Read" or "Glob" or "Grep" => "Смотрю файлы",
            "Edit" or "Write" => "Пишу файл",
            "Bash" => "Выполняю команду",
            _ => name,
        };
    }

    private sealed class Step(string title, bool isTool = false)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public bool Done { get; private set; }

        public bool IsTool { get; } = isTool;

        public void Complete()
        {
            if (!Done)
            {
                _watch.Stop();
                Done = true;
            }
        }

        public string Render()
        {
            var seconds = _watch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            return Done ? $"✓ {title} · {seconds}с" : $"⏳ {title}… {seconds}с";
        }
    }
}
