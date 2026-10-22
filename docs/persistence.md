# Sprint 3: Local persistence

Sprint 3 adds the durable local storage foundation for MeetingRecall. It deliberately contains no cloud service, remote database, telemetry, audio processing, or AI integration.

## Storage location

At startup, MeetingRecall creates a private application workspace under:

```text
%LOCALAPPDATA%\MeetingRecall
```

The workspace contains:

```text
MeetingRecall
├── Database
│   └── MeetingRecall.db
├── Meetings
├── Models
├── Logs
├── Temp
└── Exports
```

A custom root directory can be injected for automated tests. Production uses the Windows local application-data directory.

## Database

`MeetingRecall.db` is a local SQLite database. No database server or background service is required.

The initial schema stores:

- conversations and their processing metadata;
- manual group definitions and conversation memberships;
- speakers, local identification metadata and model-versioned voiceprints;
- transcript segments with original and current text;
- complete transcript revision history;
- schema migration history.

Audio, model files, logs, temporary files, and exports remain normal files outside the database.

## Schema migrations

The database contains a `schema_migrations` table. Startup applies pending migrations transactionally and rejects databases created by a newer unsupported application version.

SQLite runs with foreign keys enabled, a five-second busy timeout, WAL journaling, and normal synchronous durability.

## Repository boundary

The Core project defines persistence interfaces:

- `IConversationRepository`
- `ISpeakerRepository`
- `IConversationGroupRepository`

Infrastructure implements them with SQLite. Core entities remain independent of SQLite and restore their complete state through internal rehydration factories, including timestamps and transcript revisions.

Conversation writes are atomic: the conversation row, group memberships, speaker memberships, transcript segments, and revisions are replaced within one transaction.

## Dependency injection

`AddMeetingRecallInfrastructure()` registers:

- application-data paths;
- the SQLite connection factory;
- database initialization;
- all repository implementations.

The WinUI application builds the service provider and initializes the database before opening the main window.

## Automated coverage

Integration tests use isolated temporary application-data folders and verify:

- creation of the complete workspace;
- idempotent database initialization;
- complete conversation and revision round trips;
- speaker and group persistence;
- newest-first conversation ordering;
- conversation deletion with owned transcript data;
- group deletion without deleting conversations.
