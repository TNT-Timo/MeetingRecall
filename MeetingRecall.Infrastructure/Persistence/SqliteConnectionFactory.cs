using Microsoft.Data.Sqlite;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Persistence;

internal sealed class SqliteConnectionFactory(AppDataPaths paths) : ISqliteConnectionFactory
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabaseFilePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();

        return connection;
    }
}
