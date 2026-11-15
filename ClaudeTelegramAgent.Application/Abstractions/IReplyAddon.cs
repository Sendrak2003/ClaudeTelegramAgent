using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

/// <summary>
/// Необязательное дополнение к ответу (голос, аватар): пользователь включает его командой, ядро о нём ничего не знает.
/// </summary>
public interface IReplyAddon
{
    /// <summary>Короткое имя для команды и настроек: «voice», «avatar».</summary>
    string Name { get; }

    /// <summary>Включено ли у пользователя, который ещё ничего не выбирал.</summary>
    bool EnabledByDefault { get; }

    /// <summary>Доступно ли дополнение вообще (глобальный рубильник в настройках).</summary>
    bool Available { get; }

    /// <summary>Участие дополнения в одном ответе; null — в этот раз не участвует.</summary>
    IAddonRun? Begin(ChatId chatId, bool afterVoiceInput);
}

/// <summary>Работа дополнения над одним ответом.</summary>
public interface IAddonRun
{
    /// <summary>Вызывается с растущим текстом ответа, пока агент ещё пишет.</summary>
    void OnAnswerGrowing(string textSoFar);

    /// <summary>Написанное раньше не финальное, начинаем заново.</summary>
    void Restart();

    /// <summary>Запуск агента упал или отменён.</summary>
    void Cancel();

    /// <summary>Итоговый текст ответа уже отправлен.</summary>
    void Deliver(string finalText);

    /// <summary>Агент перешёл к новому этапу («Думаю», «Ищу в интернете»): пока ответа нет, дополнение может это показать.</summary>
    void OnStage(string headline)
    {
    }
}
