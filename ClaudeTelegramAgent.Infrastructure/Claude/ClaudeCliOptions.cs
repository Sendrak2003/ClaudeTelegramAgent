namespace ClaudeTelegramAgent.Infrastructure.Claude;

/// <param name="OwnerIds">Владельцы (Agent__AllowedUserIds): работают в общей WorkDir с полным набором инструментов. Остальные (гости) получают отдельную папку и урезанные права. null — все считаются владельцами.</param>
/// <param name="GuestsDir">Корень папок гостей: для каждого своя подпапка по id чата. По умолчанию {WorkDir}/users.</param>
/// <param name="IncomingDir">Куда бот сохраняет присланные файлы: гостю открывается только подпапка его чата.</param>
public sealed record ClaudeCliOptions(
    string WorkDir,
    string Bin,
    string PermissionMode,
    int TimeoutSeconds,
    ClaudeAuthMode Auth,
    string? ApiKey,
    string? Model = null,
    int? MaxTurns = null,
    IReadOnlySet<long>? OwnerIds = null,
    string? GuestsDir = null,
    string? IncomingDir = null,
    bool GuestBrowser = true)
{
    public bool IsOwner(long chatId) => OwnerIds is null || OwnerIds.Contains(chatId);

    public string EffectiveGuestsDir => string.IsNullOrWhiteSpace(GuestsDir) ? Path.Combine(WorkDir, "users") : GuestsDir;
}
