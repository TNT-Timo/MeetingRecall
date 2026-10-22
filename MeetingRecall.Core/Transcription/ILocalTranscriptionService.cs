using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Transcription;

public interface ILocalTranscriptionService
{
    bool IsModelInstalled { get; }

    string ModelDisplayName { get; }

    long ModelDownloadSizeBytes { get; }

    bool IsSpeakerDiarizationModelInstalled { get; }

    long SpeakerDiarizationDownloadSizeBytes { get; }

    string LastRuntimeDescription { get; }

    Task<Conversation> TranscribeAsync(
        Guid conversationId,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum TranscriptionStage
{
    DownloadingModel = 0,
    DownloadingSpeakerModel = 1,
    PreparingAudio = 2,
    Transcribing = 3,
    AnalyzingSpeakers = 4,
    Saving = 5,
    Completed = 6
}

public sealed record TranscriptionProgress(
    TranscriptionStage Stage,
    double? Fraction = null,
    int SegmentCount = 0,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    int SuppressedSegmentCount = 0);
