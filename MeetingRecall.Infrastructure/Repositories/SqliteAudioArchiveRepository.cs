using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Infrastructure.Repositories;

internal sealed class SqliteAudioArchiveRepository(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : IAudioArchiveRepository
{
    private const int Sha256Length = 64;

    public Task ImportAsync(
        Conversation conversation,
        AudioTrack primaryTrack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primaryTrack);
        return ImportAsync(conversation, new[] { primaryTrack }, cancellationToken);
    }

    public async Task ImportAsync(
        Conversation conversation,
        IReadOnlyCollection<AudioTrack> tracks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(tracks);

        AudioTrack[] trackArray = tracks.ToArray();
        if (trackArray.Length == 0)
        {
            throw new ArgumentException("At least one audio track is required.", nameof(tracks));
        }

        if (trackArray.Count(track => track.Kind == AudioTrackKind.Primary) != 1)
        {
            throw new ArgumentException("Exactly one primary audio track is required.", nameof(tracks));
        }

        if (trackArray.Any(track => track.ConversationId != conversation.Id))
        {
            throw new ArgumentException("Every audio track must belong to the imported conversation.", nameof(tracks));
        }

        if (trackArray.Select(track => track.Id).Distinct().Count() != trackArray.Length)
        {
            throw new ArgumentException("Audio-track identifiers must be unique.", nameof(tracks));
        }

        if (trackArray.GroupBy(track => track.Kind).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Only one audio track of each kind may be imported.", nameof(tracks));
        }

        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            await InsertConversationAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
            foreach (AudioTrack track in trackArray)
            {
                await InsertTrackAsync(
                        connection,
                        transaction,
                        track,
                        isPrimary: track.Kind == AudioTrackKind.Primary,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<AudioTrack?> GetPrimaryAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(conversationId, nameof(conversationId));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        const string sql = """
            SELECT
                id,
                conversation_id,
                kind,
                relative_path,
                original_file_name,
                media_type,
                file_size_bytes,
                duration_ticks,
                source_sha256,
                created_at_utc
            FROM audio_tracks
            WHERE conversation_id = @conversationId AND is_primary = 1
            LIMIT 1;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversationId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTrack(reader)
            : null;
    }

    public async Task<AudioTrack?> FindPrimaryBySourceHashAsync(
        string sourceSha256,
        CancellationToken cancellationToken = default)
    {
        string normalizedHash = NormalizeSha256(sourceSha256);
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        const string sql = """
            SELECT
                id,
                conversation_id,
                kind,
                relative_path,
                original_file_name,
                media_type,
                file_size_bytes,
                duration_ticks,
                source_sha256,
                created_at_utc
            FROM audio_tracks
            WHERE source_sha256 = @sourceSha256 AND is_primary = 1
            LIMIT 1;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@sourceSha256", normalizedHash);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTrack(reader)
            : null;
    }

    public async Task<IReadOnlyList<AudioTrack>> ListPrimaryWithoutSourceHashAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        const string sql = """
            SELECT
                track.id,
                track.conversation_id,
                track.kind,
                track.relative_path,
                track.original_file_name,
                track.media_type,
                track.file_size_bytes,
                track.duration_ticks,
                track.source_sha256,
                track.created_at_utc
            FROM audio_tracks AS track
            INNER JOIN conversations AS conversation
                ON conversation.id = track.conversation_id
            WHERE track.is_primary = 1
                AND track.source_sha256 IS NULL
                AND conversation.source = @importedAudioSource
            ORDER BY track.created_at_utc, track.id;
            """;

        var tracks = new List<AudioTrack>();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@importedAudioSource", (int)ConversationSource.ImportedAudio);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    public async Task<bool> TrySetSourceHashAsync(
        Guid trackId,
        string sourceSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(trackId, nameof(trackId));
        string normalizedHash = NormalizeSha256(sourceSha256);
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        const string sql = """
            UPDATE OR IGNORE audio_tracks
            SET source_sha256 = @sourceSha256
            WHERE id = @id AND source_sha256 IS NULL;
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(trackId));
        command.Parameters.AddWithValue("@sourceSha256", normalizedHash);
        int affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows == 1;
    }

    public async Task<IReadOnlyList<AudioTrack>> ListAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(conversationId, nameof(conversationId));
        databaseInitializer.Initialize();

        using SqliteConnection connection = connectionFactory.OpenConnection();
        const string sql = """
            SELECT
                id,
                conversation_id,
                kind,
                relative_path,
                original_file_name,
                media_type,
                file_size_bytes,
                duration_ticks,
                source_sha256,
                created_at_utc
            FROM audio_tracks
            WHERE conversation_id = @conversationId
            ORDER BY is_primary DESC, kind, id;
            """;

        var tracks = new List<AudioTrack>();
        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql);
        command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(conversationId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    private static async Task InsertConversationAsync(
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
                @updatedAtUtc);
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

    private static async Task InsertTrackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AudioTrack track,
        bool isPrimary,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO audio_tracks (
                id,
                conversation_id,
                kind,
                relative_path,
                original_file_name,
                media_type,
                file_size_bytes,
                duration_ticks,
                is_primary,
                source_sha256,
                created_at_utc)
            VALUES (
                @id,
                @conversationId,
                @kind,
                @relativePath,
                @originalFileName,
                @mediaType,
                @fileSizeBytes,
                @durationTicks,
                @isPrimary,
                @sourceSha256,
                @createdAtUtc);
            """;

        using SqliteCommand command = SqliteCommandFactory.Create(connection, sql, transaction);
        command.Parameters.AddWithValue("@id", SqliteValues.FromGuid(track.Id));
        command.Parameters.AddWithValue("@conversationId", SqliteValues.FromGuid(track.ConversationId));
        command.Parameters.AddWithValue("@kind", (int)track.Kind);
        command.Parameters.AddWithValue("@relativePath", track.RelativePath);
        command.Parameters.AddWithValue("@originalFileName", track.OriginalFileName);
        command.Parameters.AddWithValue("@mediaType", track.MediaType);
        command.Parameters.AddWithValue("@fileSizeBytes", track.FileSizeBytes);
        command.Parameters.AddWithValue("@durationTicks", track.Duration.Ticks);
        command.Parameters.AddWithValue("@isPrimary", isPrimary ? 1 : 0);
        command.Parameters.AddWithValue("@sourceSha256", (object?)track.SourceSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValues.FromDateTimeOffset(track.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AudioTrack ReadTrack(SqliteDataReader reader)
    {
        return AudioTrack.Restore(
            SqliteValues.ToGuid(reader.GetString(0)),
            SqliteValues.ToGuid(reader.GetString(1)),
            (AudioTrackKind)reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            TimeSpan.FromTicks(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            SqliteValues.ToDateTimeOffset(reader.GetString(9)));
    }

    private static string NormalizeSha256(string sourceSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);

        string normalized = sourceSha256.Trim().ToLowerInvariant();
        if (normalized.Length != Sha256Length || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 fingerprint must contain exactly 64 hexadecimal characters.", nameof(sourceSha256));
        }

        return normalized;
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
