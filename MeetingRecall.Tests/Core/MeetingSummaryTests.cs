using MeetingRecall.Core.Entities;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class MeetingSummaryTests
{
    [TestMethod]
    public void Create_PreservesStructuredSummarySections()
    {
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset sourceUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        MeetingSummary summary = MeetingSummary.Create(
            conversationId,
            "Migration · Zeitplan",
            "Die Migration und der Zeitplan wurden besprochen.",
            ["Die Migration startet im September."],
            ["Der Pilot beginnt am Montag."],
            [new MeetingActionItem("Testdaten vorbereiten.", "Sana")],
            sourceUpdatedAt);

        Assert.AreEqual(conversationId, summary.ConversationId);
        Assert.AreEqual("Migration · Zeitplan", summary.SuggestedTitle);
        Assert.AreEqual(1, summary.KeyPoints.Count);
        Assert.AreEqual(1, summary.Decisions.Count);
        Assert.AreEqual(1, summary.ActionItems.Count);
        Assert.AreEqual("Sana", summary.ActionItems[0].Owner);
        Assert.IsFalse(summary.IsStale(sourceUpdatedAt));
        Assert.IsTrue(summary.IsStale(sourceUpdatedAt.AddMinutes(1)));
    }

    [TestMethod]
    public void Create_RejectsEmptyOverview()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MeetingSummary.Create(
            Guid.NewGuid(),
            "Titel",
            " ",
            [],
            [],
            [],
            DateTimeOffset.UtcNow));
    }
}
