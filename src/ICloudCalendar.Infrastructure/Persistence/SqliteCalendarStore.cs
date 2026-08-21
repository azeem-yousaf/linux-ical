using System.Data;
using ICloudCalendar.Core;
using Microsoft.Data.Sqlite;

namespace ICloudCalendar.Infrastructure.Persistence;

public sealed class SqliteCalendarStore(
    ISqliteConnectionFactory connections,
    ISqliteDatabaseInitializer database) : ICalendarStore, IAgendaReader
{
    private const int MaximumAgendaSize = 500;

    public async Task<SyncCheckpoint> GetCheckpointAsync(
        string calendarId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sync_token, last_successful_sync_ms
            FROM sync_checkpoints
            WHERE calendar_id = $calendar_id;
            """;
        command.Parameters.AddWithValue("$calendar_id", calendarId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SyncCheckpoint(null, null);
        }

        var syncToken = reader.IsDBNull(0) ? null : reader.GetString(0);
        var completedAt = reader.IsDBNull(1)
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
        return new SyncCheckpoint(syncToken, completedAt);
    }

    public async Task ApplyAsync(
        string calendarId,
        IReadOnlyList<CalendarEvent> upserts,
        IReadOnlyList<string> deletions,
        bool replaceExisting,
        string? syncToken,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletions);
        if (upserts.Any(item => !StringComparer.Ordinal.Equals(item.CalendarId, calendarId)))
        {
            throw new ArgumentException("Every event must belong to the calendar being updated.", nameof(upserts));
        }

        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            if (replaceExisting)
            {
                await DeleteCalendarAsync(connection, transaction, calendarId, cancellationToken);
            }

            foreach (var remoteId in deletions)
            {
                await DeleteAsync(connection, transaction, calendarId, remoteId, cancellationToken);
            }

            foreach (var sourceRemoteId in upserts
                .Select(item => item.SourceRemoteId ?? item.RemoteId)
                .Distinct(StringComparer.Ordinal))
            {
                await DeleteAsync(connection, transaction, calendarId, sourceRemoteId, cancellationToken);
            }

            foreach (var calendarEvent in upserts)
            {
                await UpsertAsync(connection, transaction, calendarEvent.Validate(), cancellationToken);
            }

            await SaveCheckpointAsync(
                connection,
                transaction,
                calendarId,
                syncToken,
                completedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetAgendaAsync(
        DateTimeOffset startsBefore,
        DateTimeOffset endsAfter,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (startsBefore <= endsAfter)
        {
            throw new ArgumentException("The agenda range end must be after its start.", nameof(startsBefore));
        }

        if (limit is < 1 or > MaximumAgendaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"Agenda size must be between 1 and {MaximumAgendaSize}.");
        }

        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT calendar_id, remote_id, etag, title, starts_at_ms, ends_at_ms,
                   is_all_day, location, notes, source_remote_id
            FROM calendar_events
            WHERE starts_at_ms < $range_end AND ends_at_ms > $range_start
            ORDER BY starts_at_ms, ends_at_ms, title
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$range_end", startsBefore.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$range_start", endsAfter.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<CalendarEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new CalendarEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return events;
    }

    private static async Task DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string calendarId,
        string remoteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM calendar_events
            WHERE calendar_id = $calendar_id
              AND (remote_id = $remote_id OR source_remote_id = $remote_id);
            """;
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        command.Parameters.AddWithValue("$remote_id", remoteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCalendarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string calendarId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM calendar_events WHERE calendar_id = $calendar_id;";
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalendarEvent item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_events (
                calendar_id, remote_id, etag, title, starts_at_ms, ends_at_ms,
                is_all_day, location, notes, source_remote_id)
            VALUES (
                $calendar_id, $remote_id, $etag, $title, $starts_at, $ends_at,
                $is_all_day, $location, $notes, $source_remote_id)
            ON CONFLICT(calendar_id, remote_id) DO UPDATE SET
                etag = excluded.etag,
                title = excluded.title,
                starts_at_ms = excluded.starts_at_ms,
                ends_at_ms = excluded.ends_at_ms,
                is_all_day = excluded.is_all_day,
                location = excluded.location,
                notes = excluded.notes,
                source_remote_id = excluded.source_remote_id;
            """;
        command.Parameters.AddWithValue("$calendar_id", item.CalendarId);
        command.Parameters.AddWithValue("$remote_id", item.RemoteId);
        command.Parameters.AddWithValue("$etag", item.ETag);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$starts_at", item.StartsAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$ends_at", item.EndsAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$is_all_day", item.IsAllDay);
        command.Parameters.AddWithValue("$location", (object?)item.Location ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_remote_id", (object?)item.SourceRemoteId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SaveCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string calendarId,
        string? syncToken,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_checkpoints (calendar_id, sync_token, last_successful_sync_ms)
            VALUES ($calendar_id, $sync_token, $completed_at)
            ON CONFLICT(calendar_id) DO UPDATE SET
                sync_token = excluded.sync_token,
                last_successful_sync_ms = excluded.last_successful_sync_ms;
            """;
        command.Parameters.AddWithValue("$calendar_id", calendarId);
        command.Parameters.AddWithValue("$sync_token", (object?)syncToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed_at", completedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
