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
    ILogger<AgentConversationService> logger)
{
    public async Task HandleMessageAsync(ChatId chatId, string text, string? audioPath, CancellationToken ct)
    {
        var sessionId = sessions.GetActiveSessionId(chatId);

        // Telegram гасит индикатор "печатает" через ~5 секунд, а ответ агента (особенно
        // с браузером) может идти минутами — поэтому обновляем его, пока claude работает.
        // Индикатор идёт и во время распознавания голосового.
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingLoop = KeepTypingAsync(chatId, typingCts.Token);

        // Статус с этапами и ответ, растущий по мере генерации (см. LiveReplyPresenter).
        var live = new LiveReplyPresenter(messageSender, chatId, logger);
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

            live.StartStep("Думаю", "размышление");
            var prompt = $"[Текущее время: {clock.NowLocal.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}]\n{text}";
            reply = await claude.SendAsync(chatId, prompt, sessionId, ct, live.OnProgress);
        }
        catch (Exception ex)
        {
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

        await live.FinishAsync(reply.Text, reply.Success, ct);
    }

    // Сбой распознавания не должен терять сообщение: модель всё равно получает промпт
    // и честно скажет пользователю, что голосовое не расшифровано.
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
                    // Сбой индикатора (сеть, лимиты) не должен ломать основной ответ.
                }

                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальное завершение: ответ готов, индикатор больше не нужен.
        }
    }

    public void Reset(ChatId chatId) => sessions.ClearActiveSession(chatId);

    public string? GetActiveSessionId(ChatId chatId) => sessions.GetActiveSessionId(chatId);

    public int NewChat(ChatId chatId) => sessions.CreateChat(chatId);

    public bool SwitchChat(ChatId chatId, int slotNumber) => sessions.SwitchChat(chatId, slotNumber);

    public IReadOnlyList<ChatSlotInfo> ListChats(ChatId chatId) => sessions.ListChats(chatId);
}
