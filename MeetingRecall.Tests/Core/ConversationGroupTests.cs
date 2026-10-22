using MeetingRecall.Core.Entities;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class ConversationGroupTests
{
    [TestMethod]
    public void CreateDefault_UsesPaddedNameAndRequiresNaming()
    {
        ConversationGroup group = ConversationGroup.CreateDefault(Guid.NewGuid(), 7);

        Assert.AreEqual("Gruppe 007", group.Name);
        Assert.IsTrue(group.NeedsNaming);
    }

    [TestMethod]
    public void Rename_TrimsNameAndClearsNamingRequirement()
    {
        ConversationGroup group = ConversationGroup.CreateDefault(Guid.NewGuid(), 1);

        bool changed = group.Rename("  Projekt Phoenix  ");

        Assert.IsTrue(changed);
        Assert.AreEqual("Projekt Phoenix", group.Name);
        Assert.IsFalse(group.NeedsNaming);
    }
}
