using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Application.Abstractions;

/// <summary>Отправляет сообщение с кнопкой, открывающей мини-приложение Telegram.</summary>
public interface IWebAppLauncher
{
    Task SendWebAppButtonAsync(ChatId chatId, string text, string buttonText, string url, CancellationToken ct);
}
