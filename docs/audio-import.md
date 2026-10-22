# Sprint 4: Audio import and playback

Sprint 4 adds the first complete user-facing audio workflow.

## Supported imports

Audio files:

- MP3
- WAV
- FLAC
- M4A
- AAC
- OGG
- WMA

Video containers accepted for local audio extraction:

- MP4
- MKV
- MOV
- M4V

A video import always requires explicit confirmation. The original video is never copied into MeetingRecall. Windows Media Transcoding produces a local MP3 file inside the conversation directory.

## Storage layout

Each imported conversation receives a stable UUID directory:

```text
Meetings/<conversation-id>/Audio/primary.<extension>
```

Only relative paths are stored in SQLite. Path validation prevents stored paths from escaping the MeetingRecall data directory.

## Database schema

Schema version 2 adds `audio_tracks`. It supports a primary user-facing track and is already shaped for future hidden microphone and desktop raw tracks.

The import repository stores the new conversation row and its primary audio metadata in one SQLite transaction. The file-system directory is removed if the database operation fails.

## User interface

The archive now loads persisted conversations, displays dates and durations, and opens a selected conversation in a detail view. The primary audio file can be played, paused, scrubbed, and played at 0.75x to 2x speed.

A power-user action opens the actual application data directory, including the MSIX-virtualized location.

## Deliberate boundary

Native dual-track recording is not part of this pull request. It requires a separate capture pipeline for microphone and Windows loopback audio, synchronization, recovery, and post-recording fusion. The persistence model introduced here already reserves `MicrophoneRaw` and `DesktopRaw` track kinds for that work.
