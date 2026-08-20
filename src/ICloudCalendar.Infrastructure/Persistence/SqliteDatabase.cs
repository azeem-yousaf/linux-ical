using Microsoft.Data.Sqlite;

namespace ICloudCalendar.Infrastructure.Persistence;

public interface ISqliteConnectionFactory
{
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);
}

public interface ISqliteDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public sealed class SqliteConnectionFactory(string connectionString) : ISqliteConnectionFactory
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

public sealed class SqliteDatabaseInitializer(ISqliteConnectionFactory connections) : ISqliteDatabaseInitializer, IDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await connections.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS calendar_events (
                    calendar_id TEXT NOT NULL,
                    remote_id TEXT NOT NULL,
                    etag TEXT NOT NULL,
                    title TEXT NOT NULL,
                    starts_at_ms INTEGER NOT NULL,
                    ends_at_ms INTEGER NOT NULL,
                    is_all_day INTEGER NOT NULL,
                    location TEXT NULL,
                    notes TEXT NULL,
                    source_remote_id TEXT NULL,
                    PRIMARY KEY (calendar_id, remote_id)
                );
                CREATE INDEX IF NOT EXISTS ix_calendar_events_agenda
                    ON calendar_events (starts_at_ms, ends_at_ms);
                CREATE TABLE IF NOT EXISTS sync_checkpoints (
                    calendar_id TEXT PRIMARY KEY,
                    sync_token TEXT NULL,
                    last_successful_sync_ms INTEGER NULL
                );
                CREATE TABLE IF NOT EXISTS calendar_accounts (
                    account_id TEXT PRIMARY KEY,
                    user_name TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS calendar_subscriptions (
                    calendar_id TEXT PRIMARY KEY,
                    account_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    remote_uri TEXT NOT NULL,
                    color TEXT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    FOREIGN KEY (account_id) REFERENCES calendar_accounts(account_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_calendar_subscriptions_account
                    ON calendar_subscriptions (account_id, is_enabled);
                CREATE TABLE IF NOT EXISTS projection_rebuilds (
                    calendar_id TEXT PRIMARY KEY,
                    last_completed_ms INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            command.CommandText = "PRAGMA table_info(calendar_events);";
            var hasSourceColumn = false;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    hasSourceColumn |= StringComparer.Ordinal.Equals(reader.GetString(1), "source_remote_id");
                }
            }

            if (!hasSourceColumn)
            {
                command.CommandText = "ALTER TABLE calendar_events ADD COLUMN source_remote_id TEXT NULL;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            command.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_calendar_events_source
                    ON calendar_events (calendar_id, source_remote_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public void Dispose() => _initializationLock.Dispose();
}
