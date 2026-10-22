namespace MeetingRecall.Core.ValueObjects;

public sealed record TranscriptRevision(
    Guid Id,
    string PreviousText,
    string RevisedText,
    DateTimeOffset EditedAtUtc);
