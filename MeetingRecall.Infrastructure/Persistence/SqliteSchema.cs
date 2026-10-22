namespace MeetingRecall.Infrastructure.Persistence;

internal static class SqliteSchema
{
    public const int CurrentVersion = 9;

    public const string CreateMigrationTable = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );
        """;

    public const string Migration1 = """
        CREATE TABLE conversations (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            recorded_at_utc_ticks INTEGER NOT NULL,
            source INTEGER NOT NULL,
            processing_state INTEGER NOT NULL,
            duration_ticks INTEGER NOT NULL,
            detected_language TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_conversations_recorded_at
            ON conversations(recorded_at_utc_ticks DESC);

        CREATE TABLE conversation_groups (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            needs_naming INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_conversation_groups_name
            ON conversation_groups(name COLLATE NOCASE);

        CREATE TABLE speakers (
            id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            identification_kind INTEGER NOT NULL,
            match_confidence REAL NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_speakers_display_name
            ON speakers(display_name COLLATE NOCASE);

        CREATE TABLE conversation_group_memberships (
            conversation_id TEXT NOT NULL,
            group_id TEXT NOT NULL,
            PRIMARY KEY (conversation_id, group_id),
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_conversation_group_memberships_group_id
            ON conversation_group_memberships(group_id);

        CREATE TABLE conversation_speakers (
            conversation_id TEXT NOT NULL,
            speaker_id TEXT NOT NULL,
            PRIMARY KEY (conversation_id, speaker_id),
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_conversation_speakers_speaker_id
            ON conversation_speakers(speaker_id);

        CREATE TABLE transcript_segments (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            speaker_id TEXT NOT NULL,
            start_ticks INTEGER NOT NULL,
            end_ticks INTEGER NOT NULL,
            original_text TEXT NOT NULL,
            current_text TEXT NOT NULL,
            last_edited_at_utc TEXT NULL,
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_transcript_segments_conversation_time
            ON transcript_segments(conversation_id, start_ticks, id);

        CREATE INDEX ix_transcript_segments_speaker_id
            ON transcript_segments(speaker_id);

        CREATE TABLE transcript_revisions (
            id TEXT NOT NULL PRIMARY KEY,
            segment_id TEXT NOT NULL,
            revision_order INTEGER NOT NULL,
            previous_text TEXT NOT NULL,
            revised_text TEXT NOT NULL,
            edited_at_utc TEXT NOT NULL,
            UNIQUE (segment_id, revision_order),
            FOREIGN KEY (segment_id) REFERENCES transcript_segments(id) ON DELETE CASCADE
        );

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (1, @appliedAtUtc);
        """;

    public const string Migration2 = """
        CREATE TABLE audio_tracks (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            kind INTEGER NOT NULL,
            relative_path TEXT NOT NULL,
            original_file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            file_size_bytes INTEGER NOT NULL,
            duration_ticks INTEGER NOT NULL,
            is_primary INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_audio_tracks_conversation
            ON audio_tracks(conversation_id, kind, id);

        CREATE UNIQUE INDEX ux_audio_tracks_primary
            ON audio_tracks(conversation_id)
            WHERE is_primary = 1;

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (2, @appliedAtUtc);
        """;

    public const string Migration3 = """
        ALTER TABLE audio_tracks
            ADD COLUMN source_sha256 TEXT NULL;

        CREATE UNIQUE INDEX ux_audio_tracks_source_sha256
            ON audio_tracks(source_sha256)
            WHERE source_sha256 IS NOT NULL;

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (3, @appliedAtUtc);
        """;

    public const string Migration4 = """
        ALTER TABLE speakers
            ADD COLUMN color_hex TEXT NOT NULL DEFAULT '#6F8FAF';

        CREATE TABLE speaker_voiceprints (
            speaker_id TEXT NOT NULL PRIMARY KEY,
            sample_count INTEGER NOT NULL,
            feature_vector_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY (speaker_id) REFERENCES speakers(id) ON DELETE CASCADE
        );

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (4, @appliedAtUtc);
        """;

    public const string Migration5 = """
        CREATE TABLE meeting_summaries (
            conversation_id TEXT NOT NULL PRIMARY KEY,
            suggested_title TEXT NOT NULL,
            overview TEXT NOT NULL,
            key_points_json TEXT NOT NULL,
            decisions_json TEXT NOT NULL,
            action_items_json TEXT NOT NULL,
            source_transcript_updated_at_utc TEXT NOT NULL,
            generated_at_utc TEXT NOT NULL,
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_meeting_summaries_generated_at
            ON meeting_summaries(generated_at_utc DESC);

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (5, @appliedAtUtc);
        """;

    public const string Migration6 = """
        CREATE TABLE conversation_trash (
            conversation_id TEXT NOT NULL PRIMARY KEY,
            deleted_at_utc TEXT NOT NULL,
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_conversation_trash_deleted_at
            ON conversation_trash(deleted_at_utc DESC);

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (6, @appliedAtUtc);
        """;

    public const string Migration7 = """
        ALTER TABLE speaker_voiceprints
            ADD COLUMN embedding_model_id TEXT NOT NULL DEFAULT 'unversioned';

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (7, @appliedAtUtc);
        """;

    public const string Migration8 = """
        DELETE FROM conversations
        WHERE id IN (
            SELECT conversation_id
            FROM conversation_trash);

        DELETE FROM speakers
        WHERE identification_kind = 0
          AND NOT EXISTS (
              SELECT 1
              FROM conversation_speakers
              WHERE speaker_id = speakers.id)
          AND NOT EXISTS (
              SELECT 1
              FROM transcript_segments
              WHERE speaker_id = speakers.id);

        DROP TABLE conversation_trash;

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (8, @appliedAtUtc);
        """;

    public const string Migration9 = """
        CREATE TABLE conversation_question_histories (
            conversation_id TEXT NOT NULL PRIMARY KEY,
            transcript_fingerprint TEXT NOT NULL,
            exchanges_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        INSERT INTO schema_migrations(version, applied_at_utc)
        VALUES (9, @appliedAtUtc);
        """;
}
