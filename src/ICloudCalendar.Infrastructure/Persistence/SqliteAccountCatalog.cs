using System.Data;
using ICloudCalendar.Core;
using Microsoft.Data.Sqlite;

namespace ICloudCalendar.Infrastructure.Persistence;

public sealed class SqliteAccountCatalog(
    ISqliteConnectionFactory connections,
    ISqliteDatabaseInitializer database) : IAccountCatalog
{
    public async Task SaveAsync(
        CalendarAccount account,
        IReadOnlyList<CalendarSubscription> calendars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendars);
        if (calendars.Any(item => !StringComparer.Ordinal.Equals(item.AccountId, account.Id)))
        {
            throw new ArgumentException("Every calendar must belong to the saved account.", nameof(calendars));
        }

        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await UpsertAccountAsync(connection, transaction, account, cancellationToken);
            await DeleteRemovedCalendarStateAsync(
                connection,
                transaction,
                account.Id,
                calendars.Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
                cancellationToken);
            await DeleteCalendarsAsync(connection, transaction, account.Id, cancellationToken);
            foreach (var calendar in calendars)
            {
                await UpsertCalendarAsync(connection, transaction, calendar, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<CalendarAccount?> GetAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, user_name FROM calendar_accounts WHERE account_id = $id;";
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CalendarAccount(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task<IReadOnlyList<CalendarSubscription>> GetCalendarsAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT calendar_id, account_id, display_name, remote_uri, color, is_enabled
            FROM calendar_subscriptions
            WHERE account_id = $account_id
            ORDER BY display_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$account_id", accountId);
        var result = new List<CalendarSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCalendar(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<CalendarSubscription>> GetAllCalendarsAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT calendar_id, account_id, display_name, remote_uri, color, is_enabled
            FROM calendar_subscriptions
            ORDER BY account_id, display_name COLLATE NOCASE;
            """;
        var result = new List<CalendarSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCalendar(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<CalendarAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, user_name FROM calendar_accounts ORDER BY user_name COLLATE NOCASE;";
        var result = new List<CalendarAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CalendarAccount(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public async Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await database.InitializeAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM calendar_events
                WHERE calendar_id IN (
                    SELECT calendar_id FROM calendar_subscriptions WHERE account_id = $account_id
                );
                DELETE FROM sync_checkpoints
                WHERE calendar_id IN (
                    SELECT calendar_id FROM calendar_subscriptions WHERE account_id = $account_id
                );
                DELETE FROM projection_rebuilds
                WHERE calendar_id IN (
                    SELECT calendar_id FROM calendar_subscriptions WHERE account_id = $account_id
                );
                DELETE FROM calendar_subscriptions WHERE account_id = $account_id;
                DELETE FROM calendar_accounts WHERE account_id = $account_id;
                """;
            command.Parameters.AddWithValue("$account_id", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static CalendarSubscription ReadCalendar(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        new Uri(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetBoolean(5));

    private static async Task UpsertAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_accounts (account_id, user_name) VALUES ($id, $user_name)
            ON CONFLICT(account_id) DO UPDATE SET user_name = excluded.user_name;
            """;
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$user_name", account.UserName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCalendarsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM calendar_subscriptions WHERE account_id = $account_id;";
        command.Parameters.AddWithValue("$account_id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteRemovedCalendarStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        HashSet<string> retainedCalendarIds,
        CancellationToken cancellationToken)
    {
        var existingCalendarIds = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT calendar_id FROM calendar_subscriptions WHERE account_id = $account_id;";
            select.Parameters.AddWithValue("$account_id", accountId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingCalendarIds.Add(reader.GetString(0));
            }
        }

        foreach (var calendarId in existingCalendarIds.Where(id => !retainedCalendarIds.Contains(id)))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM calendar_events WHERE calendar_id = $calendar_id;
                DELETE FROM sync_checkpoints WHERE calendar_id = $calendar_id;
                DELETE FROM projection_rebuilds WHERE calendar_id = $calendar_id;
                """;
            delete.Parameters.AddWithValue("$calendar_id", calendarId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertCalendarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalendarSubscription calendar,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_subscriptions (
                calendar_id, account_id, display_name, remote_uri, color, is_enabled)
            VALUES ($id, $account_id, $display_name, $remote_uri, $color, $is_enabled)
            ON CONFLICT(calendar_id) DO UPDATE SET
                account_id = excluded.account_id,
                display_name = excluded.display_name,
                remote_uri = excluded.remote_uri,
                color = excluded.color,
                is_enabled = excluded.is_enabled;
            """;
        command.Parameters.AddWithValue("$id", calendar.Id);
        command.Parameters.AddWithValue("$account_id", calendar.AccountId);
        command.Parameters.AddWithValue("$display_name", calendar.DisplayName);
        command.Parameters.AddWithValue("$remote_uri", calendar.RemoteUri.AbsoluteUri);
        command.Parameters.AddWithValue("$color", (object?)calendar.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_enabled", calendar.IsEnabled);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
