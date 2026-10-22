using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ConversationDeletionServiceTests
{
    [TestMethod]
    public async Task DeleteAsync_RemovesFilesDatabaseGraphSpeakerAndImportFingerprint()
    {
        using var context = new PersistenceTestContext();
        var paths = context.GetRequiredService<AppDataPaths>();
        var deletionService = context.GetRequiredService<ConversationDeletionService>();
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();
        IAudioArchiveRepository audio =
            context.GetRequiredService<IAudioArchiveRepository>();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();

        Guid conversationId = Guid.NewGuid();
        Speaker detectedPerson = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        await speakers.UpsertAsync(detectedPerson);

        var conversation = new Conversation(
            conversationId,
            "Löschtest",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddSpeaker(detectedPerson.Id);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            detectedPerson.Id,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            "Test"));

        string audioDirectory = paths.GetMeetingAudioDirectory(conversationId);
        Directory.CreateDirectory(audioDirectory);
        string audioPath = Path.Combine(audioDirectory, "primary.wav");
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3, 4]);
        string sourceSha256 = new('a', 64);
        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            paths.ToRelativePath(audioPath),
            "test.wav",
            "audio/wav",
            4,
            TimeSpan.FromSeconds(2),
            sourceSha256);
        await audio.ImportAsync(conversation, track);
        await conversations.UpsertAsync(conversation);

        bool deleted = await deletionService.DeleteAsync(conversationId);

        Assert.IsTrue(deleted);
        Assert.IsFalse(Directory.Exists(paths.GetMeetingDirectory(conversationId)));
        Assert.IsNull(await conversations.GetByIdAsync(conversationId));
        Assert.IsNull(await audio.GetPrimaryAsync(conversationId));
        Assert.IsNull(await audio.FindPrimaryBySourceHashAsync(sourceSha256));
        Assert.IsNull(await speakers.GetByIdAsync(detectedPerson.Id));

        Guid replacementConversationId = Guid.NewGuid();
        var replacementConversation = new Conversation(
            replacementConversationId,
            "Neuimport",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        var replacementTrack = new AudioTrack(
            Guid.NewGuid(),
            replacementConversationId,
            AudioTrackKind.Primary,
            $"Meetings/{replacementConversationId:N}/Audio/primary.wav",
            "test.wav",
            "audio/wav",
            4,
            TimeSpan.FromSeconds(2),
            sourceSha256);

        await audio.ImportAsync(replacementConversation, replacementTrack);

        Assert.AreEqual(
            replacementConversationId,
            (await audio.FindPrimaryBySourceHashAsync(sourceSha256))?.ConversationId);
    }

    [TestMethod]
    public async Task DeleteAsync_KeepsUnknownSpeakerUsedByAnotherConversation()
    {
        using var context = new PersistenceTestContext();
        var deletionService = context.GetRequiredService<ConversationDeletionService>();
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();

        Speaker sharedPerson = Speaker.CreateUnknown(Guid.NewGuid(), 7);
        await speakers.UpsertAsync(sharedPerson);
        var first = new Conversation(
            Guid.NewGuid(),
            "Erstes Gespräch",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        first.AddSpeaker(sharedPerson.Id);
        var second = new Conversation(
            Guid.NewGuid(),
            "Zweites Gespräch",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        second.AddSpeaker(sharedPerson.Id);
        await conversations.UpsertAsync(first);
        await conversations.UpsertAsync(second);

        Assert.IsTrue(await deletionService.DeleteAsync(first.Id));

        Assert.IsNotNull(await speakers.GetByIdAsync(sharedPerson.Id));
        Assert.IsNotNull(await conversations.GetByIdAsync(second.Id));
    }
}
