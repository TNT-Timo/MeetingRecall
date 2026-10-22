using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteConversationQuestionHistoryRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory)
    : IConversationQuestionHistoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<ConversationQuestionHistory?> GetByConversationIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(conversationId);
        databaseInitializer.Initialize();

        const string sql = """
            SELECT transcript_fingerprint, exchanges_json
            FROM conversation_question_histories
            WHERE conversation_id = @conversationId;
            """;
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(conversationId));
        using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        ConversationQuestionExchange[] exchanges =
            JsonSerializer.Deserialize<ConversationQuestionExchange[]>(
                reader.GetString(1),
                JsonOptions)
            ?? throw new InvalidDataException(
                "Der gespeicherte Gesprächschat ist ungültig.");
        return new ConversationQuestionHistory(
            conversationId,
            reader.GetString(0),
            exchanges);
    }

    public async Task SaveAsync(
        ConversationQuestionHistory history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ValidateIdentifier(history.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            history.TranscriptFingerprint);
        if (history.Exchanges.Count == 0)
        {
            await DeleteAsync(history.ConversationId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (history.Exchanges.Any(exchange =>
                exchange.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(exchange.Question)))
        {
            throw new ArgumentException(
                "Ein Gesprächschat enthält eine ungültige Nachricht.",
                nameof(history));
        }

        databaseInitializer.Initialize();
        const string sql = """
            INSERT INTO conversation_question_histories (
                conversation_id,
                transcript_fingerprint,
                exchanges_json,
                updated_at_utc)
            VALUES (
                @conversationId,
                @transcriptFingerprint,
                @exchangesJson,
                @updatedAtUtc)
            ON CONFLICT(conversation_id) DO UPDATE SET
                transcript_fingerprint = excluded.transcript_fingerprint,
                exchanges_json = excluded.exchanges_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(history.ConversationId));
        command.Parameters.AddWithValue(
            "@transcriptFingerprint",
            history.TranscriptFingerprint);
        command.Parameters.AddWithValue(
            "@exchangesJson",
            JsonSerializer.Serialize(history.Exchanges, JsonOptions));
        command.Parameters.AddWithValue(
            "@updatedAtUtc",
            SqliteValues.FromDateTimeOffset(DateTimeOffset.UtcNow));
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
            "DELETE FROM conversation_question_histories WHERE conversation_id = @conversationId;");
        command.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(conversationId));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    private static void ValidateIdentifier(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Ein Gesprächschat benötigt ein Gespräch.",
                nameof(conversationId));
        }
    }
}
