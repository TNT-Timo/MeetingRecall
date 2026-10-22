using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ConversationQuestionHistoryRepositoryTests
{
    [TestMethod]
    public async Task SaveAndReload_RoundTripsCompleteChatHistory()
    {
        using var context = new PersistenceTestContext();
        Conversation conversation = await CreateConversationAsync(context);
        IConversationQuestionHistoryRepository histories =
            context.GetRequiredService<IConversationQuestionHistoryRepository>();
        Guid answeredId = Guid.NewGuid();
        Guid unansweredId = Guid.NewGuid();
        var answer = new GroundedConversationAnswer(
            "Der Pilot beginnt am Montag.",
            [new ConversationAnswerSource(
                conversation.TranscriptSegments[0].Id,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(5),
                "Sana",
                "Der Pilot beginnt am Montag.")],
            IsAnswerable: true,
            IsBestEffort: false);

        await histories.SaveAsync(new ConversationQuestionHistory(
            conversation.Id,
            "stable-transcript-fingerprint",
            [
                new ConversationQuestionExchange(
                    answeredId,
                    "Wann beginnt der Pilot?",
                    answer),
                new ConversationQuestionExchange(
                    unansweredId,
                    "Wer nimmt teil?",
                    null)
            ]));

        ConversationQuestionHistory? restored =
            await histories.GetByConversationIdAsync(conversation.Id);

        Assert.IsNotNull(restored);
        Assert.AreEqual("stable-transcript-fingerprint", restored.TranscriptFingerprint);
        Assert.AreEqual(2, restored.Exchanges.Count);
        Assert.AreEqual(answeredId, restored.Exchanges[0].Id);
        Assert.AreEqual("Wann beginnt der Pilot?", restored.Exchanges[0].Question);
        GroundedConversationAnswer? restoredAnswer =
            restored.Exchanges[0].Answer;
        Assert.IsNotNull(restoredAnswer);
        Assert.AreEqual(answer.Text, restoredAnswer.Text);
        Assert.AreEqual(answer.IsAnswerable, restoredAnswer.IsAnswerable);
        Assert.AreEqual(answer.IsBestEffort, restoredAnswer.IsBestEffort);
        Assert.AreEqual(1, restoredAnswer.Sources.Count);
        Assert.AreEqual(
            answer.Sources[0],
            restoredAnswer.Sources[0]);
        Assert.AreEqual(unansweredId, restored.Exchanges[1].Id);
        Assert.IsNull(restored.Exchanges[1].Answer);
    }

    [TestMethod]
    public async Task DeletingConversation_CascadesToChatHistory()
    {
        using var context = new PersistenceTestContext();
        Conversation conversation = await CreateConversationAsync(context);
        IConversationQuestionHistoryRepository histories =
            context.GetRequiredService<IConversationQuestionHistoryRepository>();
        await histories.SaveAsync(new ConversationQuestionHistory(
            conversation.Id,
            "fingerprint",
            [new ConversationQuestionExchange(
                Guid.NewGuid(),
                "Was wurde besprochen?",
                GroundedConversationAnswer.Insufficient())]));

        bool deleted = await context
            .GetRequiredService<IConversationRepository>()
            .DeleteAsync(conversation.Id);

        Assert.IsTrue(deleted);
        Assert.IsNull(await histories.GetByConversationIdAsync(conversation.Id));
    }

    [TestMethod]
    public async Task SavingEmptyHistory_RemovesExistingChat()
    {
        using var context = new PersistenceTestContext();
        Conversation conversation = await CreateConversationAsync(context);
        IConversationQuestionHistoryRepository histories =
            context.GetRequiredService<IConversationQuestionHistoryRepository>();
        await histories.SaveAsync(new ConversationQuestionHistory(
            conversation.Id,
            "fingerprint",
            [new ConversationQuestionExchange(
                Guid.NewGuid(),
                "Was wurde besprochen?",
                GroundedConversationAnswer.Insufficient())]));

        await histories.SaveAsync(new ConversationQuestionHistory(
            conversation.Id,
            "fingerprint",
            []));

        Assert.IsNull(await histories.GetByConversationIdAsync(conversation.Id));
    }

    private static async Task<Conversation> CreateConversationAsync(
        PersistenceTestContext context)
    {
        var conversation = new Conversation(
            Guid.NewGuid(),
            "Projektmeeting",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            "Der Pilot beginnt am Montag."));
        await context
            .GetRequiredService<IConversationRepository>()
            .UpsertAsync(conversation);
        return conversation;
    }
}
