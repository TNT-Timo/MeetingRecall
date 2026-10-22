using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Audio;

public sealed record NativeRecordingResult(
    Conversation Conversation,
    IReadOnlyList<AudioTrack> AudioTracks);
