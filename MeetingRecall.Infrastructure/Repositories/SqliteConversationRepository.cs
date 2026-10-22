using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.ValueObjects;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteConversationRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : IConversationRepository
{
    public async Task<Conversation?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(id, nameof(id));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        return await LoadConversationAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        var ids = new List<Guid>();

        using (SqliteCommand command = SqliteCommandFactory.Create(
                   connection,
                   """
                   SELECT id
                   FROM conversations
                   ORDER BY recorded_at_utc_ticks DESC, id;
                   """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(SqliteValues.ToGuid(reader.GetString(0)));
            }
        }

        var conversations = new List<Conversation>(ids.Count);
        foreach (Guid id in ids)
        {
            Conversation? conversation = await LoadConversationAsync(connection, id, cancellationToken).ConfigureAwait(false);
            if (conversation is not null)
            {
                conversations.Add(conversation);
            }
        }

        return conversations;
    }

    public async Task UpsertAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            await UpsertConversationRowAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
            await DeleteConversationChildrenAsync(connection, transaction, conversation.Id, cancellationToken).ConfigureAwait(false);
            await InsertGroupMembershipsAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
            await InsertSpeakerMembershipsAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
            await InsertTranscriptAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(id, nameof(id));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        var unknownSpeakerIds = new List<Guid>();
        using (SqliteCommand speakerCommand = SqliteCommandFactory.Create(
                   connection,
                   """
                   SELECT DISTINCT speaker.id
                   FROM speakers AS speaker
                   LEFT JOIN conversation_speakers AS membership
                       ON membership.speaker_id = speaker.id
                   LEFT JOIN transcript_segments AS segment
                       ON segment.speaker_id = speaker.id
                   WHERE speaker.identification_kind = 0
                     AND (
                         membership.conversation_id = @conversationId
                         OR segment.conversation_id = @conversationId);
                   """,
                   transaction))
        {
            speakerCommand.Parameters.AddWithValue(
                "@conversationId",
                SqliteValues.FromGuid(id));
            using var reader = await speakerCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unknownSpeakerIds.Add(SqliteValues.ToGuid(reader.GetString(0)));
            }
        }

        int affected;
        using (SqliteCommand deleteConversation = SqliteCommandFactory.Create(
                   connection,
                   "DELETE FROM conversations WHERE id = @id;",
                   transaction))
        {
            deleteConversation.Parameters.AddWithValue(
                "@id",
                SqliteValues.FromGuid(id));
            affected = await deleteConversation
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (Guid speakerId in unknownSpeakerIds)
        {
            using SqliteCommand deleteSpeaker = SqliteCommandFactory.Create(
                connection,
                """
                DELETE FROM speakers
                WHERE id = @speakerId
                  AND identification_kind = 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM conversation_speakers
                      WHERE speaker_id = @speakerId)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM transcript_segments
                      WHERE speaker_id = @speakerId);
                """,
                transaction);
            deleteSpeaker.Parameters.AddWithValue(
                "@speakerId",
                SqliteValues.FromGuid(speakerId));
            await deleteSpeaker
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
        return affected > 0;
    }

    private static async Task UpsertConversationRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO conversations (
                id,
                title,
                recorded_at,
                recorded_at_utc_ticks,
                source,
                processing_state,
                duration_ticks,
                detected_language,
                created_at_utc,
                updated_at_utc)
            VALUES (
                @id,
                @title,
                @recordedAt,
                @recordedAtUtcTicks,
                @source,
                @processingState,
                @durationTicks,
                @detectedLanguage,
                @createdAtUtc,
                @updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                recorded_at = excluded.recorded_at,
                recorded_at_utc_ticks = excluded.recorded_at_utc_ticks,
                source = excluded.source,
                processing_state = excluded.processing_state,
                duration_ticks = excluded.duration_ticks,
                detected_language = excluded.detected_language,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql, transaction);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(conversation.Id));
        command.Parameters.AddWithValue("@title", conversation.Title);
        command.Parameters.AddWithValue("@recordedAt", SqliteValues.FromDateTimeOffset(conversation.RecordedAt));
        command.Parameters.AddWithValue("@recordedAtUtcTicks", conversation.RecordedAt.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("@source", (int)conversation.Source);
        command.Parameters.AddWithValue("@processingState", (int)conversation.ProcessingState);
        command.Parameters.AddWithValue("@durationTicks", conversation.Duration.Ticks);
        command.Parameters.AddWithValue("@detectedLanguage", (object?)conversation.DetectedLanguage ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValues.FromDateTimeOffset(conversation.CreatedAtUtc));
        command.Parameters.AddWithValue("@updatedAtUtc", SqliteValues.FromDateTimeOffset(conversation.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteConversationChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM transcript_segments WHERE conversation_id = @conversationId;
            DELETE FROM conversation_group_memberships WHERE conversation_id = @conversationId;
            DELETE FROM conversation_speakers WHERE conversation_id = @conversationId;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql, transaction);
        command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversationId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertGroupMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        foreach (Guid groupId in conversation.GroupIds)
        {
            using SqliteCommand command = SqliteCommandFactory.Create(
                connection,
                "INSERT INTO conversation_group_memberships(conversation_id, group_id) VALUES (@conversationId, @groupId);",
                transaction);
            command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversation.Id));
            command.Parameters.AddWithValue("@groupId", SqliteValues.FromGuid(groupId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertSpeakerMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        foreach (Guid speakerId in conversation.SpeakerIds)
        {
            using SqliteCommand command = SqliteCommandFactory.Create(
                connection,
                "INSERT INTO conversation_speakers(conversation_id, speaker_id) VALUES (@conversationId, @speakerId);",
                transaction);
            command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversation.Id));
            command.Parameters.AddWithValue("@speakerId", SqliteValues.FromGuid(speakerId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertTranscriptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        foreach (TranscriptSegment segment in conversation.TranscriptSegments)
        {
            const string segmentSql = """
                INSERT INTO transcript_segments (
                    id,
                    conversation_id,
                    speaker_id,
                    start_ticks,
                    end_ticks,
                    original_text,
                    current_text,
                    last_edited_at_utc)
                VALUES (
                    @id,
                    @conversationId,
                    @speakerId,
                    @startTicks,
                    @endTicks,
                    @originalText,
                    @currentText,
                    @lastEditedAtUtc);
                """;

            using (SqliteCommand command = SqliteCommandFactory.Create(connection, segmentSql, transaction))
            {
                command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(segment.Id));
                command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversation.Id));
                command.Parameters.AddWithValue("@speakerId", SqliteValues.FromGuid(segment.SpeakerId));
                command.Parameters.AddWithValue("@startTicks", segment.StartTime.Ticks);
                command.Parameters.AddWithValue("@endTicks", segment.EndTime.Ticks);
                command.Parameters.AddWithValue("@originalText", segment.OriginalText);
                command.Parameters.AddWithValue("@currentText", segment.CurrentText);
                command.Parameters.AddWithValue(
                    "@lastEditedAtUtc",
                    SqliteValues.FromNullableDateTimeOffset(segment.LastEditedAtUtc));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            for (int index = 0; index < segment.Revisions.Count; index++)
            {
                TranscriptRevision revision = segment.Revisions[index];
                const string revisionSql = """
                    INSERT INTO transcript_revisions (
                        id,
                        segment_id,
                        revision_order,
                        previous_text,
                        revised_text,
                        edited_at_utc)
                    VALUES (
                        @id,
                        @segmentId,
                        @revisionOrder,
                        @previousText,
                        @revisedText,
                        @editedAtUtc);
                    """;

                using SqliteCommand command = SqliteCommandFactory.Create(connection, revisionSql, transaction);
                command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(revision.Id));
                command.Parameters.AddWithValue("@segmentId", SqliteValues.FromGuid(segment.Id));
                command.Parameters.AddWithValue("@revisionOrder", index);
                command.Parameters.AddWithValue("@previousText", revision.PreviousText);
                command.Parameters.AddWithValue("@revisedText", revision.RevisedText);
                command.Parameters.AddWithValue("@editedAtUtc", SqliteValues.FromDateTimeOffset(revision.EditedAtUtc));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Conversation?> LoadConversationAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        ConversationRow? row = await LoadConversationRowAsync(connection, id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        IReadOnlyList<Guid> groupIds = await LoadIdsAsync(
            connection,
            "SELECT group_id FROM conversation_group_memberships WHERE conversation_id = @conversationId ORDER BY group_id;",
            id,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Guid> speakerIds = await LoadIdsAsync(
            connection,
            "SELECT speaker_id FROM conversation_speakers WHERE conversation_id = @conversationId ORDER BY speaker_id;",
            id,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TranscriptSegment> segments = await LoadTranscriptSegmentsAsync(
            connection,
            id,
            cancellationToken).ConfigureAwait(false);

        return Conversation.Restore(
            row.Id,
            row.Title,
            row.RecordedAt,
            row.Source,
            row.ProcessingState,
            row.Duration,
            row.DetectedLanguage,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            groupIds,
            speakerIds,
            segments);
    }

    private static async Task<ConversationRow?> LoadConversationRowAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                title,
                recorded_at,
                source,
                processing_state,
                duration_ticks,
                detected_language,
                created_at_utc,
                updated_at_utc
            FROM conversations
            WHERE id = @id;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(id));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ConversationRow(
            SqliteValues.ToGuid(reader.GetString(0)),
            reader.GetString(1),
            SqliteValues.ToDateTimeOffset(reader.GetString(2)),
            (ConversationSource)reader.GetInt32(3),
            (ConversationProcessingState)reader.GetInt32(4),
            TimeSpan.FromTicks(reader.GetInt64(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            SqliteValues.ToDateTimeOffset(reader.GetString(7)),
            SqliteValues.ToDateTimeOffset(reader.GetString(8)));
    }

    private static async Task<IReadOnlyList<Guid>> LoadIdsAsync(
        SqliteConnection connection,
        string sql,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversationId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(SqliteValues.ToGuid(reader.GetString(0)));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptSegmentsAsync(
        SqliteConnection connection,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                speaker_id,
                start_ticks,
                end_ticks,
                original_text,
                current_text,
                last_edited_at_utc
            FROM transcript_segments
            WHERE conversation_id = @conversationId
            ORDER BY start_ticks, id;
            """;

        var rows = new List<TranscriptSegmentRow>();
        using (SqliteCommand command = SqliteCommandFactory.Create(connection, sql))
        {
            command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversationId));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new TranscriptSegmentRow(
                    SqliteValues.ToGuid(reader.GetString(0)),
                    SqliteValues.ToGuid(reader.GetString(1)),
                    TimeSpan.FromTicks(reader.GetInt64(2)),
                    TimeSpan.FromTicks(reader.GetInt64(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : SqliteValues.ToDateTimeOffset(reader.GetString(6))));
            }
        }

        var segments = new List<TranscriptSegment>(rows.Count);
        foreach (TranscriptSegmentRow row in rows)
        {
            IReadOnlyList<TranscriptRevision> revisions = await LoadRevisionsAsync(
                connection,
                row.Id,
                cancellationToken).ConfigureAwait(false);

            segments.Add(TranscriptSegment.Restore(
                row.Id,
                row.SpeakerId,
                row.StartTime,
                row.EndTime,
                row.OriginalText,
                row.CurrentText,
                row.LastEditedAtUtc,
                revisions));
        }

        return segments;
    }

    private static async Task<IReadOnlyList<TranscriptRevision>> LoadRevisionsAsync(
        SqliteConnection connection,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, previous_text, revised_text, edited_at_utc
            FROM transcript_revisions
            WHERE segment_id = @segmentId
            ORDER BY revision_order;
            """;

        var revisions = new List<TranscriptRevision>();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@segmentId", SqliteValues.FromGuid(segmentId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new TranscriptRevision(
                SqliteValues.ToGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                SqliteValues.ToDateTimeOffset(reader.GetString(3))));
        }

        return revisions;
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }

    private sealed record ConversationRow(
        Guid Id,
        string Title,
        DateTimeOffset RecordedAt,
        ConversationSource Source,
        ConversationProcessingState ProcessingState,
        TimeSpan Duration,
        string? DetectedLanguage,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed record TranscriptSegmentRow(
        Guid Id,
        Guid SpeakerId,
        TimeSpan StartTime,
        TimeSpan EndTime,
        string OriginalText,
        string CurrentText,
        DateTimeOffset? LastEditedAtUtc);
}
