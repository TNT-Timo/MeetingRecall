using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteMeetingSummaryRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : IMeetingSummaryRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<MeetingSummary?> GetByConversationIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(conversationId);
        databaseInitializer.Initialize();

        const string sql = """
            SELECT
                suggested_title,
                overview,
                key_points_json,
                decisions_json,
                action_items_json,
                source_transcript_updated_at_utc,
                generated_at_utc
            FROM meeting_summaries
            WHERE conversation_id = @conversationId;
            """;

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(conversationId));
        using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string[] keyPoints = Deserialize<string[]>(reader.GetString(2));
        string[] decisions = Deserialize<string[]>(reader.GetString(3));
        MeetingActionItem[] actionItems =
            Deserialize<MeetingActionItem[]>(reader.GetString(4));

        return MeetingSummary.Restore(
            conversationId,
            reader.GetString(0),
            reader.GetString(1),
            keyPoints,
            decisions,
            actionItems,
            SqliteValues.ToDateTimeOffset(reader.GetString(5)),
            SqliteValues.ToDateTimeOffset(reader.GetString(6)));
    }

    public async Task UpsertAsync(
        MeetingSummary summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        databaseInitializer.Initialize();

        const string sql = """
            INSERT INTO meeting_summaries (
                conversation_id,
                suggested_title,
                overview,
                key_points_json,
                decisions_json,
                action_items_json,
                source_transcript_updated_at_utc,
                generated_at_utc)
            VALUES (
                @conversationId,
                @suggestedTitle,
                @overview,
                @keyPointsJson,
                @decisionsJson,
                @actionItemsJson,
                @sourceTranscriptUpdatedAtUtc,
                @generatedAtUtc)
            ON CONFLICT(conversation_id) DO UPDATE SET
                suggested_title = excluded.suggested_title,
                overview = excluded.overview,
                key_points_json = excluded.key_points_json,
                decisions_json = excluded.decisions_json,
                action_items_json = excluded.action_items_json,
                source_transcript_updated_at_utc =
                    excluded.source_transcript_updated_at_utc,
                generated_at_utc = excluded.generated_at_utc;
            """;

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(summary.ConversationId));
        command.Parameters.AddWithValue("@suggestedTitle", summary.SuggestedTitle);
        command.Parameters.AddWithValue("@overview", summary.Overview);
        command.Parameters.AddWithValue(
            "@keyPointsJson",
            JsonSerializer.Serialize(summary.KeyPoints, JsonOptions));
        command.Parameters.AddWithValue(
            "@decisionsJson",
            JsonSerializer.Serialize(summary.Decisions, JsonOptions));
        command.Parameters.AddWithValue(
            "@actionItemsJson",
            JsonSerializer.Serialize(summary.ActionItems, JsonOptions));
        command.Parameters.AddWithValue(
            "@sourceTranscriptUpdatedAtUtc",
            SqliteValues.FromDateTimeOffset(summary.SourceTranscriptUpdatedAtUtc));
        command.Parameters.AddWithValue(
            "@generatedAtUtc",
            SqliteValues.FromDateTimeOffset(summary.GeneratedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(conversationId);
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(
            connection,
            "DELETE FROM meeting_summaries WHERE conversation_id = @conversationId;");
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(conversationId));

        return await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException(
                "Gespeicherte Zusammenfassungsdaten sind ungültig.");
    }

    private static void ValidateIdentifier(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Eine Zusammenfassung benötigt ein Gespräch.",
                nameof(id));
        }
    }
}
