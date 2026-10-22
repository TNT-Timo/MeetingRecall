using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class ConversationTests
{
    [TestMethod]
    public void Conversation_CanBelongToMultipleGroupsWithoutDuplicates()
    {
        var conversation = CreateConversation();
        Guid firstGroup = Guid.NewGuid();
        Guid secondGroup = Guid.NewGuid();

        Assert.IsTrue(conversation.AddGroup(firstGroup));
        Assert.IsTrue(conversation.AddGroup(secondGroup));
        Assert.IsFalse(conversation.AddGroup(firstGroup));

        CollectionAssert.AreEquivalent(
            new[] { firstGroup, secondGroup },
            conversation.GroupIds.ToArray());
    }

    [TestMethod]
    public void AddTranscriptSegment_OrdersSegmentsAndRegistersSpeakers()
    {
        var conversation = CreateConversation();
        Guid firstSpeaker = Guid.NewGuid();
        Guid secondSpeaker = Guid.NewGuid();
        var laterSegment = new TranscriptSegment(
            Guid.NewGuid(),
            secondSpeaker,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(25),
            "Späterer Beitrag");
        var earlierSegment = new TranscriptSegment(
            Guid.NewGuid(),
            firstSpeaker,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(8),
            "Früherer Beitrag");

        conversation.AddTranscriptSegment(laterSegment);
        conversation.AddTranscriptSegment(earlierSegment);

        Assert.AreSame(earlierSegment, conversation.TranscriptSegments[0]);
        Assert.AreSame(laterSegment, conversation.TranscriptSegments[1]);
        CollectionAssert.AreEquivalent(
            new[] { firstSpeaker, secondSpeaker },
            conversation.SpeakerIds.ToArray());
    }

    [TestMethod]
    public void ReassignTranscriptSegmentSpeaker_RegistersReplacementSpeaker()
    {
        var conversation = CreateConversation();
        var segment = new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            "Hallo");
        Guid replacementSpeaker = Guid.NewGuid();
        conversation.AddTranscriptSegment(segment);

        bool changed = conversation.ReassignTranscriptSegmentSpeaker(segment.Id, replacementSpeaker);

        Assert.IsTrue(changed);
        Assert.AreEqual(replacementSpeaker, segment.SpeakerId);
        CollectionAssert.Contains(conversation.SpeakerIds.ToArray(), replacementSpeaker);
    }

    [TestMethod]
    public void SetDuration_RejectsNegativeValues()
    {
        var conversation = CreateConversation();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => conversation.SetDuration(TimeSpan.FromSeconds(-1)));
    }

    [TestMethod]
    public void Rename_TrimsAndUpdatesConversationTitle()
    {
        var conversation = CreateConversation();

        bool changed = conversation.Rename("  Projektstatus August  ");

        Assert.IsTrue(changed);
        Assert.AreEqual("Projektstatus August", conversation.Title);
        Assert.IsFalse(conversation.Rename("Projektstatus August"));
    }

    [TestMethod]
    public void Rename_RejectsBlankTitle()
    {
        var conversation = CreateConversation();

        Assert.ThrowsExactly<ArgumentException>(() => conversation.Rename("   "));
    }

    private static Conversation CreateConversation()
    {
        return new Conversation(
            Guid.NewGuid(),
            "Testgespräch",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
    }
}
