using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ConversationRepositoryTests
{
    [TestMethod]
    public async Task UpsertAndLoad_RoundTripsCompleteConversationAggregate()
    {
        using var context = new PersistenceTestContext();
        IConversationRepository conversations = context.GetRequiredService<IConversationRepository>();
        ISpeakerRepository speakers = context.GetRequiredService<ISpeakerRepository>();
        IConversationGroupRepository groups = context.GetRequiredService<IConversationGroupRepository>();

        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        speaker.ApplyAutomaticMatch("Alex", 0.91);
        ConversationGroup group = ConversationGroup.CreateNamed(Guid.NewGuid(), "Projekt Phoenix");
        await speakers.UpsertAsync(speaker);
        await groups.UpsertAsync(group);

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Finanzplanung August",
            new DateTimeOffset(2026, 8, 5, 10, 30, 0, TimeSpan.FromHours(2)),
            ConversationSource.ImportedAudio);
        conversation.SetDuration(TimeSpan.FromMinutes(47));
        conversation.SetDetectedLanguage("de");
        conversation.SetProcessingState(ConversationProcessingState.Ready);
        conversation.AddGroup(group.Id);
        conversation.AddSpeaker(speaker.Id);

        var segment = new TranscriptSegment(
            Guid.NewGuid(),
            speaker.Id,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(11),
            "Wir planen das Budget für August.");
        DateTimeOffset firstEdit = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondEdit = firstEdit.AddMinutes(1);
        segment.EditText("Wir planen das Budget für den August.", firstEdit);
        segment.EditText("Wir planen das August-Budget.", secondEdit);
        conversation.AddTranscriptSegment(segment);

        await conversations.UpsertAsync(conversation);
        Conversation? loaded = await conversations.GetByIdAsync(conversation.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(conversation.Id, loaded.Id);
        Assert.AreEqual("Finanzplanung August", loaded.Title);
        Assert.AreEqual(conversation.RecordedAt, loaded.RecordedAt);
        Assert.AreEqual(ConversationSource.ImportedAudio, loaded.Source);
        Assert.AreEqual(ConversationProcessingState.Ready, loaded.ProcessingState);
        Assert.AreEqual(TimeSpan.FromMinutes(47), loaded.Duration);
        Assert.AreEqual("de", loaded.DetectedLanguage);
        Assert.AreEqual(conversation.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.AreEqual(conversation.UpdatedAtUtc, loaded.UpdatedAtUtc);
        CollectionAssert.Contains(loaded.GroupIds.ToArray(), group.Id);
        CollectionAssert.Contains(loaded.SpeakerIds.ToArray(), speaker.Id);
        Assert.AreEqual(1, loaded.TranscriptSegments.Count);

        TranscriptSegment loadedSegment = loaded.TranscriptSegments[0];
        Assert.AreEqual(segment.Id, loadedSegment.Id);
        Assert.AreEqual("Wir planen das Budget für August.", loadedSegment.OriginalText);
        Assert.AreEqual("Wir planen das August-Budget.", loadedSegment.CurrentText);
        Assert.AreEqual(secondEdit, loadedSegment.LastEditedAtUtc);
        Assert.AreEqual(2, loadedSegment.Revisions.Count);
        Assert.AreEqual(
            "Wir planen das Budget für den August.",
            loadedSegment.Revisions[1].PreviousText);
    }

    [TestMethod]
    public async Task ListAsync_ReturnsNewestConversationFirst()
    {
        using var context = new PersistenceTestContext();
        IConversationRepository repository = context.GetRequiredService<IConversationRepository>();

        var older = new Conversation(
            Guid.NewGuid(),
            "Älteres Gespräch",
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.FromHours(2)),
            ConversationSource.ImportedAudio);
        var newer = new Conversation(
            Guid.NewGuid(),
            "Neueres Gespräch",
            new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.FromHours(2)),
            ConversationSource.NativeRecording);

        await repository.UpsertAsync(older);
        await repository.UpsertAsync(newer);

        IReadOnlyList<Conversation> result = await repository.ListAsync();

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(newer.Id, result[0].Id);
        Assert.AreEqual(older.Id, result[1].Id);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesConversationAndOwnedTranscriptData()
    {
        using var context = new PersistenceTestContext();
        IConversationRepository repository = context.GetRequiredService<IConversationRepository>();

        Guid speakerId = Guid.NewGuid();
        var conversation = new Conversation(
            Guid.NewGuid(),
            "Zu löschendes Gespräch",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            speakerId,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            "Test"));

        await repository.UpsertAsync(conversation);
        bool deleted = await repository.DeleteAsync(conversation.Id);
        Conversation? loaded = await repository.GetByIdAsync(conversation.Id);

        Assert.IsTrue(deleted);
        Assert.IsNull(loaded);
        Assert.IsFalse(await repository.DeleteAsync(conversation.Id));
    }
}
