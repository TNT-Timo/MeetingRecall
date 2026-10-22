using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ReferenceDataRepositoryTests
{
    [TestMethod]
    public async Task SpeakerRepository_RoundTripsAutomaticMatchMetadata()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository repository = context.GetRequiredService<ISpeakerRepository>();
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 4);
        speaker.ApplyAutomaticMatch("Sarah", 0.84);
        speaker.SetColor("#9279A6");

        await repository.UpsertAsync(speaker);
        Speaker? loaded = await repository.GetByIdAsync(speaker.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("Sarah", loaded.DisplayName);
        Assert.AreEqual(SpeakerIdentificationKind.AutomaticallyMatched, loaded.IdentificationKind);
        Assert.AreEqual(0.84, loaded.MatchConfidence!.Value);
        Assert.AreEqual("#9279A6", loaded.ColorHex);
        Assert.AreEqual(speaker.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.AreEqual(speaker.UpdatedAtUtc, loaded.UpdatedAtUtc);
    }

    [TestMethod]
    public async Task GroupRepository_DeleteRemovesGroupMembershipWithoutDeletingConversation()
    {
        using var context = new PersistenceTestContext();
        IConversationGroupRepository groups = context.GetRequiredService<IConversationGroupRepository>();
        IConversationRepository conversations = context.GetRequiredService<IConversationRepository>();

        ConversationGroup group = ConversationGroup.CreateNamed(Guid.NewGuid(), "Kundentermine");
        await groups.UpsertAsync(group);

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Kundentermin",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddGroup(group.Id);
        await conversations.UpsertAsync(conversation);

        bool deleted = await groups.DeleteAsync(group.Id);
        Conversation? loadedConversation = await conversations.GetByIdAsync(conversation.Id);

        Assert.IsTrue(deleted);
        Assert.IsNull(await groups.GetByIdAsync(group.Id));
        Assert.IsNotNull(loadedConversation);
        Assert.IsFalse(loadedConversation.GroupIds.Contains(group.Id));
    }
    [TestMethod]
    public async Task SpeakerRepository_DeleteAnonymizesExistingTranscriptReferences()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository speakers = context.GetRequiredService<ISpeakerRepository>();
        IConversationRepository conversations = context.GetRequiredService<IConversationRepository>();

        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        speaker.AssignName("Timo");
        await speakers.UpsertAsync(speaker);

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Sprintbesprechung",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            speaker.Id,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            "Wir starten mit Sprint sieben."));
        await conversations.UpsertAsync(conversation);

        bool deleted = await speakers.DeleteAsync(speaker.Id);
        Conversation? loadedConversation =
            await conversations.GetByIdAsync(conversation.Id);
        Guid replacementId =
            loadedConversation!.TranscriptSegments.Single().SpeakerId;
        Speaker? replacement = await speakers.GetByIdAsync(replacementId);

        Assert.IsTrue(deleted);
        Assert.IsNull(await speakers.GetByIdAsync(speaker.Id));
        Assert.AreNotEqual(speaker.Id, replacementId);
        Assert.IsNotNull(replacement);
        Assert.IsFalse(replacement.IsNamed);
        StringAssert.StartsWith(replacement.DisplayName, "Person ");
    }

    [TestMethod]
    public async Task SpeakerRepository_DeleteAllAnonymizesReferencedSpeakers()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();

        Speaker referenced = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        referenced.AssignName("Alibaba");
        Speaker unreferenced = Speaker.CreateUnknown(Guid.NewGuid(), 2);
        unreferenced.AssignName("Sana");
        await speakers.UpsertAsync(referenced);
        await speakers.UpsertAsync(unreferenced);

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Unterhaltung",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            referenced.Id,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            "Ein erhaltener Transkriptabschnitt."));
        await conversations.UpsertAsync(conversation);

        int deleted = await speakers.DeleteAllAsync();
        Conversation? loadedConversation =
            await conversations.GetByIdAsync(conversation.Id);
        Guid replacementId =
            loadedConversation!.TranscriptSegments.Single().SpeakerId;
        Speaker? replacement = await speakers.GetByIdAsync(replacementId);
        IReadOnlyList<Speaker> remaining = await speakers.ListAsync();

        Assert.AreEqual(2, deleted);
        Assert.IsNull(await speakers.GetByIdAsync(referenced.Id));
        Assert.IsNull(await speakers.GetByIdAsync(unreferenced.Id));
        Assert.AreNotEqual(referenced.Id, replacementId);
        Assert.IsNotNull(replacement);
        Assert.IsFalse(replacement.IsNamed);
        Assert.AreEqual(1, remaining.Count);
    }

}
