using System.Reflection;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ContextualSummaryIntegrationTests
{
    [TestMethod]
    public async Task GenerateSummary_SavesTheEditedDocumentWithoutNumericReorderingOrPadding()
    {
        var model = new ContextualSummaryPipelineTests.RecordingModel((_, prompt) =>
            prompt.StartsWith("EDITOR")
                ? ContextualSummaryPipelineTests.Document("Das eigentliche Thema", "Eine zusammenhängende Übersicht.",
                    ["Qualitative Hauptkritik.", "Abschließende Bewertung: vier von zehn."])
                : ContextualSummaryPipelineTests.Document("Entwurf", "Ungeprüfter Entwurf."));
        using var context = Context(model);
        var conversation = ConversationWithTranscript();
        await context.GetRequiredService<IConversationRepository>().UpsertAsync(conversation);
        var service = context.GetRequiredService<ILocalMeetingIntelligenceService>();
        MeetingSummary result = await service.GenerateSummaryAsync(conversation.Id);
        MeetingSummary? stored = await context.GetRequiredService<IMeetingSummaryRepository>()
            .GetByConversationIdAsync(conversation.Id);
        Assert.IsNotNull(stored);
        Assert.AreEqual("Das eigentliche Thema", result.SuggestedTitle);
        Assert.AreEqual(result.Overview, stored.Overview);
        CollectionAssert.AreEqual(new[] { "Qualitative Hauptkritik.", "Abschließende Bewertung: vier von zehn." }, result.KeyPoints.ToArray());
        Assert.AreEqual(0, result.Decisions.Count);
        Assert.AreEqual(0, result.ActionItems.Count);
        StringAssert.Contains(model.Calls[0].User, "ursprüngliche Aussage");
        StringAssert.Contains(model.Calls[0].User, "letzte Aussage");
    }

    [TestMethod]
    public async Task FailedGeneration_PreservesPreviouslySavedSummary()
    {
        using var context = Context(new ContextualSummaryPipelineTests.RecordingModel((_, _) => "{"));
        Conversation conversation = ConversationWithTranscript();
        await context.GetRequiredService<IConversationRepository>().UpsertAsync(conversation);
        IMeetingSummaryRepository summaries = context.GetRequiredService<IMeetingSummaryRepository>();
        MeetingSummary original = MeetingSummary.Create(conversation.Id, "Vorher", "Vorherige Zusammenfassung.",
            [], [], [], conversation.UpdatedAtUtc);
        await summaries.UpsertAsync(original);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.GetRequiredService<ILocalMeetingIntelligenceService>()
            .GenerateSummaryAsync(conversation.Id));
        MeetingSummary? stored = await summaries.GetByConversationIdAsync(conversation.Id);
        Assert.AreEqual(original.Overview, stored!.Overview);
        Assert.AreEqual(original.GeneratedAtUtc, stored.GeneratedAtUtc);
    }

    [TestMethod]
    public async Task CancelledGeneration_DoesNotSaveDraft()
    {
        using var cancel = new CancellationTokenSource();
        var model = new ContextualSummaryPipelineTests.RecordingModel((_, prompt) =>
        {
            if (prompt.StartsWith("EDITOR")) { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
            return ContextualSummaryPipelineTests.Document();
        });
        using var context = Context(model);
        Conversation conversation = ConversationWithTranscript();
        await context.GetRequiredService<IConversationRepository>().UpsertAsync(conversation);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.GetRequiredService<ILocalMeetingIntelligenceService>()
            .GenerateSummaryAsync(conversation.Id, cancellationToken: cancel.Token));
        Assert.IsNull(await context.GetRequiredService<IMeetingSummaryRepository>().GetByConversationIdAsync(conversation.Id));
    }

    [TestMethod]
    public async Task EmptyTranscript_DoesNotInvokeModel()
    {
        var model = new ContextualSummaryPipelineTests.RecordingModel((_, _) => throw new AssertFailedException("Model must not run."));
        using var context = Context(model);
        var conversation = new Conversation(Guid.NewGuid(), "Leer", DateTimeOffset.UtcNow, ConversationSource.ImportedAudio);
        await context.GetRequiredService<IConversationRepository>().UpsertAsync(conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.GetRequiredService<ILocalMeetingIntelligenceService>()
            .GenerateSummaryAsync(conversation.Id));
        Assert.AreEqual(0, model.Calls.Count);
    }

    [TestMethod]
    public void SummaryWorkerRequest_DisablesThinkingAndSelectsConservativeProfile()
    {
        object request = CreateRequest(ContextualSummaryPipeline.ProfileMarker + "\nSummarize.");
        Assert.AreEqual(true, request.GetType().GetProperty("DisableThinking")!.GetValue(request));
        Assert.AreEqual(true, request.GetType().GetProperty("ConservativeSummarySampling")!.GetValue(request));
        Assert.AreEqual(2048, request.GetType().GetProperty("MaximumOutputTokens")!.GetValue(request));
    }

    [TestMethod]
    public void OtherWorkerRequests_KeepTheirExistingProfile()
    {
        object request = CreateRequest("An unrelated existing request");
        Assert.AreEqual(false, request.GetType().GetProperty("DisableThinking")!.GetValue(request));
        Assert.AreEqual(false, request.GetType().GetProperty("ConservativeSummarySampling")!.GetValue(request));
    }

    private static object CreateRequest(string systemPrompt) => typeof(GpuFirstQwenLanguageModelService)
        .GetMethod("CreateWorkerRequest", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, ["model.gguf", systemPrompt, "source", 2048])!;

    private static PersistenceTestContext Context(ILocalLanguageModelService model) => new(services =>
    {
        services.RemoveAll<ILocalLanguageModelService>();
        services.AddSingleton(model);
    });

    private static Conversation ConversationWithTranscript()
    {
        var conversation = new Conversation(Guid.NewGuid(), "Quelle", DateTimeOffset.UtcNow, ConversationSource.ImportedAudio);
        conversation.SetDetectedLanguage("de");
        Guid speaker = Guid.NewGuid();
        conversation.AddTranscriptSegment(new(Guid.NewGuid(), speaker, TimeSpan.Zero, TimeSpan.FromSeconds(3), "Die ursprüngliche Aussage."));
        conversation.AddTranscriptSegment(new(Guid.NewGuid(), speaker, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "Die letzte Aussage."));
        return conversation;
    }
}
