using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteSpeakerRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : ISpeakerRepository
{
    public async Task<Speaker?> GetByIdAsync(
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
            ? ReadSpeaker(reader)
            : null;
    }

    public async Task<IReadOnlyList<Speaker>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = CreateSelectCommand(connection, "ORDER BY display_name COLLATE NOCASE, id");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var speakers = new List<Speaker>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            speakers.Add(ReadSpeaker(reader));
        }

        return speakers;
    }

    public async Task UpsertAsync(
        Speaker speaker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        databaseInitializer.Initialize();

        const string sql = """
            INSERT INTO speakers (
                id,
                display_name,
                color_hex,
                identification_kind,
                match_confidence,
                created_at_utc,
                updated_at_utc)
            VALUES (
                @id,
                @displayName,
                @colorHex,
                @identificationKind,
                @matchConfidence,
                @createdAtUtc,
                @updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                color_hex = excluded.color_hex,
                identification_kind = excluded.identification_kind,
                match_confidence = excluded.match_confidence,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(speaker.Id));
        command.Parameters.AddWithValue("@displayName", speaker.DisplayName);
        command.Parameters.AddWithValue("@colorHex", speaker.ColorHex);
        command.Parameters.AddWithValue("@identificationKind", (int)speaker.IdentificationKind);
        command.Parameters.AddWithValue("@matchConfidence", (object?)speaker.MatchConfidence ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValues.FromDateTimeOffset(speaker.CreatedAtUtc));
        command.Parameters.AddWithValue("@updatedAtUtc", SqliteValues.FromDateTimeOffset(speaker.UpdatedAtUtc));

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

        using (SqliteCommand existsCommand = SqliteCommandFactory.Create(
                   connection,
                   "SELECT COUNT(*) FROM speakers WHERE id = @id;",
                   transaction))
        {
            existsCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
            long count = (long)(await existsCommand
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0L);
            if (count == 0)
            {
                return false;
            }
        }

        long usageCount;
        using (SqliteCommand usageCommand = SqliteCommandFactory.Create(
                   connection,
                   "SELECT COUNT(*) FROM transcript_segments WHERE speaker_id = @id;",
                   transaction))
        {
            usageCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
            usageCount = (long)(await usageCommand
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0L);
        }

        if (usageCount > 0)
        {
            int nextSequence = await GetNextUnknownSequenceAsync(
                connection,
                transaction,
                cancellationToken)
                .ConfigureAwait(false);
            Guid replacementId = Guid.NewGuid();
            Speaker replacement = Speaker.CreateUnknown(replacementId, nextSequence);

            const string insertReplacementSql = """
                INSERT INTO speakers (
                    id,
                    display_name,
                    color_hex,
                    identification_kind,
                    match_confidence,
                    created_at_utc,
                    updated_at_utc)
                VALUES (
                    @replacementId,
                    @displayName,
                    @colorHex,
                    @identificationKind,
                    NULL,
                    @createdAtUtc,
                    @updatedAtUtc);
                """;
            using (SqliteCommand insertReplacement = SqliteCommandFactory.Create(
                       connection,
                       insertReplacementSql,
                       transaction))
            {
                insertReplacement.Parameters.AddWithValue(
                    "@replacementId",
                    SqliteValues.FromGuid(replacement.Id));
                insertReplacement.Parameters.AddWithValue("@displayName", replacement.DisplayName);
                insertReplacement.Parameters.AddWithValue("@colorHex", replacement.ColorHex);
                insertReplacement.Parameters.AddWithValue(
                    "@identificationKind",
                    (int)replacement.IdentificationKind);
                insertReplacement.Parameters.AddWithValue(
                    "@createdAtUtc",
                    SqliteValues.FromDateTimeOffset(replacement.CreatedAtUtc));
                insertReplacement.Parameters.AddWithValue(
                    "@updatedAtUtc",
                    SqliteValues.FromDateTimeOffset(replacement.UpdatedAtUtc));
                await insertReplacement
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            const string reassignSql = """
                UPDATE transcript_segments
                SET speaker_id = @replacementId
                WHERE speaker_id = @id;

                INSERT OR IGNORE INTO conversation_speakers (conversation_id, speaker_id)
                SELECT conversation_id, @replacementId
                FROM conversation_speakers
                WHERE speaker_id = @id;

                DELETE FROM conversation_speakers
                WHERE speaker_id = @id;
                """;
            using SqliteCommand reassignCommand = SqliteCommandFactory.Create(
                connection,
                reassignSql,
                transaction);
            reassignCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
            reassignCommand.Parameters.AddWithValue(
                "@replacementId",
                SqliteValues.FromGuid(replacementId));
            await reassignCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (SqliteCommand deleteCommand = SqliteCommandFactory.Create(
                   connection,
                   "DELETE FROM speakers WHERE id = @id;",
                   transaction))
        {
            deleteCommand.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
            await deleteCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    public async Task<int> DeleteAllAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        int deletedCount;
        using (SqliteCommand countCommand = SqliteCommandFactory.Create(
                   connection,
                   "SELECT COUNT(*) FROM speakers;",
                   transaction))
        {
            deletedCount = Convert.ToInt32(
                await countCommand
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (deletedCount == 0)
        {
            transaction.Commit();
            return 0;
        }

        var referencedIds = new List<Guid>();
        using (SqliteCommand referencedCommand = SqliteCommandFactory.Create(
                   connection,
                   """
                   SELECT DISTINCT speaker_id
                   FROM transcript_segments
                   ORDER BY speaker_id;
                   """,
                   transaction))
        using (var reader = await referencedCommand
                   .ExecuteReaderAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            while (await reader
                       .ReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                referencedIds.Add(SqliteValues.ToGuid(reader.GetString(0)));
            }
        }

        int replacementSequence = 1;
        foreach (Guid sourceId in referencedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Speaker replacement = Speaker.CreateUnknown(
                Guid.NewGuid(),
                replacementSequence++);

            using (SqliteCommand insertReplacement = SqliteCommandFactory.Create(
                       connection,
                       """
                       INSERT INTO speakers (
                           id,
                           display_name,
                           color_hex,
                           identification_kind,
                           match_confidence,
                           created_at_utc,
                           updated_at_utc)
                       VALUES (
                           @replacementId,
                           @displayName,
                           @colorHex,
                           @identificationKind,
                           NULL,
                           @createdAtUtc,
                           @updatedAtUtc);
                       """,
                       transaction))
            {
                insertReplacement.Parameters.AddWithValue(
                    "@replacementId",
                    SqliteValues.FromGuid(replacement.Id));
                insertReplacement.Parameters.AddWithValue(
                    "@displayName",
                    replacement.DisplayName);
                insertReplacement.Parameters.AddWithValue(
                    "@colorHex",
                    replacement.ColorHex);
                insertReplacement.Parameters.AddWithValue(
                    "@identificationKind",
                    (int)replacement.IdentificationKind);
                insertReplacement.Parameters.AddWithValue(
                    "@createdAtUtc",
                    SqliteValues.FromDateTimeOffset(replacement.CreatedAtUtc));
                insertReplacement.Parameters.AddWithValue(
                    "@updatedAtUtc",
                    SqliteValues.FromDateTimeOffset(replacement.UpdatedAtUtc));
                await insertReplacement
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            using SqliteCommand reassignCommand = SqliteCommandFactory.Create(
                connection,
                """
                UPDATE transcript_segments
                SET speaker_id = @replacementId
                WHERE speaker_id = @sourceId;

                INSERT OR IGNORE INTO conversation_speakers (
                    conversation_id,
                    speaker_id)
                SELECT conversation_id, @replacementId
                FROM conversation_speakers
                WHERE speaker_id = @sourceId;

                DELETE FROM conversation_speakers
                WHERE speaker_id = @sourceId;
                """,
                transaction);
            reassignCommand.Parameters.AddWithValue(
                "@sourceId",
                SqliteValues.FromGuid(sourceId));
            reassignCommand.Parameters.AddWithValue(
                "@replacementId",
                SqliteValues.FromGuid(replacement.Id));
            await reassignCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (SqliteCommand cleanupCommand = SqliteCommandFactory.Create(
                   connection,
                   """
                   DELETE FROM speaker_voiceprints;

                   DELETE FROM conversation_speakers
                   WHERE speaker_id NOT IN (
                       SELECT DISTINCT speaker_id
                       FROM transcript_segments);

                   DELETE FROM speakers
                   WHERE id NOT IN (
                       SELECT DISTINCT speaker_id
                       FROM transcript_segments);
                   """,
                   transaction))
        {
            await cleanupCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
        return deletedCount;
    }

    private static async Task<int> GetNextUnknownSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = SqliteCommandFactory.Create(
            connection,
            """
            SELECT display_name
            FROM speakers
            WHERE identification_kind = 0;
            """,
            transaction);
        using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var usedSequences = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string displayName = reader.GetString(0);
            if (displayName.StartsWith("Person ", StringComparison.Ordinal)
                && int.TryParse(
                    displayName.AsSpan("Person ".Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int sequence))
            {
                usedSequences.Add(sequence);
            }
        }

        int candidate = 1;
        while (usedSequences.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection, string suffix)
    {
        string sql = $"""
            SELECT
                id,
                display_name,
                color_hex,
                identification_kind,
                match_confidence,
                created_at_utc,
                updated_at_utc
            FROM speakers
            {suffix};
            """;

        return SqliteCommandFactory.Create(connection, sql);
    }

    private static Speaker ReadSpeaker(DbDataReader reader)
    {
        return Speaker.Restore(
            SqliteValues.ToGuid(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (SpeakerIdentificationKind)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4),
            SqliteValues.ToDateTimeOffset(reader.GetString(5)),
            SqliteValues.ToDateTimeOffset(reader.GetString(6)));
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
