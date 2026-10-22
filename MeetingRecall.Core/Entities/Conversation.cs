using MeetingRecall.Core.Enums;

namespace MeetingRecall.Core.Entities;

public sealed class Conversation
{
    private const int MaximumTitleLength = 240;
    private const int MaximumLanguageCodeLength = 35;

    private readonly HashSet<Guid> _groupIds = [];
    private readonly HashSet<Guid> _speakerIds = [];
    private readonly List<TranscriptSegment> _transcriptSegments = [];

    public Conversation(
        Guid id,
        string title,
        DateTimeOffset recordedAt,
        ConversationSource source)
    {
        ValidateIdentifier(id, nameof(id));

        Id = id;
        Title = ValidateTitle(title);
        RecordedAt = recordedAt;
        Source = source;
        ProcessingState = ConversationProcessingState.Pending;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    private Conversation(
        Guid id,
        string title,
        DateTimeOffset recordedAt,
        ConversationSource source,
        ConversationProcessingState processingState,
        TimeSpan duration,
        string? detectedLanguage,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IEnumerable<Guid> groupIds,
        IEnumerable<Guid> speakerIds,
        IEnumerable<TranscriptSegment> transcriptSegments)
    {
        ValidateIdentifier(id, nameof(id));

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A conversation duration cannot be negative.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("The updated timestamp cannot precede the created timestamp.", nameof(updatedAtUtc));
        }

        Id = id;
        Title = ValidateTitle(title);
        RecordedAt = recordedAt;
        Source = source;
        ProcessingState = processingState;
        Duration = duration;
        DetectedLanguage = NormalizeLanguage(detectedLanguage);
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;

        foreach (Guid groupId in groupIds)
        {
            ValidateIdentifier(groupId, nameof(groupIds));
            _groupIds.Add(groupId);
        }

        foreach (Guid speakerId in speakerIds)
        {
            ValidateIdentifier(speakerId, nameof(speakerIds));
            _speakerIds.Add(speakerId);
        }

        foreach (TranscriptSegment segment in transcriptSegments.OrderBy(segment => segment.StartTime))
        {
            ArgumentNullException.ThrowIfNull(segment);

            if (_transcriptSegments.Any(existing => existing.Id == segment.Id))
            {
                throw new ArgumentException($"Duplicate transcript segment '{segment.Id}'.", nameof(transcriptSegments));
            }

            _transcriptSegments.Add(segment);
            _speakerIds.Add(segment.SpeakerId);
        }
    }

    public Guid Id { get; }

    public string Title { get; private set; }

    public DateTimeOffset RecordedAt { get; }

    public ConversationSource Source { get; }

    public ConversationProcessingState ProcessingState { get; private set; }

    public TimeSpan Duration { get; private set; }

    public string? DetectedLanguage { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<Guid> GroupIds => _groupIds;

    public IReadOnlyCollection<Guid> SpeakerIds => _speakerIds;

    public IReadOnlyList<TranscriptSegment> TranscriptSegments => _transcriptSegments;

    internal static Conversation Restore(
        Guid id,
        string title,
        DateTimeOffset recordedAt,
        ConversationSource source,
        ConversationProcessingState processingState,
        TimeSpan duration,
        string? detectedLanguage,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IEnumerable<Guid> groupIds,
        IEnumerable<Guid> speakerIds,
        IEnumerable<TranscriptSegment> transcriptSegments)
    {
        return new Conversation(
            id,
            title,
            recordedAt,
            source,
            processingState,
            duration,
            detectedLanguage,
            createdAtUtc,
            updatedAtUtc,
            groupIds,
            speakerIds,
            transcriptSegments);
    }

    public bool Rename(string title)
    {
        string validatedTitle = ValidateTitle(title);
        if (string.Equals(Title, validatedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        Title = validatedTitle;
        Touch();
        return true;
    }

    public bool SetDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A conversation duration cannot be negative.");
        }

        if (Duration == duration)
        {
            return false;
        }

        Duration = duration;
        Touch();
        return true;
    }

    public bool SetDetectedLanguage(string? languageCode)
    {
        string? normalizedLanguage = NormalizeLanguage(languageCode);
        if (string.Equals(DetectedLanguage, normalizedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DetectedLanguage = normalizedLanguage;
        Touch();
        return true;
    }

    public bool SetProcessingState(ConversationProcessingState processingState)
    {
        if (ProcessingState == processingState)
        {
            return false;
        }

        ProcessingState = processingState;
        Touch();
        return true;
    }

    public bool AddGroup(Guid groupId)
    {
        ValidateIdentifier(groupId, nameof(groupId));

        bool added = _groupIds.Add(groupId);
        if (added)
        {
            Touch();
        }

        return added;
    }

    public bool RemoveGroup(Guid groupId)
    {
        ValidateIdentifier(groupId, nameof(groupId));

        bool removed = _groupIds.Remove(groupId);
        if (removed)
        {
            Touch();
        }

        return removed;
    }

    public bool AddSpeaker(Guid speakerId)
    {
        ValidateIdentifier(speakerId, nameof(speakerId));

        bool added = _speakerIds.Add(speakerId);
        if (added)
        {
            Touch();
        }

        return added;
    }

    public void AddTranscriptSegment(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (_transcriptSegments.Any(existing => existing.Id == segment.Id))
        {
            throw new InvalidOperationException($"Transcript segment '{segment.Id}' already belongs to this conversation.");
        }

        int insertionIndex = _transcriptSegments.FindIndex(existing => existing.StartTime > segment.StartTime);
        if (insertionIndex < 0)
        {
            _transcriptSegments.Add(segment);
        }
        else
        {
            _transcriptSegments.Insert(insertionIndex, segment);
        }

        _speakerIds.Add(segment.SpeakerId);
        Touch();
    }

    public bool ReassignTranscriptSegmentSpeaker(Guid segmentId, Guid speakerId)
    {
        ValidateIdentifier(segmentId, nameof(segmentId));
        ValidateIdentifier(speakerId, nameof(speakerId));

        TranscriptSegment segment = _transcriptSegments.FirstOrDefault(existing => existing.Id == segmentId)
            ?? throw new KeyNotFoundException($"Transcript segment '{segmentId}' does not belong to this conversation.");

        bool changed = segment.ReassignSpeaker(speakerId);
        if (changed)
        {
            _speakerIds.Add(speakerId);
            Touch();
        }

        return changed;
    }

    private static string ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        string trimmedTitle = title.Trim();
        if (trimmedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentException(
                $"A conversation title may not exceed {MaximumTitleLength} characters.",
                nameof(title));
        }

        return trimmedTitle;
    }

    private static string? NormalizeLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string trimmedLanguage = languageCode.Trim();
        if (trimmedLanguage.Length > MaximumLanguageCodeLength)
        {
            throw new ArgumentException(
                $"A language code may not exceed {MaximumLanguageCodeLength} characters.",
                nameof(languageCode));
        }

        return trimmedLanguage;
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }

    private void Touch()
    {
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
