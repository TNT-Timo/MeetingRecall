using System.Globalization;

namespace MeetingRecall.Core.Entities;

public sealed class ConversationGroup
{
    private const int MaximumNameLength = 120;

    private ConversationGroup(
        Guid id,
        string name,
        bool needsNaming,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A group requires a non-empty identifier.", nameof(id));
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("The updated timestamp cannot precede the created timestamp.", nameof(updatedAtUtc));
        }

        Id = id;
        Name = ValidateName(name);
        NeedsNaming = needsNaming;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public bool NeedsNaming { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static ConversationGroup CreateDefault(Guid id, int sequenceNumber)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "The sequence number must be positive.");
        }

        string name = string.Format(CultureInfo.InvariantCulture, "Gruppe {0:000}", sequenceNumber);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ConversationGroup(id, name, needsNaming: true, now, now);
    }

    public static ConversationGroup CreateNamed(Guid id, string name)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ConversationGroup(id, name, needsNaming: false, now, now);
    }

    internal static ConversationGroup Restore(
        Guid id,
        string name,
        bool needsNaming,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        return new ConversationGroup(id, name, needsNaming, createdAtUtc, updatedAtUtc);
    }

    public bool Rename(string name)
    {
        string validatedName = ValidateName(name);
        if (string.Equals(Name, validatedName, StringComparison.Ordinal) && !NeedsNaming)
        {
            return false;
        }

        Name = validatedName;
        NeedsNaming = false;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmedName = name.Trim();
        if (trimmedName.Length > MaximumNameLength)
        {
            throw new ArgumentException($"A group name may not exceed {MaximumNameLength} characters.", nameof(name));
        }

        return trimmedName;
    }
}
