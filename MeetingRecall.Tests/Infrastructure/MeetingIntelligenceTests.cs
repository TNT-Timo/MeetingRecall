using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class MeetingIntelligenceTests
{
    [TestMethod]
    public async Task GenerateAnotherTitleAsync_ChangesOnlyTheSuggestion()
    {
        var languageModel = new FakeLanguageModelService([
            """
            {"titles":["Neue Perspektiven auf den Pilotstart","Migration im September","Testdaten und Stabilität"]}
            """
        ]);
        using var context = new PersistenceTestContext(services =>
        {
            services.RemoveAll<ILocalLanguageModelService>();
            services.AddSingleton<ILocalLanguageModelService>(languageModel);
        });
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();
        IMeetingSummaryRepository summaries =
            context.GetRequiredService<IMeetingSummaryRepository>();
        ILocalMeetingIntelligenceService intelligence =
            context.GetRequiredService<ILocalMeetingIntelligenceService>();

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Besprechung",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        await conversations.UpsertAsync(conversation);

        MeetingSummary original = MeetingSummary.Create(
            conversation.Id,
            "Pilotstart und Migration",
            "Das Team bespricht den Pilotstart und die Migration.",
            ["Der Pilot beginnt im September."],
            [],
            [],
            conversation.UpdatedAtUtc);
        await summaries.UpsertAsync(original);

        MeetingSummary updated = await intelligence
            .GenerateAnotherTitleAsync(conversation.Id);
        MeetingSummary? loaded = await summaries
            .GetByConversationIdAsync(conversation.Id);

        Assert.AreEqual(
            "Neue Perspektiven auf den Pilotstart",
            updated.SuggestedTitle);
        Assert.AreEqual(original.Overview, updated.Overview);
        Assert.AreEqual(original.GeneratedAtUtc, updated.GeneratedAtUtc);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(updated.SuggestedTitle, loaded.SuggestedTitle);
        StringAssert.Contains(
            languageModel.Prompts.Single(),
            "same language as the overview");
    }

    [TestMethod]
    public async Task SummaryRepository_ConversationDeleteCascadesSummary()
    {
        using var context = new PersistenceTestContext();
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();
        IMeetingSummaryRepository summaries =
            context.GetRequiredService<IMeetingSummaryRepository>();

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Kundentermin",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        await conversations.UpsertAsync(conversation);

        MeetingSummary summary = MeetingSummary.Create(
            conversation.Id,
            "Kunde · Angebot",
            "Das Angebot wurde besprochen.",
            ["Der Kunde prüft das Angebot."],
            [],
            [],
            conversation.UpdatedAtUtc);
        await summaries.UpsertAsync(summary);

        await conversations.DeleteAsync(conversation.Id);

        Assert.IsNull(
            await summaries.GetByConversationIdAsync(conversation.Id));
    }

    private sealed class FakeLanguageModelService(IEnumerable<string> responses)
        : ILocalLanguageModelService
    {
        private readonly Queue<string> _responses = new(responses);

        public bool IsModelInstalled => true;

        public string ModelDisplayName => "Testmodell";

        public long ModelDownloadSizeBytes => 0;

        public List<string> Prompts { get; } = [];

        public List<int> MaximumOutputTokens { get; } = [];

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            int maximumOutputTokens,
            IProgress<LocalLanguageModelProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompts.Add(userPrompt);
            MaximumOutputTokens.Add(maximumOutputTokens);
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.Generating));
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
