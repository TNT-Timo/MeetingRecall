namespace MeetingRecall.Core.Intelligence;

public interface ILocalConversationQuestionService
{
    bool IsModelInstalled { get; }

    string ModelDisplayName { get; }

    long ModelDownloadSizeBytes { get; }

    Task<GroundedConversationAnswer> AnswerAsync(
        Guid conversationId,
        string question,
        IReadOnlyList<ConversationQuestionTurn>? recentTurns = null,
        IProgress<ConversationQuestionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceAnswer = false);
}

public sealed record ConversationQuestionTurn(
    string Question,
    string Answer);

public sealed record GroundedConversationAnswer(
    string Text,
    IReadOnlyList<ConversationAnswerSource> Sources,
    bool IsAnswerable,
    bool IsBestEffort = false)
{
    public static GroundedConversationAnswer Insufficient() =>
        new(string.Empty, [], false);
}

public sealed record ConversationAnswerSource(
    Guid SegmentId,
    TimeSpan StartTime,
    TimeSpan EndTime,
    string SpeakerName,
    string Text);

public enum ConversationQuestionStage
{
    SearchingTranscript = 0,
    DownloadingModel = 1,
    LoadingModel = 2,
    FormulatingAnswer = 3,
    VerifyingAnswer = 4,
    Completed = 5
}

public sealed record ConversationQuestionProgress(
    ConversationQuestionStage Stage,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null);
