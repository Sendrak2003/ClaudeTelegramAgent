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
public sealed class LiveReplyPresenter(IMessageSender sender, ChatId chatId, ILogger logger, IReadOnlyList<IAddonRun>? addons = null)
{
    private static readonly TimeSpan FirstFlushDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);
    private const int MaxVisibleStages = 4;
    private const int MaxStreamedLength = 3800;
    private static readonly char[] SentenceEnds = ['.', '!', '?', '…', '\n'];

    private const string ThinkingIcon = "5456125285160226779";
    private const string SearchIcon = "5537244684667060232";
    private const string FilesIcon = "5537186088428240919";

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
            _steps.Add(new Step(headline, stage, ThinkingIcon));
            NotifyStage(headline);
        }
    }

    public void CompleteRunning()
    {
        lock (_gate)
        {
            CompleteRunningUnsafe();
        }
    }

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
                    var toolStep = Step.ForTool(progress.Text);
                    if (toolStep is not null)
                    {
                        CompleteRunningUnsafe();
                        _steps.Add(toolStep);
                    }

                    RestartAddons();
                    if (toolStep is not null)
                    {
                        NotifyStage(toolStep.Headline);
                    }

                    break;

                case AgentProgressKind.ToolFinished:
                    if (progress.Text == "error")
                    {
                        foreach (var step in _steps.Where(s => !s.Done && s.Stage is not null))
                        {
                            step.Failed = true;
                        }
                    }

                    CompleteRunningUnsafe();
                    _steps.Add(Step.Thinking());
                    break;

                case AgentProgressKind.TextStarted:
                    CompleteRunningUnsafe();
                    _answer.Clear();
                    RestartAddons();
                    break;

                case AgentProgressKind.TextDelta:
                    _answer.Append(progress.Text);

                    if (addons is { Count: > 0 } && progress.Text?.IndexOfAny(SentenceEnds) >= 0)
                    {
                        var soFar = ReplyButtons.StripForStreaming(_answer.ToString());
                        foreach (var addon in addons)
                        {
                            addon.OnAnswerGrowing(soFar);
                        }
                    }

                    break;
            }
        }
    }

    private void NotifyStage(string headline)
    {
        if (addons is null)
        {
            return;
        }

        foreach (var addon in addons)
        {
            addon.OnStage(headline);
        }
    }

    private void RestartAddons()
    {
        if (addons is null)
        {
            return;
        }

        foreach (var addon in addons)
        {
            addon.Restart();
        }
    }

    public async Task FinishAsync(string finalText, CancellationToken ct)
    {
        await StopLoopAsync();
        var (text, buttons) = ReplyButtons.Extract(finalText);
        if (buttons.Count > 0)
        {
            await sender.SendWithButtonsAsync(chatId, text, buttons, ct);
        }
        else
        {
            await sender.SendAsync(chatId, text, ct);
        }
    }

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
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        string text;
        string? icon;
        lock (_gate)
        {
            (text, icon) = RenderUnsafe();
        }

        if (text.Length == 0 || text == _lastText)
        {
            return;
        }

        await sender.StreamDraftAsync(chatId, _draftId, text, ct, icon);
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

    private (string Text, string? Icon) RenderUnsafe()
    {
        var answer = ReplyButtons.StripForStreaming(_answer.ToString(), out var hadTable);
        if (hadTable)
        {
            answer = (answer + "\n\nСобираю таблицу…").TrimStart();
        }

        if (answer.Length > 0)
        {
            return (answer.Length > MaxStreamedLength ? "…" + answer[^MaxStreamedLength..] : answer, null);
        }

        if (_steps.Count == 0)
        {
            return (string.Empty, null);
        }

        var running = _steps.LastOrDefault(s => !s.Done) ?? Step.Thinking();
        var stages = _steps.Where(s => s.Stage is not null).Select(s => s.Render()).ToList();
        var shown = stages.Count > MaxVisibleStages ? stages.Skip(stages.Count - MaxVisibleStages).Prepend("…") : stages;

        var seconds = _total.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        var details = stages.Count > 0 ? $"{seconds}с · {string.Join(" · ", shown)}" : $"{seconds}с";
        return ($"{running.Headline}\n{details}", running.IconId);
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
            logger.LogWarning(ex, "Live reply update failed for chat {ChatId}", chatId);
        }
    }

    private sealed class Step(string headline, string? stage, string? iconId)
    {
        public string Headline { get; } = headline;

        public string? Stage { get; } = stage;

        public string? IconId { get; } = iconId;

        public bool Done { get; set; }

        public bool Failed { get; set; }

        public string Render() => Done ? (Failed ? $"{Stage} ✗" : $"{Stage} ✓") : $"{Stage}…";

        public static Step Thinking() => new("Думаю", null, ThinkingIcon);

        public static Step? ForTool(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new Step("Использую инструмент", "инструмент", ThinkingIcon);
            }

            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = name.Split("__");
                var tool = parts.Length >= 3 ? parts[2] : name;
                return tool switch
                {
                    "add_reminder" => new Step("Создаю напоминание", "создание напоминания", ThinkingIcon),
                    "list_reminders" => new Step("Смотрю напоминания", "список напоминаний", ThinkingIcon),
                    "cancel_reminder" => new Step("Отменяю напоминание", "отмена напоминания", ThinkingIcon),
                    "send_file" => new Step("Отправляю файл", "отправка файла", FilesIcon),
                    "browser_navigate" => new Step("Открываю страницу", "открытие страницы", SearchIcon),
                    _ when tool.StartsWith("browser_", StringComparison.Ordinal) => new Step("Работаю в браузере", "браузер", SearchIcon),
                    _ => new Step($"Использую {tool}", tool, ThinkingIcon),
                };
            }

            return name switch
            {
                "ToolSearch" or "Skill" or "TodoWrite" => null,
                "WebSearch" => new Step("Ищу в интернете", "поиск в интернете", SearchIcon),
                "WebFetch" => new Step("Читаю страницу", "чтение страницы", SearchIcon),
                "Read" or "Glob" or "Grep" => new Step("Смотрю файлы", "поиск по файлам", FilesIcon),
                "Edit" or "Write" => new Step("Пишу файл", "запись файла", FilesIcon),
                "Bash" => new Step("Выполняю команду", "выполнение команды", ThinkingIcon),
                _ => new Step($"Использую {name}", name, ThinkingIcon),
            };
        }
    }
}
