namespace MeetingRecall.Core.Models;

public interface ILocalModelBundleService
{
    bool AreAllModelsInstalled { get; }

    int ModelCount { get; }

    long TotalDownloadSizeBytes { get; }

    Task DownloadAllAsync(
        IProgress<LocalModelBundleProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record LocalModelBundleProgress(
    string ModelName,
    int CompletedModels,
    int TotalModels,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    double? BytesPerSecond = null);
