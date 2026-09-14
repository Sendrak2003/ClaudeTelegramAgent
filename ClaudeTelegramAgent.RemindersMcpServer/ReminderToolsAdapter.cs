using System.ComponentModel;
using System.Globalization;
using ClaudeTelegramAgent.Application.Services;
using ModelContextProtocol.Server;

namespace ClaudeTelegramAgent.RemindersMcpServer;

[McpServerToolType]
public sealed class ReminderToolsAdapter
{
    [McpServerTool(Name = "add_reminder")]
    [Description("Создаёт напоминание для текущего чата на заданное время.")]
    public static async Task<string> AddReminder(
        ReminderService reminderService,
        CurrentChatContext chatContext,
        [Description("Время напоминания в формате ISO 8601, например 2026-08-22T18:30:00+03:00")] string whenIso,
        [Description("Текст напоминания")] string message,
        CancellationToken ct)
    {
        var remindAt = DateTimeOffset.Parse(whenIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        var reminder = await reminderService.AddAsync(chatContext.ChatId, remindAt, message, ct);
        return $"Напоминание #{reminder.Id} создано на {remindAt:O}.";
    }

    [McpServerTool(Name = "list_reminders")]
    [Description("Возвращает список активных напоминаний для текущего чата.")]
    public static async Task<string> ListReminders(
        ReminderService reminderService,
        CurrentChatContext chatContext,
        CancellationToken ct)
    {
        var reminders = await reminderService.ListActiveAsync(chatContext.ChatId, ct);
        if (reminders.Count == 0)
        {
            return "Активных напоминаний нет.";
        }

        return string.Join(
            Environment.NewLine,
            reminders.Select(r => $"#{r.Id} — {r.RemindAtUtc:O} — {r.Message}"));
    }

    [McpServerTool(Name = "cancel_reminder")]
    [Description("Отменяет напоминание по его идентификатору.")]
    public static async Task<string> CancelReminder(
        ReminderService reminderService,
        CurrentChatContext chatContext,
        [Description("Идентификатор напоминания")] long id,
        CancellationToken ct)
    {
        var cancelled = await reminderService.CancelAsync(id, ct);
        return cancelled ? $"Напоминание #{id} отменено." : $"Напоминание #{id} не найдено или уже сработало.";
    }
}
