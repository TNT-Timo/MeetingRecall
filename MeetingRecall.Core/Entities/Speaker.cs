using System.Globalization;
using MeetingRecall.Core.Enums;

namespace MeetingRecall.Core.Entities;

public sealed class Speaker
{
    private const int MaximumDisplayNameLength = 120;

    private Speaker(
        Guid id,
        string displayName,
        string colorHex,
        SpeakerIdentificationKind identificationKind,
        double? matchConfidence,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A speaker requires a non-empty identifier.", nameof(id));
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("The updated timestamp cannot precede the created timestamp.", nameof(updatedAtUtc));
        }

        ValidateConfidence(identificationKind, matchConfidence);

        Id = id;
        DisplayName = ValidateDisplayName(displayName);
        ColorHex = SpeakerColorPalette.Normalize(colorHex);
        IdentificationKind = identificationKind;
        MatchConfidence = matchConfidence;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public string DisplayName { get; private set; }

    public string ColorHex { get; private set; }

    public SpeakerIdentificationKind IdentificationKind { get; private set; }

    public double? MatchConfidence { get; private set; }

    public bool IsNamed => IdentificationKind != SpeakerIdentificationKind.Unknown;

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Speaker CreateUnknown(Guid id, int sequenceNumber)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "The sequence number must be positive.");
        }

        string displayName = string.Format(CultureInfo.InvariantCulture, "Person {0:0000}", sequenceNumber);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new Speaker(
            id,
            displayName,
            SpeakerColorPalette.ForSequence(sequenceNumber),
            SpeakerIdentificationKind.Unknown,
            matchConfidence: null,
            now,
            now);
    }

    public static int FindNextAvailableUnknownSequence(
        IEnumerable<Speaker> speakers)
    {
        ArgumentNullException.ThrowIfNull(speakers);

        var usedSequences = new HashSet<int>();
        foreach (Speaker speaker in speakers)
        {
            if (speaker.DisplayName.StartsWith("Person ", StringComparison.Ordinal)
                && int.TryParse(
                    speaker.DisplayName.AsSpan("Person ".Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int sequence)
                && sequence > 0)
            {
                usedSequences.Add(sequence);
            }
        }

        int candidate = 1;
        while (usedSequences.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    public static Speaker CreateNativeMicrophoneOwner(Guid id, string displayName)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new Speaker(
            id,
            displayName,
            SpeakerColorPalette.ForSequence(1),
            SpeakerIdentificationKind.NativeMicrophoneOwner,
            matchConfidence: null,
            now,
            now);
    }

    internal static Speaker Restore(
        Guid id,
        string displayName,
        string colorHex,
        SpeakerIdentificationKind identificationKind,
        double? matchConfidence,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        return new Speaker(
            id,
            displayName,
            colorHex,
            identificationKind,
            matchConfidence,
            createdAtUtc,
            updatedAtUtc);
    }

    public bool AssignName(string displayName)
    {
        string validatedName = ValidateDisplayName(displayName);
        if (string.Equals(DisplayName, validatedName, StringComparison.Ordinal)
            && IdentificationKind == SpeakerIdentificationKind.UserAssigned)
        {
            return false;
        }

        DisplayName = validatedName;
        IdentificationKind = SpeakerIdentificationKind.UserAssigned;
        MatchConfidence = null;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public bool SetColor(string colorHex)
    {
        string normalized = SpeakerColorPalette.Normalize(colorHex);
        if (string.Equals(ColorHex, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        ColorHex = normalized;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public bool ApplyAutomaticMatch(string displayName, double confidence)
    {
        ValidateConfidence(SpeakerIdentificationKind.AutomaticallyMatched, confidence);

        string validatedName = ValidateDisplayName(displayName);
        if (string.Equals(DisplayName, validatedName, StringComparison.Ordinal)
            && IdentificationKind == SpeakerIdentificationKind.AutomaticallyMatched
            && MatchConfidence == confidence)
        {
            return false;
        }

        DisplayName = validatedName;
        IdentificationKind = SpeakerIdentificationKind.AutomaticallyMatched;
        MatchConfidence = confidence;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private static void ValidateConfidence(
        SpeakerIdentificationKind identificationKind,
        double? matchConfidence)
    {
        if (identificationKind == SpeakerIdentificationKind.AutomaticallyMatched)
        {
            if (matchConfidence is null or < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(matchConfidence),
                    "An automatic match requires confidence between 0 and 1.");
            }

            return;
        }

        if (matchConfidence is not null)
        {
            throw new ArgumentException(
                "Only automatically matched speakers may have a match confidence.",
                nameof(matchConfidence));
        }
    }

    private static string ValidateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        string trimmedName = displayName.Trim();
        if (trimmedName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"A speaker name may not exceed {MaximumDisplayNameLength} characters.",
                nameof(displayName));
        }

        return trimmedName;
    }
}
