using MeetingRecall.Core.Models;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Infrastructure.Models;

internal sealed class LocalModelBundleService(
    WhisperModelManager whisperModels,
    SpeakerDiarizationModelManager speakerModels,
    SummaryLanguageModelManager languageModels) : ILocalModelBundleService
{
    private const int WhisperModelCount = 1;
    private const int SpeakerModelCount = 2;
    private const int LanguageModelCount = 1;

    public bool AreAllModelsInstalled =>
        whisperModels.IsInstalled
        && speakerModels.IsInstalled
        && languageModels.IsModelInstalled;

    public int ModelCount =>
        WhisperModelCount + SpeakerModelCount + LanguageModelCount;

    public long TotalDownloadSizeBytes =>
        whisperModels.ModelDownloadSizeBytes
        + speakerModels.DownloadSizeBytes
        + languageModels.ModelDownloadSizeBytes;

    public async Task DownloadAllAsync(
        IProgress<LocalModelBundleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long totalBytes = TotalDownloadSizeBytes;
        long completedBytes = 0;

        await whisperModels
            .EnsureBundleModelInstalledAsync(
                progress,
                completedModelOffset: 0,
                completedByteOffset: completedBytes,
                totalModelCount: ModelCount,
                totalBytes,
                cancellationToken)
            .ConfigureAwait(false);
        completedBytes += whisperModels.ModelDownloadSizeBytes;

        long speakerByteOffset = completedBytes;
        var speakerProgress = new ImmediateProgress<TranscriptionProgress>(item =>
        {
            long familyBytes = Math.Clamp(
                item.DownloadedBytes,
                0,
                speakerModels.DownloadSizeBytes);
            long overallBytes = speakerByteOffset + familyBytes;
            progress?.Report(new LocalModelBundleProgress(
                "Neuronale Sprechererkennung",
                WhisperModelCount,
                ModelCount,
                totalBytes > 0 ? overallBytes / (double)totalBytes : null,
                overallBytes,
                totalBytes,
                item.BytesPerSecond));
        });
        await speakerModels
            .EnsureInstalledAsync(speakerProgress, cancellationToken)
            .ConfigureAwait(false);
        completedBytes += speakerModels.DownloadSizeBytes;
        progress?.Report(new LocalModelBundleProgress(
            "Neuronale Sprechererkennung",
            WhisperModelCount + SpeakerModelCount,
            ModelCount,
            totalBytes > 0 ? completedBytes / (double)totalBytes : 1,
            completedBytes,
            totalBytes));

        await languageModels
            .EnsureBundleModelInstalledAsync(
                progress,
                completedModelOffset: WhisperModelCount + SpeakerModelCount,
                completedByteOffset: completedBytes,
                totalModelCount: ModelCount,
                totalBytes,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new LocalModelBundleProgress(
            "Alle Modelle",
            ModelCount,
            ModelCount,
            1,
            totalBytes,
            totalBytes));
    }
}

internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
