using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class SpeakerTests
{
    [TestMethod]
    public void FindNextAvailableUnknownSequence_ReusesSmallestGap()
    {
        Speaker first = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        Speaker third = Speaker.CreateUnknown(Guid.NewGuid(), 3);
        Speaker fiftieth = Speaker.CreateUnknown(Guid.NewGuid(), 50);

        int sequence = Speaker.FindNextAvailableUnknownSequence(
            [first, third, fiftieth]);

        Assert.AreEqual(2, sequence);
    }

    [TestMethod]
    public void CreateUnknown_UsesPaddedDisplayName()
    {
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 12);

        Assert.AreEqual("Person 0012", speaker.DisplayName);
        Assert.AreEqual(SpeakerIdentificationKind.Unknown, speaker.IdentificationKind);
        Assert.IsFalse(speaker.IsNamed);
    }

    [TestMethod]
    public void CreateNativeMicrophoneOwner_DoesNotPretendToBeAVoiceMatch()
    {
        Speaker speaker = Speaker.CreateNativeMicrophoneOwner(Guid.NewGuid(), "Alex");

        Assert.AreEqual("Alex", speaker.DisplayName);
        Assert.AreEqual(SpeakerIdentificationKind.NativeMicrophoneOwner, speaker.IdentificationKind);
        Assert.IsTrue(speaker.IsNamed);
        Assert.IsNull(speaker.MatchConfidence);
    }

    [TestMethod]
    public void AssignName_ReplacesPlaceholderAndMarksUserAssignment()
    {
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 1);

        bool changed = speaker.AssignName(" Alex ");

        Assert.IsTrue(changed);
        Assert.AreEqual("Alex", speaker.DisplayName);
        Assert.AreEqual(SpeakerIdentificationKind.UserAssigned, speaker.IdentificationKind);
        Assert.IsTrue(speaker.IsNamed);
        Assert.IsNull(speaker.MatchConfidence);
    }

    [TestMethod]
    public void ApplyAutomaticMatch_RejectsConfidenceOutsideRange()
    {
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 1);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => speaker.ApplyAutomaticMatch("Alex", 1.01));
    }
    [TestMethod]
    public void SetColor_NormalizesAndPersistsMutedProfileColor()
    {
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 3);

        bool changed = speaker.SetColor("#9279a6");

        Assert.IsTrue(changed);
        Assert.AreEqual("#9279A6", speaker.ColorHex);
    }

    [TestMethod]
    public void SetColor_RejectsValuesOutsideRgbHexFormat()
    {
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 1);

        Assert.ThrowsExactly<ArgumentException>(
            () => speaker.SetColor("red"));
    }

}
