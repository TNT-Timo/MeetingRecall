using MeetingRecall.Core.Entities;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class TranscriptSegmentTests
{
    [TestMethod]
    public void EditText_PreservesOriginalAndCompleteRevisionHistory()
    {
        var segment = new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(14),
            "Wir liefern das vielleicht nächste Woche.");
        DateTimeOffset firstEdit = DateTimeOffset.UtcNow;
        DateTimeOffset secondEdit = firstEdit.AddMinutes(1);

        segment.EditText("Wir liefern das vermutlich nächste Woche.", firstEdit);
        segment.EditText("Wir liefern nächste Woche.", secondEdit);

        Assert.AreEqual("Wir liefern das vielleicht nächste Woche.", segment.OriginalText);
        Assert.AreEqual("Wir liefern nächste Woche.", segment.CurrentText);
        Assert.IsTrue(segment.IsEdited);
        Assert.AreEqual(secondEdit, segment.LastEditedAtUtc);
        Assert.AreEqual(2, segment.Revisions.Count);
        Assert.AreEqual(
            "Wir liefern das vermutlich nächste Woche.",
            segment.Revisions[1].PreviousText);
    }

    [TestMethod]
    public void EditText_WithCurrentText_DoesNotCreateRevision()
    {
        var segment = new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            "Unverändert");

        bool changed = segment.EditText("Unverändert", DateTimeOffset.UtcNow);

        Assert.IsFalse(changed);
        Assert.IsFalse(segment.IsEdited);
        Assert.AreEqual(0, segment.Revisions.Count);
    }

    [TestMethod]
    public void Constructor_RejectsEndBeforeStart()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(4),
            "Ungültig"));
    }
}
