using MeetingRecall.Core.Audio;

namespace MeetingRecall.Core.Interfaces;

public interface INativeAudioRecordingService
{
    bool IsRecording { get; }

    DateTimeOffset? StartedAt { get; }

    TimeSpan Elapsed { get; }

    Task<IReadOnlyList<AudioCaptureDevice>> ListMicrophonesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudioCaptureDevice>> ListDesktopOutputsAsync(
        CancellationToken cancellationToken = default);

    Task StartAsync(
        NativeRecordingOptions options,
        CancellationToken cancellationToken = default);

    Task<NativeRecordingResult> StopAsync(
        CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}
