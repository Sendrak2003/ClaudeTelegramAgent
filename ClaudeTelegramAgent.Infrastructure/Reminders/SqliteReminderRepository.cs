using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeTelegramAgent.Infrastructure.Reminders;

public sealed class SqliteReminderRepository : IReminderRepository
{
    private readonly string _connectionString;

    public SqliteReminderRepository(SqliteReminderOptions options)
    {
        var directory = Path.GetDirectoryName(options.DbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = options.DbPath }.ToString();
    }

    public async Task<Reminder> AddAsync(Reminder reminder, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO reminders (chat_id, remind_at, message, fired, created_at)
            VALUES (@ChatId, @RemindAt, @Message, 0, @CreatedAt);
            SELECT last_insert_rowid();
            """,
            new
            {
                ChatId = reminder.ChatId.ToString(),
                // Храним и сравниваем строго в UTC ("O" на DateTimeOffset сохраняет исходный
                // офсет, а SQLite сравнивает remind_at как обычный текст — строки с разными
                // офсетами не сортируются как реальное время. UtcDateTime убирает эту ловушку.
                RemindAt = reminder.RemindAtUtc.UtcDateTime.ToString("O"),
                reminder.Message,
                CreatedAt = reminder.CreatedAtUtc.UtcDateTime.ToString("O"),
            },
            cancellationToken: ct));

        reminder.AssignId(id);
        return reminder;
    }

    public async Task<IReadOnlyList<Reminder>> ListActiveAsync(ChatId chatId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        var rows = await connection.QueryAsync<ReminderRow>(new CommandDefinition(
            $"""
            SELECT {RowColumns}
            FROM reminders
            WHERE chat_id = @ChatId AND fired = 0
            ORDER BY remind_at ASC;
            """,
            new { ChatId = chatId.ToString() },
            cancellationToken: ct));

        return rows.Select(ToDomain).ToList();
    }

    public async Task<bool> CancelAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM reminders WHERE id = @Id AND fired = 0;",
            new { Id = id },
            cancellationToken: ct));

        return affected > 0;
    }

    public async Task<IReadOnlyList<Reminder>> PopDueAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction();

        var rows = (await connection.QueryAsync<ReminderRow>(new CommandDefinition(
            $"""
            SELECT {RowColumns}
            FROM reminders
            WHERE fired = 0 AND remind_at <= @Now
            ORDER BY remind_at ASC;
            """,
            new { Now = nowUtc.UtcDateTime.ToString("O") },
            transaction,
            cancellationToken: ct))).ToList();

        if (rows.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE reminders SET fired = 1 WHERE id IN @Ids;",
                new { Ids = rows.Select(r => r.Id).ToArray() },
                transaction,
                cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);

        var reminders = rows.Select(ToDomain).ToList();
        foreach (var reminder in reminders)
        {
            reminder.MarkFired();
        }

        return reminders;
    }

    private const string RowColumns =
        "id AS Id, chat_id AS ChatId, remind_at AS RemindAt, message AS Message, fired AS Fired, created_at AS CreatedAt";

    private static Reminder ToDomain(ReminderRow row) =>
        Reminder.Reconstruct(
            row.Id,
            new ChatId(long.Parse(row.ChatId)),
            DateTimeOffset.Parse(row.RemindAt),
            row.Message,
            row.Fired != 0,
            DateTimeOffset.Parse(row.CreatedAt));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS reminders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id TEXT NOT NULL,
                remind_at TEXT NOT NULL,
                message TEXT NOT NULL,
                fired INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """,
            cancellationToken: ct));

        return connection;
    }

    // POCO с обычными set-свойствами — под материализацию через Dapper.
    private sealed class ReminderRow
    {
        public long Id { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public string RemindAt { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public long Fired { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }
}
