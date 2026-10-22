using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Intelligence;

public interface ILocalMeetingIntelligenceService
{
    bool IsModelInstalled { get; }

    string ModelDisplayName { get; }

    long ModelDownloadSizeBytes { get; }

    Task<MeetingSummary> GenerateSummaryAsync(
        Guid conversationId,
        IProgress<MeetingIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MeetingSummary> GenerateAnotherTitleAsync(
        Guid conversationId,
        IProgress<MeetingIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum MeetingIntelligenceStage
{
    DownloadingModel = 0,
    LoadingModel = 1,
    AnalyzingTranscript = 2,
    SummarizingSections = 3,
    BuildingSummary = 4,
    VerifyingSummary = 5,
    GeneratingTitle = 6,
    Saving = 7,
    Completed = 8
}

public sealed record MeetingIntelligenceProgress(
    MeetingIntelligenceStage Stage,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null);

public interface ILocalLanguageModelService
{
    bool IsModelInstalled { get; }

    string ModelDisplayName { get; }

    long ModelDownloadSizeBytes { get; }

    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum LocalLanguageModelStage
{
    DownloadingModel = 0,
    LoadingModel = 1,
    Generating = 2
}

public sealed record LocalLanguageModelProgress(
    LocalLanguageModelStage Stage,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null);
