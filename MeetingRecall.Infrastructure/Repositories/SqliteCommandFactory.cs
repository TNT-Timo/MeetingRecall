using Microsoft.Data.Sqlite;

namespace MeetingRecall.Infrastructure.Repositories;

internal static class SqliteCommandFactory
{
    public static SqliteCommand Create(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction = null)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }
}
