using MeetingRecall.Core.ValueObjects;

namespace MeetingRecall.Core.Entities;

public sealed class TranscriptSegment
{
    private readonly List<TranscriptRevision> _revisions = [];

    public TranscriptSegment(
        Guid id,
        Guid speakerId,
        TimeSpan startTime,
        TimeSpan endTime,
        string originalText)
    {
        Validate(id, speakerId, startTime, endTime);

        Id = id;
        SpeakerId = speakerId;
        StartTime = startTime;
        EndTime = endTime;
        OriginalText = ValidateText(originalText, nameof(originalText));
        CurrentText = OriginalText;
    }

    private TranscriptSegment(
        Guid id,
        Guid speakerId,
        TimeSpan startTime,
        TimeSpan endTime,
        string originalText,
        string currentText,
        DateTimeOffset? lastEditedAtUtc,
        IEnumerable<TranscriptRevision> revisions)
    {
        Validate(id, speakerId, startTime, endTime);

        Id = id;
        SpeakerId = speakerId;
        StartTime = startTime;
        EndTime = endTime;
        OriginalText = ValidateText(originalText, nameof(originalText));
        CurrentText = ValidateText(currentText, nameof(currentText));
        LastEditedAtUtc = lastEditedAtUtc;

        foreach (TranscriptRevision revision in revisions)
        {
            ArgumentNullException.ThrowIfNull(revision);
            _revisions.Add(revision);
        }

        if (_revisions.Count == 0 && !string.Equals(OriginalText, CurrentText, StringComparison.Ordinal))
        {
            throw new ArgumentException("Edited text requires at least one revision.", nameof(revisions));
        }

        if (_revisions.Count > 0 && LastEditedAtUtc is null)
        {
            throw new ArgumentException("Edited text requires a last-edited timestamp.", nameof(lastEditedAtUtc));
        }
    }

    public Guid Id { get; }

    public Guid SpeakerId { get; private set; }

    public TimeSpan StartTime { get; }

    public TimeSpan EndTime { get; }

    public string OriginalText { get; }

    public string CurrentText { get; private set; }

    public bool IsEdited => _revisions.Count > 0;

    public DateTimeOffset? LastEditedAtUtc { get; private set; }

    public IReadOnlyList<TranscriptRevision> Revisions => _revisions;

    internal static TranscriptSegment Restore(
        Guid id,
        Guid speakerId,
        TimeSpan startTime,
        TimeSpan endTime,
        string originalText,
        string currentText,
        DateTimeOffset? lastEditedAtUtc,
        IEnumerable<TranscriptRevision> revisions)
    {
        return new TranscriptSegment(
            id,
            speakerId,
            startTime,
            endTime,
            originalText,
            currentText,
            lastEditedAtUtc,
            revisions);
    }

    public bool EditText(string revisedText, DateTimeOffset editedAtUtc)
    {
        string validatedText = ValidateText(revisedText, nameof(revisedText));
        if (string.Equals(CurrentText, validatedText, StringComparison.Ordinal))
        {
            return false;
        }

        _revisions.Add(new TranscriptRevision(
            Guid.NewGuid(),
            CurrentText,
            validatedText,
            editedAtUtc));

        CurrentText = validatedText;
        LastEditedAtUtc = editedAtUtc;
        return true;
    }

    internal bool ReassignSpeaker(Guid speakerId)
    {
        if (speakerId == Guid.Empty)
        {
            throw new ArgumentException("A transcript segment requires a speaker.", nameof(speakerId));
        }

        if (SpeakerId == speakerId)
        {
            return false;
        }

        SpeakerId = speakerId;
        return true;
    }

    private static void Validate(
        Guid id,
        Guid speakerId,
        TimeSpan startTime,
        TimeSpan endTime)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A transcript segment requires a non-empty identifier.", nameof(id));
        }

        if (speakerId == Guid.Empty)
        {
            throw new ArgumentException("A transcript segment requires a speaker.", nameof(speakerId));
        }

        if (startTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startTime), "A segment cannot start before the recording.");
        }

        if (endTime < startTime)
        {
            throw new ArgumentOutOfRangeException(nameof(endTime), "A segment cannot end before it starts.");
        }
    }

    private static string ValidateText(string text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Transcript text cannot be empty.", parameterName);
        }

        return text;
    }
}
