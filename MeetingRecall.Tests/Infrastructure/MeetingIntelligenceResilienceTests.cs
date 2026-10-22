using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class MeetingIntelligenceResilienceTests
{
    [TestMethod]
    public async Task GenerateSummaryAsync_SummarizesUnansweredQuestionWithoutInventingDecision()
    {
        const string summary = """
            {
              "title":"Offene Terminfrage für Freitag",
              "overview":"Ob Sarah am Freitag kommt, bleibt offen.",
              "keyPoints":[],
              "decisions":[],
              "actionItems":[]
            }
            """;
        var languageModel = new FakeLanguageModelService([summary, summary]);

        using var context = new PersistenceTestContext(services =>
        {
            services.RemoveAll<ILocalLanguageModelService>();
            services.AddSingleton<ILocalLanguageModelService>(languageModel);
        });
        IConversationRepository conversations =
            context.GetRequiredService<IConversationRepository>();
        ILocalMeetingIntelligenceService intelligence =
            context.GetRequiredService<ILocalMeetingIntelligenceService>();

        var conversation = new Conversation(
            Guid.NewGuid(),
            "Terminfrage",
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.SetDetectedLanguage("de");
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            "Kommt Sarah am Freitag?"));
        await conversations.UpsertAsync(conversation);

        MeetingSummary generated = await intelligence.GenerateSummaryAsync(conversation.Id);

        Assert.IsFalse(string.IsNullOrWhiteSpace(generated.SuggestedTitle));
        Assert.IsFalse(string.IsNullOrWhiteSpace(generated.Overview));
        StringAssert.Contains(generated.SuggestedTitle, "Freitag");
        StringAssert.Contains(generated.Overview, "offen");
        Assert.AreEqual(0, generated.Decisions.Count);
        Assert.AreEqual(0, generated.ActionItems.Count);
    }

    private sealed class FakeLanguageModelService(IEnumerable<string> responses)
        : ILocalLanguageModelService
    {
        private readonly Queue<string> _responses = new(responses);

        public bool IsModelInstalled => true;
        public string ModelDisplayName => "Testmodell";
        public long ModelDownloadSizeBytes => 0;

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            int maximumOutputTokens,
            IProgress<LocalLanguageModelProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
