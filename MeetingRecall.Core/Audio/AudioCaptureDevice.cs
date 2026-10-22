namespace MeetingRecall.Core.Audio;

public sealed record AudioCaptureDevice(
    string Id,
    string DisplayName,
    bool IsDefault)
{
    public override string ToString() => DisplayName;
}
