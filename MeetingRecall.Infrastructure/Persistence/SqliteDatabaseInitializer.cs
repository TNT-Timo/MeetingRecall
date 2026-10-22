using Microsoft.Data.Sqlite;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Persistence;

internal sealed class SqliteDatabaseInitializer(
    AppDataPaths paths,
    ISqliteConnectionFactory connectionFactory) : IDatabaseInitializer
{
    private readonly object _gate = new();
    private bool _initialized;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            InitializeCore();
            _initialized = true;
        }
    }

    public void Reinitialize()
    {
        lock (_gate)
        {
            _initialized = false;
            InitializeCore();
            _initialized = true;
        }
    }

    private void InitializeCore()
    {
        paths.EnsureCreated();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        ConfigureDatabase(connection);
        EnsureMigrationTable(connection);
        ApplyPendingMigrations(connection);
        CleanupConversationStorage(connection);
    }

    private static void ConfigureDatabase(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
    }

    private static void EnsureMigrationTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SqliteSchema.CreateMigrationTable;
        command.ExecuteNonQuery();
    }

    private static void ApplyPendingMigrations(SqliteConnection connection)
    {
        int currentVersion = ReadCurrentVersion(connection);
        if (currentVersion > SqliteSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The database schema version {currentVersion} is newer than this application supports ({SqliteSchema.CurrentVersion}).");
        }

        if (currentVersion < 1)
        {
            ApplyMigration(connection, SqliteSchema.Migration1);
            currentVersion = 1;
        }

        if (currentVersion < 2)
        {
            ApplyMigration(connection, SqliteSchema.Migration2);
            currentVersion = 2;
        }

        if (currentVersion < 3)
        {
            ApplyMigration(connection, SqliteSchema.Migration3);
            currentVersion = 3;
        }

        if (currentVersion < 4)
        {
            ApplyMigration(connection, SqliteSchema.Migration4);
            currentVersion = 4;
        }

        if (currentVersion < 5)
        {
            ApplyMigration(connection, SqliteSchema.Migration5);
            currentVersion = 5;
        }

        if (currentVersion < 6)
        {
            ApplyMigration(connection, SqliteSchema.Migration6);
            currentVersion = 6;
        }

        if (currentVersion < 7)
        {
            ApplyMigration(connection, SqliteSchema.Migration7);
            currentVersion = 7;
        }

        if (currentVersion < 8)
        {
            ApplyMigration(connection, SqliteSchema.Migration8);
            currentVersion = 8;
        }

        if (currentVersion < 9)
        {
            ApplyMigration(connection, SqliteSchema.Migration9);
        }
    }

    private void CleanupConversationStorage(SqliteConnection connection)
    {
        HashSet<Guid> existingConversationIds = ReadConversationIds(connection);
        RecoverPendingConversationDeletions(existingConversationIds);

        if (!Directory.Exists(paths.MeetingsDirectory))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(
                     paths.MeetingsDirectory))
        {
            string directoryName = Path.GetFileName(directory);
            if (!Guid.TryParseExact(directoryName, "N", out Guid conversationId)
                || existingConversationIds.Contains(conversationId))
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static HashSet<Guid> ReadConversationIds(SqliteConnection connection)
    {
        var ids = new HashSet<Guid>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM conversations;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(SqliteValues.ToGuid(reader.GetString(0)));
        }

        return ids;
    }

    private void RecoverPendingConversationDeletions(
        IReadOnlySet<Guid> existingConversationIds)
    {
        string pendingDirectory = Path.Combine(
            paths.TempDirectory,
            "PendingConversationDeletion");
        if (!Directory.Exists(pendingDirectory))
        {
            return;
        }

        foreach (string stagedDirectory in Directory.EnumerateDirectories(
                     pendingDirectory))
        {
            string name = Path.GetFileName(stagedDirectory);
            if (name.Length < 32
                || !Guid.TryParseExact(name[..32], "N", out Guid conversationId))
            {
                TryDeleteDirectory(stagedDirectory);
                continue;
            }

            string meetingDirectory = paths.GetMeetingDirectory(conversationId);
            if (existingConversationIds.Contains(conversationId)
                && !Directory.Exists(meetingDirectory))
            {
                try
                {
                    Directory.Move(stagedDirectory, meetingDirectory);
                }
                catch
                {
                    // The next startup retries recovery without losing data.
                }

                continue;
            }

            TryDeleteDirectory(stagedDirectory);
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(pendingDirectory).Any())
            {
                Directory.Delete(pendingDirectory);
            }
        }
        catch
        {
            // A later startup retries cleanup.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Locked legacy files are retried on the next application start.
        }
    }

    private static void ApplyMigration(SqliteConnection connection, string sql)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static int ReadCurrentVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        object? result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }
}
