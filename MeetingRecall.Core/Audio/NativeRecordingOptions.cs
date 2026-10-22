namespace MeetingRecall.Core.Audio;

public sealed record NativeRecordingOptions(
    string? Title,
    string? MicrophoneDeviceId,
    string? DesktopOutputDeviceId,
    bool CaptureMicrophone,
    bool CaptureDesktopAudio);
