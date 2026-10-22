using Microsoft.Data.Sqlite;

namespace MeetingRecall.Infrastructure.Persistence;

internal interface ISqliteConnectionFactory
{
    SqliteConnection OpenConnection();
}
