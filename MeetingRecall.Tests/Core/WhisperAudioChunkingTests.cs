using MeetingRecall.Core.Transcription;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class WhisperAudioChunkingTests
{
    [TestMethod]
    public void Plan_ShortAudioCreatesExactlyOneChunk()
    {
        TimeSpan duration = TimeSpan.FromSeconds(52);

        IReadOnlyList<WhisperAudioChunk> chunks =
            WhisperAudioChunking.Plan(duration);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(TimeSpan.Zero, chunks[0].OwnedStart);
        Assert.AreEqual(duration, chunks[0].OwnedEnd);
    }

    [TestMethod]
    public void Plan_LongAudioCreatesContinuousNonOverlappingOwnership()
    {
        TimeSpan duration = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(2);

        IReadOnlyList<WhisperAudioChunk> chunks =
            WhisperAudioChunking.Plan(duration);

        Assert.IsGreaterThan(1, chunks.Count);
        Assert.AreEqual(TimeSpan.FromMinutes(1), chunks[0].Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), chunks[0].End - chunks[1].Start);
        Assert.AreEqual(TimeSpan.Zero, chunks[0].OwnedStart);
        Assert.AreEqual(duration, chunks[^1].OwnedEnd);
        Assert.IsTrue(chunks.All(chunk =>
            chunk.Duration <= WhisperAudioChunking.DefaultChunkDuration));
        for (int index = 1; index < chunks.Count; index++)
        {
            Assert.AreEqual(
                chunks[index - 1].OwnedEnd,
                chunks[index].OwnedStart);
            Assert.AreEqual(
                WhisperAudioChunking.DefaultOverlap,
                chunks[index - 1].End - chunks[index].Start);
        }
    }

    [TestMethod]
    public void OwnsSegment_AssignsOverlapToExactlyOneChunk()
    {
        TimeSpan duration = TimeSpan.FromMinutes(12);
        IReadOnlyList<WhisperAudioChunk> chunks =
            WhisperAudioChunking.Plan(duration);
        TimeSpan boundary = chunks[0].OwnedEnd;

        bool firstOwns = WhisperAudioChunking.OwnsSegment(
            chunks[0],
            boundary - TimeSpan.FromSeconds(2),
            boundary + TimeSpan.FromSeconds(2),
            duration);
        bool secondOwns = WhisperAudioChunking.OwnsSegment(
            chunks[1],
            boundary - TimeSpan.FromSeconds(2),
            boundary + TimeSpan.FromSeconds(2),
            duration);

        Assert.IsFalse(firstOwns);
        Assert.IsTrue(secondOwns);
    }

}
