using Microsoft.Data.Sqlite;
using MeetingRecall.Infrastructure.Persistence;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Infrastructure.Transcription;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class DatabaseInitializerTests
{
    [TestMethod]
    public void Initialize_CreatesDatabaseAndCanRunRepeatedly()
    {
        using var context = new PersistenceTestContext();
        IDatabaseInitializer initializer = context.GetRequiredService<IDatabaseInitializer>();
        AppDataPaths paths = context.GetRequiredService<AppDataPaths>();

        initializer.Initialize();
        initializer.Initialize();

        Assert.IsTrue(File.Exists(paths.DatabaseFilePath));
        Assert.IsTrue(new FileInfo(paths.DatabaseFilePath).Length > 0);

        using SqliteConnection connection = context
            .GetRequiredService<ISqliteConnectionFactory>()
            .OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'conversation_question_histories';
            """;
        Assert.AreEqual(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [TestMethod]
    public void Initialize_VersionEightPermanentlyRemovesLegacyDeletedConversations()
    {
        using var context = new PersistenceTestContext();
        AppDataPaths paths = context.GetRequiredService<AppDataPaths>();
        var connections = new SqliteConnectionFactory(paths);
        Guid conversationId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        string timestamp = DateTimeOffset.UtcNow.ToString("O");

        using (SqliteConnection connection = connections.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE conversation_question_histories;
                DELETE FROM schema_migrations WHERE version >= 8;

                CREATE TABLE conversation_trash (
                    conversation_id TEXT NOT NULL PRIMARY KEY,
                    deleted_at_utc TEXT NOT NULL,
                    FOREIGN KEY (conversation_id)
                        REFERENCES conversations(id) ON DELETE CASCADE);

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
                    @conversationId,
                    'Legacy deletion',
                    @timestamp,
                    1,
                    1,
                    0,
                    0,
                    NULL,
                    @timestamp,
                    @timestamp);

                INSERT INTO speakers (
                    id,
                    display_name,
                    color_hex,
                    identification_kind,
                    match_confidence,
                    created_at_utc,
                    updated_at_utc)
                VALUES (
                    @speakerId,
                    'Person 0001',
                    '#6F8FAF',
                    0,
                    NULL,
                    @timestamp,
                    @timestamp);

                INSERT INTO conversation_speakers(conversation_id, speaker_id)
                VALUES (@conversationId, @speakerId);

                INSERT INTO conversation_trash(conversation_id, deleted_at_utc)
                VALUES (@conversationId, @timestamp);
                """;
            command.Parameters.AddWithValue(
                "@conversationId",
                SqliteValues.FromGuid(conversationId));
            command.Parameters.AddWithValue(
                "@speakerId",
                SqliteValues.FromGuid(speakerId));
            command.Parameters.AddWithValue("@timestamp", timestamp);
            command.ExecuteNonQuery();
        }

        string meetingDirectory = paths.GetMeetingDirectory(conversationId);
        Directory.CreateDirectory(meetingDirectory);
        File.WriteAllText(
            Path.Combine(meetingDirectory, "legacy-file.tmp"),
            "legacy");

        var initializer = new SqliteDatabaseInitializer(paths, connections);
        initializer.Initialize();

        using SqliteConnection verification = connections.OpenConnection();
        using SqliteCommand verificationCommand = verification.CreateCommand();
        verificationCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM conversations WHERE id = @conversationId),
                (SELECT COUNT(*) FROM speakers WHERE id = @speakerId),
                (SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'table' AND name = 'conversation_trash');
            """;
        verificationCommand.Parameters.AddWithValue(
            "@conversationId",
            SqliteValues.FromGuid(conversationId));
        verificationCommand.Parameters.AddWithValue(
            "@speakerId",
            SqliteValues.FromGuid(speakerId));
        using SqliteDataReader reader = verificationCommand.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(0L, reader.GetInt64(0));
        Assert.AreEqual(0L, reader.GetInt64(1));
        Assert.AreEqual(0L, reader.GetInt64(2));
        Assert.IsFalse(Directory.Exists(meetingDirectory));
    }

    [TestMethod]
    public void Initialize_VersionSevenMarksExistingVoiceprintsAsUnversioned()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(rootDirectory);
            paths.EnsureCreated();
            var connections = new SqliteConnectionFactory(paths);
            using (SqliteConnection connection = connections.OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER NOT NULL PRIMARY KEY,
                        applied_at_utc TEXT NOT NULL);

                    CREATE TABLE conversations (
                        id TEXT NOT NULL PRIMARY KEY);

                    CREATE TABLE speakers (
                        id TEXT NOT NULL PRIMARY KEY,
                        identification_kind INTEGER NOT NULL);

                    CREATE TABLE conversation_speakers (
                        conversation_id TEXT NOT NULL,
                        speaker_id TEXT NOT NULL);

                    CREATE TABLE transcript_segments (
                        conversation_id TEXT NOT NULL,
                        speaker_id TEXT NOT NULL);

                    CREATE TABLE conversation_trash (
                        conversation_id TEXT NOT NULL PRIMARY KEY,
                        deleted_at_utc TEXT NOT NULL);

                    CREATE TABLE speaker_voiceprints (
                        speaker_id TEXT NOT NULL PRIMARY KEY,
                        sample_count INTEGER NOT NULL,
                        feature_vector_json TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL);

                    INSERT INTO schema_migrations(version, applied_at_utc)
                    VALUES (6, '2026-01-01T00:00:00.0000000+00:00');

                    INSERT INTO speaker_voiceprints(
                        speaker_id,
                        sample_count,
                        feature_vector_json,
                        updated_at_utc)
                    VALUES (
                        '00000000-0000-0000-0000-000000000001',
                        2,
                        '[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9,1.0]',
                        '2026-01-01T00:00:00.0000000+00:00');
                    """;
                command.ExecuteNonQuery();
            }

            var initializer = new SqliteDatabaseInitializer(
                paths,
                connections);
            initializer.Initialize();

            using SqliteConnection verification = connections.OpenConnection();
            using SqliteCommand versionCommand = verification.CreateCommand();
            versionCommand.CommandText =
                "SELECT MAX(version) FROM schema_migrations;";
            Assert.AreEqual(
                SqliteSchema.CurrentVersion,
                Convert.ToInt32(versionCommand.ExecuteScalar()));

            using SqliteCommand modelCommand = verification.CreateCommand();
            modelCommand.CommandText =
                "SELECT embedding_model_id FROM speaker_voiceprints;";
            Assert.AreEqual(
                SpeakerEmbeddingModels.Unversioned,
                Convert.ToString(modelCommand.ExecuteScalar()));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }
}
