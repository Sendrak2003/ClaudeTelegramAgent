using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

/// <summary>Выбор пользователя по дополнениям; null — ещё не выбирал, действует значение по умолчанию.</summary>
public interface IAddonSettings
{
    bool? Get(ChatId chatId, string addonName);

    void Set(ChatId chatId, string addonName, bool enabled);
}
