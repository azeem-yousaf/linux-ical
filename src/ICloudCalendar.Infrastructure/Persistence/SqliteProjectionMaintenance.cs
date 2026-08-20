using System.Data;
using ICloudCalendar.Core;
using Microsoft.Data.Sqlite;

namespace ICloudCalendar.Infrastructure.Persistence;

public sealed class SqliteProjectionMaintenance(
    ISqliteConnectionFactory connections,
    ISqliteDatabaseInitializer database,
    TimeProvider timeProvider) : IProjectionMaintenance
{
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromDays(30);

    public async Task<bool> PrepareIfDueAsync(
        string calendarId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var lastCompleted = await GetLastCompletedAsync(connection, transaction, calendarId, cancellationToken);
        if (lastCompleted is not null && timeProvider.GetUtcNow() - lastCompleted < RebuildInterval)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_checkpoints WHERE calendar_id = $calendar_id;";
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkCompletedAsync(
        string calendarId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projection_rebuilds (calendar_id, last_completed_ms)
            VALUES ($calendar_id, $completed_ms)
            ON CONFLICT(calendar_id) DO UPDATE SET last_completed_ms = excluded.last_completed_ms;
            """;
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        command.Parameters.AddWithValue("$completed_ms", timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset?> GetLastCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string calendarId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_completed_ms FROM projection_rebuilds WHERE calendar_id = $calendar_id;";
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    }
}
