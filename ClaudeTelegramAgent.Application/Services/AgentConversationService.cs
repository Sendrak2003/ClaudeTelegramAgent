using System.Globalization;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTelegramAgent.Application.Services;

public sealed class AgentConversationService(
    IClaudeAgentClient claude,
    ISessionStore sessions,
    IMessageSender messageSender,
    IAgentClock clock,
    ISpeechTranscriber transcriber,
    IEnumerable<IReplyAddon> addons,
    IAddonSettings addonSettings,
    ILogger<AgentConversationService> logger)
{
    private readonly IReplyAddon[] _addons = addons.ToArray();

    public IReadOnlyList<IReplyAddon> Addons => _addons;

    public bool IsAddonEnabled(ChatId chatId, IReplyAddon addon) =>
        addon.Available && (addonSettings.Get(chatId, addon.Name) ?? addon.EnabledByDefault);

    public void SetAddon(ChatId chatId, IReplyAddon addon, bool enabled) => addonSettings.Set(chatId, addon.Name, enabled);

    public async Task HandleMessageAsync(ChatId chatId, string text, string? audioPath, CancellationToken ct)
    {
        var sessionId = sessions.GetActiveSessionId(chatId);

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingLoop = KeepTypingAsync(chatId, typingCts.Token);

        var runs = _addons
            .Where(a => IsAddonEnabled(chatId, a))
            .Select(a => a.Begin(chatId, audioPath is not null))
            .OfType<IAddonRun>()
            .ToList();
        var live = new LiveReplyPresenter(messageSender, chatId, logger, runs);
        live.Start(ct);

        ClaudeAgentReply reply;
        try
        {
            if (audioPath is not null)
            {
                live.StartStep("Расшифровываю аудио", "расшифровка речи");
                text = await AppendTranscriptAsync(text, audioPath, ct);
                live.CompleteRunning();
            }

            live.StartStep("Думаю", null);
            var prompt = $"[Текущее время: {clock.NowLocal.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}]\n{text}";
            reply = await claude.SendAsync(chatId, prompt, sessionId, ct, live.OnProgress);
        }
        catch (Exception ex)
        {
            runs.ForEach(r => r.Cancel());
            await live.FailAsync(ex is OperationCanceledException ? "Остановлено." : "Не удалось получить ответ.");
            throw;
        }
        finally
        {
            typingCts.Cancel();
            await typingLoop;
        }

        if (!string.IsNullOrEmpty(reply.SessionId))
        {
            sessions.SetActiveSessionId(chatId, reply.SessionId);
        }

        await live.FinishAsync(reply.Text, ct);

        var spoken = ReplyButtons.StripForStreaming(reply.Text);
        runs.ForEach(r => r.Deliver(spoken));
    }

    private async Task<string> AppendTranscriptAsync(string text, string audioPath, CancellationToken ct)
    {
        string line;
        try
        {
            var transcript = await transcriber.TranscribeAsync(audioPath, ct);
            line = string.IsNullOrWhiteSpace(transcript)
                ? "[В голосовом сообщении речь не обнаружена]"
                : $"[Расшифровка голосового сообщения: {transcript}]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Voice transcription failed for {AudioPath}", audioPath);
            line = $"[Не удалось расшифровать голосовое сообщение: {ex.Message}]";
        }

        return string.IsNullOrWhiteSpace(text) ? line : $"{text.TrimEnd()}\n{line}";
    }

    private async Task KeepTypingAsync(ChatId chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await messageSender.SendTypingAsync(chatId, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Reset(ChatId chatId) => sessions.ClearActiveSession(chatId);

    public string? GetActiveSessionId(ChatId chatId) => sessions.GetActiveSessionId(chatId);

    public int NewChat(ChatId chatId) => sessions.CreateChat(chatId);

    public bool SwitchChat(ChatId chatId, int slotNumber) => sessions.SwitchChat(chatId, slotNumber);

    public IReadOnlyList<ChatSlotInfo> ListChats(ChatId chatId) => sessions.ListChats(chatId);
}
