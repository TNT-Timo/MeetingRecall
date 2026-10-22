using System.Data.Common;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteConversationGroupRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : IConversationGroupRepository
{
    public async Task<ConversationGroup?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(id, nameof(id));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = CreateSelectCommand(connection, "WHERE id = @id");
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadGroup(reader)
            : null;
    }

    public async Task<IReadOnlyList<ConversationGroup>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = CreateSelectCommand(connection, "ORDER BY name COLLATE NOCASE, id");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<ConversationGroup>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(ReadGroup(reader));
        }

        return groups;
    }

    public async Task UpsertAsync(
        ConversationGroup group,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        databaseInitializer.Initialize();

        const string sql = """
            INSERT INTO conversation_groups (
                id,
                name,
                needs_naming,
                created_at_utc,
                updated_at_utc)
            VALUES (
                @id,
                @name,
                @needsNaming,
                @createdAtUtc,
                @updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                needs_naming = excluded.needs_naming,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(group.Id));
        command.Parameters.AddWithValue("@name", group.Name);
        command.Parameters.AddWithValue("@needsNaming", group.NeedsNaming ? 1 : 0);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValues.FromDateTimeOffset(group.CreatedAtUtc));
        command.Parameters.AddWithValue("@updatedAtUtc", SqliteValues.FromDateTimeOffset(group.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(id, nameof(id));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            using (SqliteCommand membershipCommand = SqliteCommandFactory.Create(
                       connection,
                       "DELETE FROM conversation_group_memberships WHERE group_id = @id;",
                       transaction))
            {
                membershipCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
                await membershipCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int affected;
            using (SqliteCommand groupCommand = SqliteCommandFactory.Create(
                       connection,
                       "DELETE FROM conversation_groups WHERE id = @id;",
                       transaction))
            {
                groupCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
                affected = await groupCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            return affected > 0;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection, string suffix)
    {
        string sql = $"""
            SELECT id, name, needs_naming, created_at_utc, updated_at_utc
            FROM conversation_groups
            {suffix};
            """;

        return SqliteCommandFactory.Create(connection, sql);
    }

    private static ConversationGroup ReadGroup(DbDataReader reader)
    {
        return ConversationGroup.Restore(
            SqliteValues.ToGuid(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2) != 0,
            SqliteValues.ToDateTimeOffset(reader.GetString(3)),
            SqliteValues.ToDateTimeOffset(reader.GetString(4)));
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
