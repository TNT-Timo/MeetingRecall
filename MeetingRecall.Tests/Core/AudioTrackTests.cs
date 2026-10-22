using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class AudioTrackTests
{
    [TestMethod]
    public void Constructor_NormalizesRelativePath()
    {
        Guid conversationId = Guid.NewGuid();

        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            $"Meetings\\{conversationId:N}\\Audio\\primary.mp3",
            "meeting.mp3",
            "audio/mpeg",
            1024,
            TimeSpan.FromMinutes(3));

        Assert.IsFalse(track.RelativePath.Contains('\\'));
        Assert.AreEqual(AudioTrackKind.Primary, track.Kind);
    }

    [TestMethod]
    public void Constructor_RejectsPathTraversal()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AudioTrack(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AudioTrackKind.Primary,
            "../outside.mp3",
            "outside.mp3",
            "audio/mpeg",
            1,
            TimeSpan.Zero));
    }
}
