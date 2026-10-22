# Sprint 2: Domain model

Sprint 2 introduces the first persistence-independent domain model in `MeetingRecall.Core`.

## Entities

### Conversation

Represents one archived meeting or imported recording. It owns the meeting title, recording date, source type, processing state, duration, detected language, group memberships, known speakers, and timestamp-ordered transcript segments.

A conversation may belong to zero, one, or multiple groups. Duplicate memberships are ignored.

### ConversationGroup

Groups are created manually. A new group receives a temporary padded name such as `Gruppe 001` and remains marked as requiring a user-defined name until it is renamed.

Groups do not move or duplicate conversations. They are additional classifications over the complete archive.

### Speaker

Unknown speakers receive padded placeholders such as `Person 0001`. A speaker can later be named manually, matched automatically against a local voice profile, or identified as the owner of the microphone track during a native recording.

Automatic matches retain a confidence value. User assignments intentionally clear that value because the user decision becomes authoritative.

### TranscriptSegment

A segment contains a speaker, start and end timestamps, immutable original text, current text, and a complete revision history.

Editing never overwrites or deletes the machine-generated original. The latest edited value is used as the current text while every transition remains inspectable.

## Current boundaries

Sprint 2 contains no database mapping, file storage, audio processing, UI binding, transcription engine, or local AI integration. Those components will depend on these domain concepts in later sprints.

## Automated coverage

The test project verifies:

- multiple group membership without duplicates;
- chronological transcript ordering;
- automatic speaker registration from transcript segments;
- padded default group and speaker names;
- manual and automatic speaker identification rules;
- immutable original transcript text and revision history;
- validation of durations, timestamps, names, identifiers, and confidence values.
