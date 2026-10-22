using MeetingRecall.Core.Entities;

namespace MeetingRecall.Desktop.Services;

public sealed record AudioImportResult(
    Conversation Conversation,
    AudioTrack PrimaryAudioTrack,
    bool WasAlreadyImported);
