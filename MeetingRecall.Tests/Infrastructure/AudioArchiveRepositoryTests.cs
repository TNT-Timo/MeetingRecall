using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class AudioArchiveRepositoryTests
{
    [TestMethod]
    public async Task ImportAsync_AtomicallyStoresConversationAndPrimaryAudio()
    {
        using var context = new PersistenceTestContext();
        IAudioArchiveRepository audioRepository = context.GetRequiredService<IAudioArchiveRepository>();
        IConversationRepository conversationRepository = context.GetRequiredService<IConversationRepository>();
        Guid conversationId = Guid.NewGuid();
        string sourceSha256 = new('a', 64);
        var conversation = new Conversation(
            conversationId,
            "Budgetbesprechung",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.SetDuration(TimeSpan.FromMinutes(12));
        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            $"Meetings/{conversationId:N}/Audio/primary.mp3",
            "Budget August.mp3",
            "audio/mpeg",
            42_000,
            conversation.Duration,
            sourceSha256.ToUpperInvariant());

        await audioRepository.ImportAsync(conversation, track);

        Conversation? restoredConversation = await conversationRepository.GetByIdAsync(conversationId);
        AudioTrack? restoredTrack = await audioRepository.GetPrimaryAsync(conversationId);

        Assert.IsNotNull(restoredConversation);
        Assert.AreEqual("Budgetbesprechung", restoredConversation.Title);
        Assert.IsNotNull(restoredTrack);
        Assert.AreEqual(track.Id, restoredTrack.Id);
        Assert.AreEqual(track.RelativePath, restoredTrack.RelativePath);
        Assert.AreEqual(track.Duration, restoredTrack.Duration);
        Assert.AreEqual(sourceSha256, restoredTrack.SourceSha256);
    }

    [TestMethod]
    public async Task ImportAsync_StoresPrimaryAndRawRecordingTracksAtomically()
    {
        using var context = new PersistenceTestContext();
        IAudioArchiveRepository audioRepository = context.GetRequiredService<IAudioArchiveRepository>();
        Guid conversationId = Guid.NewGuid();
        var conversation = new Conversation(
            conversationId,
            "Native Aufnahme",
            DateTimeOffset.UtcNow,
            ConversationSource.NativeRecording);
        conversation.SetDuration(TimeSpan.FromMinutes(3));

        AudioTrack[] tracks =
        [
            new AudioTrack(
                Guid.NewGuid(),
                conversationId,
                AudioTrackKind.Primary,
                $"Meetings/{conversationId:N}/Audio/primary.wav",
                "Aufnahme.wav",
                "audio/wav",
                1000,
                conversation.Duration),
            new AudioTrack(
                Guid.NewGuid(),
                conversationId,
                AudioTrackKind.MicrophoneRaw,
                $"Meetings/{conversationId:N}/Audio/microphone.wav",
                "microphone.wav",
                "audio/wav",
                700,
                conversation.Duration),
            new AudioTrack(
                Guid.NewGuid(),
                conversationId,
                AudioTrackKind.DesktopRaw,
                $"Meetings/{conversationId:N}/Audio/desktop.wav",
                "desktop.wav",
                "audio/wav",
                900,
                conversation.Duration)
        ];

        await audioRepository.ImportAsync(conversation, tracks);

        IReadOnlyList<AudioTrack> restoredTracks = await audioRepository.ListAsync(conversationId);
        Assert.AreEqual(3, restoredTracks.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                AudioTrackKind.Primary,
                AudioTrackKind.MicrophoneRaw,
                AudioTrackKind.DesktopRaw
            },
            restoredTracks.Select(track => track.Kind).ToArray());
        Assert.AreEqual(AudioTrackKind.Primary, restoredTracks[0].Kind);
    }

    [TestMethod]
    public async Task FindPrimaryBySourceHashAsync_ReturnsBitIdenticalImport()
    {
        using var context = new PersistenceTestContext();
        IAudioArchiveRepository audioRepository = context.GetRequiredService<IAudioArchiveRepository>();
        string sourceSha256 = new('b', 64);
        Guid conversationId = Guid.NewGuid();
        var conversation = new Conversation(
            conversationId,
            "Hash-Test",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            $"Meetings/{conversationId:N}/Audio/primary.m4a",
            "test.m4a",
            "audio/mp4",
            512,
            TimeSpan.FromSeconds(10),
            sourceSha256);
        await audioRepository.ImportAsync(conversation, track);

        AudioTrack? match = await audioRepository.FindPrimaryBySourceHashAsync(sourceSha256.ToUpperInvariant());

        Assert.IsNotNull(match);
        Assert.AreEqual(track.Id, match.Id);
        Assert.AreEqual(conversationId, match.ConversationId);
    }

    [TestMethod]
    public async Task TrySetSourceHashAsync_BackfillsLegacyAudioImport()
    {
        using var context = new PersistenceTestContext();
        IAudioArchiveRepository audioRepository = context.GetRequiredService<IAudioArchiveRepository>();
        string sourceSha256 = new('c', 64);
        Guid conversationId = Guid.NewGuid();
        var conversation = new Conversation(
            conversationId,
            "Altimport",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            $"Meetings/{conversationId:N}/Audio/primary.wav",
            "legacy.wav",
            "audio/wav",
            128,
            TimeSpan.FromSeconds(1));
        await audioRepository.ImportAsync(conversation, track);

        IReadOnlyList<AudioTrack> legacyTracks = await audioRepository.ListPrimaryWithoutSourceHashAsync();
        bool updated = await audioRepository.TrySetSourceHashAsync(track.Id, sourceSha256);
        AudioTrack? match = await audioRepository.FindPrimaryBySourceHashAsync(sourceSha256);

        Assert.AreEqual(1, legacyTracks.Count);
        Assert.AreEqual(track.Id, legacyTracks[0].Id);
        Assert.IsTrue(updated);
        Assert.IsNotNull(match);
        Assert.AreEqual(track.Id, match.Id);
        Assert.AreEqual(sourceSha256, match.SourceSha256);
    }

    [TestMethod]
    public async Task DeleteConversation_CascadesToAudioTracks()
    {
        using var context = new PersistenceTestContext();
        IAudioArchiveRepository audioRepository = context.GetRequiredService<IAudioArchiveRepository>();
        IConversationRepository conversationRepository = context.GetRequiredService<IConversationRepository>();
        Guid conversationId = Guid.NewGuid();
        var conversation = new Conversation(
            conversationId,
            "Löschtest",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        var track = new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            $"Meetings/{conversationId:N}/Audio/primary.wav",
            "test.wav",
            "audio/wav",
            100,
            TimeSpan.FromSeconds(1));
        await audioRepository.ImportAsync(conversation, track);

        await conversationRepository.DeleteAsync(conversationId);

        Assert.IsNull(await audioRepository.GetPrimaryAsync(conversationId));
    }
}
