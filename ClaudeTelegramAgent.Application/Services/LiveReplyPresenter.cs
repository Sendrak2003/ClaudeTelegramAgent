using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Живой черновик ответа: пока агент работает, Telegram показывает статус («Думаю» и строку с таймером
/// и пройденными этапами), а как только пошёл текст ответа, статус заменяется растущим ответом.
/// Итоговое сообщение с форматированием отправляется отдельно, когда ответ готов.
/// </summary>
public sealed class LiveReplyPresenter(IMessageSender sender, ChatId chatId, ILogger logger)
{
    private static readonly TimeSpan FirstFlushDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);
    private const int MaxVisibleStages = 4;
    private const int MaxStreamedLength = 3800;

    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private readonly StringBuilder _answer = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly int _draftId = Random.Shared.Next(1, int.MaxValue);

    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private string _lastText = string.Empty;

    public void Start(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(_loopCts.Token);
    }

    public void StartStep(string headline, string? stage)
    {
        lock (_gate)
        {
            CompleteRunningUnsafe();
            _steps.Add(new Step(headline, stage));
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
                        _steps.Add(Step.Thinking());
                    }

                    break;

                case AgentProgressKind.ToolStarted:
                    // Служебные инструменты (подгрузка других инструментов и т.п.) пользователю не показываем.
                    if (Step.ForTool(progress.Text) is { } toolStep)
                    {
                        CompleteRunningUnsafe();
                        _steps.Add(toolStep);
                    }

                    break;

                case AgentProgressKind.ToolFinished:
                    CompleteRunningUnsafe();
                    _steps.Add(Step.Thinking());
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

    public async Task FinishAsync(string finalText, CancellationToken ct)
    {
        await StopLoopAsync();
        await sender.SendAsync(chatId, finalText, ct);
    }

    // Запуск упал или отменён: черновик со «статусом» нельзя оставлять висеть, поэтому шлём короткое сообщение.
    public async Task FailAsync(string reason)
    {
        await StopLoopAsync();
        await SafeAsync(() => sender.SendAsync(chatId, reason, CancellationToken.None));
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
        string text;
        lock (_gate)
        {
            text = RenderUnsafe();
        }

        if (text.Length == 0 || text == _lastText)
        {
            return;
        }

        await sender.StreamDraftAsync(chatId, _draftId, text, ct);
        _lastText = text;
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

    private string RenderUnsafe()
    {
        var answer = _answer.ToString().TrimEnd();
        if (answer.Length > 0)
        {
            return answer.Length > MaxStreamedLength ? "…" + answer[^MaxStreamedLength..] : answer;
        }

        if (_steps.Count == 0)
        {
            return string.Empty;
        }

        var running = _steps.LastOrDefault(s => !s.Done) ?? Step.Thinking();
        var stages = _steps.Where(s => s.Stage is not null).Select(s => s.Done ? $"{s.Stage} ✓" : $"{s.Stage}…").ToList();
        var shown = stages.Count > MaxVisibleStages ? stages.Skip(stages.Count - MaxVisibleStages).Prepend("…") : stages;

        var seconds = _total.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        var details = stages.Count > 0 ? $"{seconds}с · {string.Join(" · ", shown)}" : $"{seconds}с";
        return $"{running.Headline}\n{details}";
    }

    private void CompleteRunningUnsafe()
    {
        foreach (var step in _steps.Where(s => !s.Done))
        {
            step.Done = true;
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

    private sealed class Step(string headline, string? stage)
    {
        public string Headline { get; } = headline;

        public string? Stage { get; } = stage;

        public bool Done { get; set; }

        public static Step Thinking() => new("Думаю", null);

        // null — служебный инструмент, в статусе его не показываем.
        public static Step? ForTool(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new Step("Использую инструмент", "инструмент");
            }

            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = name.Split("__");
                var tool = parts.Length >= 3 ? parts[2] : name;
                return new Step($"Использую {tool}", tool);
            }

            return name switch
            {
                "ToolSearch" or "Skill" or "TodoWrite" => null,
                "WebSearch" => new Step("Ищу в интернете", "поиск в интернете"),
                "WebFetch" => new Step("Читаю страницу", "чтение страницы"),
                "Read" or "Glob" or "Grep" => new Step("Смотрю файлы", "поиск по файлам"),
                "Edit" or "Write" => new Step("Пишу файл", "запись файла"),
                "Bash" => new Step("Выполняю команду", "выполнение команды"),
                _ => new Step($"Использую {name}", name),
            };
        }
    }
}
