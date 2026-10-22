namespace MeetingRecall.Core.Entities;

public sealed class MeetingSummary
{
    private const int MaximumTitleLength = 120;
    private const int MaximumOverviewLength = 4_000;
    private const int MaximumItemLength = 1_000;
    private const int MaximumItemsPerSection = 12;

    private readonly IReadOnlyList<string> _keyPoints;
    private readonly IReadOnlyList<string> _decisions;
    private readonly IReadOnlyList<MeetingActionItem> _actionItems;

    private MeetingSummary(
        Guid conversationId,
        string suggestedTitle,
        string overview,
        IEnumerable<string> keyPoints,
        IEnumerable<string> decisions,
        IEnumerable<MeetingActionItem> actionItems,
        DateTimeOffset sourceTranscriptUpdatedAtUtc,
        DateTimeOffset generatedAtUtc)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A meeting summary requires a conversation.",
                nameof(conversationId));
        }

        if (sourceTranscriptUpdatedAtUtc > generatedAtUtc)
        {
            throw new ArgumentException(
                "A summary cannot predate its source transcript.",
                nameof(generatedAtUtc));
        }

        ConversationId = conversationId;
        SuggestedTitle = ValidateText(
            suggestedTitle,
            MaximumTitleLength,
            nameof(suggestedTitle));
        Overview = ValidateText(overview, MaximumOverviewLength, nameof(overview));
        _keyPoints = ValidateItems(keyPoints, nameof(keyPoints));
        _decisions = ValidateItems(decisions, nameof(decisions));
        _actionItems = actionItems
            .Take(MaximumItemsPerSection + 1)
            .Select(item => item ?? throw new ArgumentException(
                "Action items cannot contain null values.",
                nameof(actionItems)))
            .ToArray();

        if (_actionItems.Count > MaximumItemsPerSection)
        {
            throw new ArgumentException(
                $"A summary supports at most {MaximumItemsPerSection} action items.",
                nameof(actionItems));
        }

        SourceTranscriptUpdatedAtUtc = sourceTranscriptUpdatedAtUtc;
        GeneratedAtUtc = generatedAtUtc;
    }

    public Guid ConversationId { get; }

    public string SuggestedTitle { get; }

    public string Overview { get; }

    public IReadOnlyList<string> KeyPoints => _keyPoints;

    public IReadOnlyList<string> Decisions => _decisions;

    public IReadOnlyList<MeetingActionItem> ActionItems => _actionItems;

    public DateTimeOffset SourceTranscriptUpdatedAtUtc { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public bool IsStale(DateTimeOffset conversationUpdatedAtUtc) =>
        conversationUpdatedAtUtc > SourceTranscriptUpdatedAtUtc;

    public static MeetingSummary Create(
        Guid conversationId,
        string suggestedTitle,
        string overview,
        IEnumerable<string> keyPoints,
        IEnumerable<string> decisions,
        IEnumerable<MeetingActionItem> actionItems,
        DateTimeOffset sourceTranscriptUpdatedAtUtc)
    {
        return new MeetingSummary(
            conversationId,
            suggestedTitle,
            overview,
            keyPoints,
            decisions,
            actionItems,
            sourceTranscriptUpdatedAtUtc,
            DateTimeOffset.UtcNow);
    }

    public MeetingSummary WithSuggestedTitle(string suggestedTitle)
    {
        return new MeetingSummary(
            ConversationId,
            suggestedTitle,
            Overview,
            KeyPoints,
            Decisions,
            ActionItems,
            SourceTranscriptUpdatedAtUtc,
            GeneratedAtUtc);
    }

    internal static MeetingSummary Restore(
        Guid conversationId,
        string suggestedTitle,
        string overview,
        IEnumerable<string> keyPoints,
        IEnumerable<string> decisions,
        IEnumerable<MeetingActionItem> actionItems,
        DateTimeOffset sourceTranscriptUpdatedAtUtc,
        DateTimeOffset generatedAtUtc)
    {
        return new MeetingSummary(
            conversationId,
            suggestedTitle,
            overview,
            keyPoints,
            decisions,
            actionItems,
            sourceTranscriptUpdatedAtUtc,
            generatedAtUtc);
    }

    private static IReadOnlyList<string> ValidateItems(
        IEnumerable<string> items,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items);
        string[] validated = items
            .Take(MaximumItemsPerSection + 1)
            .Select(item => ValidateText(item, MaximumItemLength, parameterName))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (validated.Length > MaximumItemsPerSection)
        {
            throw new ArgumentException(
                $"A summary section supports at most {MaximumItemsPerSection} items.",
                parameterName);
        }

        return validated;
    }

    private static string ValidateText(
        string text,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string trimmed = text.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Text may not exceed {maximumLength} characters.",
                parameterName);
        }

        return trimmed;
    }
}

public sealed record MeetingActionItem
{
    public MeetingActionItem(string description, string? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description.Trim();
        Owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
    }

    public string Description { get; }

    public string? Owner { get; }
}
