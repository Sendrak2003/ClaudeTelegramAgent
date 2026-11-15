using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Озвучка «с запасом»: пока агент ещё пишет ответ, законченные куски текста озвучиваются заранее и ложатся в кэш,
/// поэтому к моменту отправки текста голосовые почти готовы. Законченным считается любой кусок, кроме последнего:
/// последний ещё растёт. Если начался новый текстовый блок или вызван инструмент, написанное раньше уже не финальный
/// ответ, и заготовка отменяется (сервер озвучки при этом прекращает генерацию).
/// </summary>
public sealed class VoiceDraft(VoiceReplyService service, int maxChars, ChatId chatId, bool afterVoiceInput) : IAddonRun
{
    private readonly object _gate = new();
    private readonly HashSet<string> _started = [];
    private CancellationTokenSource _cts = new();
    private Task _chain = Task.CompletedTask;

    /// <summary>Вызывается с растущим текстом ответа; ничего не блокирует, озвучка идёт в фоне по очереди.</summary>
    public void OnAnswerGrowing(string answerSoFar)
    {
        var chunks = SpeechText.ToChunks(WithoutUnclosedCode(answerSoFar), maxChars);

        lock (_gate)
        {
            for (var i = 0; i < chunks.Count - 1; i++)
            {
                if (!_started.Add(chunks[i]))
                {
                    continue;
                }

                _chain = PreSynthesizeAfterAsync(_chain, chunks[i], _cts.Token);
            }
        }
    }

    /// <summary>Написанное раньше не финальное: отменяем заготовки и начинаем заново.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _started.Clear();
            _chain = Task.CompletedTask;
        }
    }

    /// <summary>Запуск агента упал или отменён: заготовки не нужны.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cts.Cancel();
        }
    }

    /// <summary>Текст отправлен: голосовые уходят следом, в фоне.</summary>
    public void Deliver(string finalText) => service.SpeakInBackground(chatId, finalText, afterVoiceInput);

    private async Task PreSynthesizeAfterAsync(Task previous, string chunk, CancellationToken ct)
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
        }

        if (!ct.IsCancellationRequested)
        {
            await service.PreSynthesizeAsync(chunk, ct);
        }
    }

    private static string WithoutUnclosedCode(string text)
    {
        var count = 0;
        for (var index = text.IndexOf("```", StringComparison.Ordinal); index >= 0; index = text.IndexOf("```", index + 3, StringComparison.Ordinal))
        {
            count++;
        }

        return count % 2 == 0 ? text : text[..text.LastIndexOf("```", StringComparison.Ordinal)];
    }
}
