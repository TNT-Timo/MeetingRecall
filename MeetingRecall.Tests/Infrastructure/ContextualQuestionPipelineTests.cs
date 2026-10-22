using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRecall.Tests.Infrastructure;

// Scripted models test orchestration and safety contracts, not real Qwen language quality.
[TestClass]
public sealed class ContextualQuestionPipelineTests
{
    private const string Missing = """{"status":"insufficient","claims":[]}""";
    private static string Answer(string text, string status = "answered", params string[] ids) =>
        JsonSerializer.Serialize(new { status, claims = new[] { new { text, sourceIds = ids.Length == 0 ? new[] { "S0001" } : ids } } });

    [TestMethod]
    public async Task ShortTranscriptUsesAllOriginalsAndEditorialAnswerWithExactSources()
    {
        var model = Script(Answer("Montag."), Answer("Der Pilot beginnt am Montag.", "answered", "S0002"));
        using var context = Context(model);
        var conversation = await Save(context, "Das Budget ist noch offen.", "Der Pilot beginnt am Montag.");
        var answer = await Service(context).AnswerAsync(conversation.Id, "Wann beginnt der Pilot?");
        Assert.IsTrue(answer.IsAnswerable);
        Assert.IsFalse(answer.IsBestEffort);
        Assert.AreEqual("Der Pilot beginnt am Montag.", answer.Text);
        Assert.AreEqual(conversation.TranscriptSegments[1].Id, answer.Sources.Single().SegmentId);
        Assert.AreEqual(TimeSpan.FromSeconds(5), answer.Sources[0].StartTime);
        Assert.AreEqual("Der Pilot beginnt am Montag.", answer.Sources[0].Text);
        Assert.AreEqual(2, model.Calls.Count);
        foreach (var call in model.Calls)
        {
            StringAssert.Contains(call.Prompt, "Das Budget ist noch offen.");
            StringAssert.Contains(call.Prompt, "Der Pilot beginnt am Montag.");
            StringAssert.Contains(call.Prompt, "complete transcript");
        }
        StringAssert.Contains(model.Calls[1].Prompt, "EDITOR");
        StringAssert.Contains(model.Calls[1].Prompt, "COMPLETE CORRECTED ANSWER");
    }

    [TestMethod]
    public async Task EditorCanRepairRefusalInsteadOfRequiringExactWordOverlap()
    {
        var model = Script(Missing, Answer("Ali Baba moderiert das Quiz.", "answered", "S0001", "S0002"));
        using var context = Context(model);
        var conversation = await Save(context, "Mein Name ist Ali Baba.", "Ich erkläre die Regeln und stelle euch zehn Fragen.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wer ist Alibaba hier?");
        Assert.AreEqual("Ali Baba moderiert das Quiz.", result.Text);
        Assert.AreEqual(2, result.Sources.Count);
        Assert.IsFalse(result.IsBestEffort);
    }

    [TestMethod]
    public async Task EditorCorrectsFinalScoreAndRetainsChronologicalContext()
    {
        var model = Script(Answer("Er gibt eine 3."), Answer("Sein abschließendes Urteil ist 4/10.", "answered", "S0003"));
        using var context = Context(model);
        var conversation = await Save(context, "Ich gebe 4/10.", "Nein, eine 3.", "Eine 3 ist zu hart, eine 4.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie bewertet er es am Ende?");
        Assert.AreEqual("Sein abschließendes Urteil ist 4/10.", result.Text);
        StringAssert.Contains(model.Calls[1].Prompt, "Eine 3 ist zu hart, eine 4.");
        StringAssert.Contains(model.Calls[1].System, "final correction");
    }

    [TestMethod]
    public async Task EditorDistinguishesQuizAnswerFromModeratorsCorrection()
    {
        var model = Script(Answer("Sie nennen Angela Merkel korrekt."),
            Answer("Die Antwort war falsch: Sie übernahmen Angelika Merkel; der Moderator korrigierte zu Angela Merkel.", "answered", "S0001", "S0002"));
        using var context = Context(model);
        var conversation = await Save(context, "Angelika Merkel. Nehmt ihr die Antwort? Ja.", "Das ist falsch, sie heißt Angela Merkel.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Hatten sie die Kanzlerin richtig?");
        StringAssert.Contains(result.Text, "war falsch");
        StringAssert.Contains(model.Calls[1].System, "contestant's original answer");
    }

    [TestMethod]
    public async Task FollowUpHistoryReachesEditorButRemainsSeparateFromEvidence()
    {
        var model = Script(Answer("Er ist unzufrieden."), Answer("Ja, er kritisiert fehlende Texturen.", "answered", "S0002"));
        using var context = Context(model);
        var conversation = await Save(context, "Heute spiele ich Parasocial.", "Texturen fehlen. Das wirkt unfertig.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Warum denn?",
            [new("Wie findet er das Spiel?", "Er ist unzufrieden, angeblich wegen Einhörnern.")]);
        Assert.IsFalse(result.Text.Contains("Einhörnern"));
        foreach (var call in model.Calls)
        {
            StringAssert.Contains(call.Prompt, "HISTORY (not evidence)");
            StringAssert.Contains(call.Prompt, "Warum denn?");
            StringAssert.Contains(call.Prompt, "Wie findet er das Spiel?");
            Assert.IsFalse(call.Prompt[(call.Prompt.IndexOf("ORIGINAL SOURCES", StringComparison.Ordinal))..]
                .Split("EDITOR")[0].Contains("Einhörnern"));
        }
    }

    [TestMethod]
    public async Task NestedGameAndPreviousGameStayVisibleToEditor()
    {
        var model = Script(Answer("Er spielt Acamanto und liebt es."),
            Answer("Er spielt Parasocial und kritisiert es. Acamanto ist nur ein Spiel innerhalb der Handlung.", "answered", "S0001", "S0002", "S0003"));
        using var context = Context(model);
        var conversation = await Save(context, "Heute Parasocial. Night Security davor war super.",
            "Die Streamerin im Spiel spielt Acamanto.", "Parasocial ist unfertig und enttäuschend.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Welches Spiel bewertet er negativ?");
        StringAssert.Contains(result.Text, "Parasocial");
        StringAssert.Contains(model.Calls[1].System, "previous game is not praise of this one");
        Assert.AreEqual(3, result.Sources.Count);
    }

    [TestMethod]
    public async Task ArithmeticAndNegativeParaphrasesAreNotBlockedByLiteralHeuristics()
    {
        var model = Script(Answer("2 waren falsch."), Answer("2 Fragen wurden nicht richtig beantwortet.", "answered", "S0001", "S0002"));
        using var context = Context(model);
        var conversation = await Save(context, "Zehn Fragen insgesamt.", "Acht Antworten richtig.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie viele waren falsch?");
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsFalse(result.IsBestEffort);
        StringAssert.Contains(result.Text, "2 Fragen");
    }

    [TestMethod]
    public async Task ExplicitEditorRejectionDoesNotLeakContradictedDraftAsOrange()
    {
        var model = Script(Answer("Das Budget ist 999 Euro."), Missing);
        using var context = Context(model);
        var conversation = await Save(context, "Das Budget ist noch offen.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie hoch ist das Budget?");
        Assert.IsFalse(result.IsAnswerable);
        Assert.AreEqual("", result.Text);
    }

    [TestMethod]
    public async Task MalformedEditorIsRetriedThenDraftIsOrangeNeverGreen()
    {
        var model = Script(Answer("Der Start ist Montag."), "not json", "still not json");
        using var context = Context(model);
        var conversation = await Save(context, "Der Start ist Montag.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wann starten wir?");
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsTrue(result.IsBestEffort);
        Assert.AreEqual(3, model.Calls.Count);
        StringAssert.Contains(model.Calls[2].Prompt, "FORMAT REPAIR");
    }

    [TestMethod]
    public async Task EditorFormatRepairCanReturnGreenCorrectedAnswer()
    {
        var model = Script(Missing, "oops", Answer("Der Start ist Montag."));
        using var context = Context(model);
        var conversation = await Save(context, "Der Start ist Montag.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wann starten wir?");
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsFalse(result.IsBestEffort);
    }

    [TestMethod]
    public async Task ForcedAnswerStillGetsEditedAndIsAlwaysOrange()
    {
        var model = Script(Answer("Das Setup ist gut."), Answer("Das Setup gefällt ihm; die Umsetzung kritisiert er."));
        using var context = Context(model);
        var conversation = await Save(context, "Das Setup ist gut, aber unfertig umgesetzt.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie findet er es?", forceAnswer: true);
        Assert.IsTrue(result.IsBestEffort);
        StringAssert.Contains(result.Text, "Umsetzung");
        Assert.AreEqual(2, model.Calls.Count);
        StringAssert.Contains(model.Calls[0].Prompt, "BEST EFFORT REQUESTED");
    }

    [TestMethod]
    public async Task QualifiedInferenceIsOrangeWithoutExtraClick()
    {
        var model = Script(Answer("Er scheint enttäuscht.", "uncertain"), Answer("Er wirkt enttäuscht.", "uncertain"));
        using var context = Context(model);
        var conversation = await Save(context, "Ich hatte mehr erwartet.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie geht es ihm damit?");
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsTrue(result.IsBestEffort);
    }

    [TestMethod]
    public async Task UnrelatedQuestionRemainsUnansweredEvenWhenForced()
    {
        var model = Script(Missing, Missing);
        using var context = Context(model);
        var conversation = await Save(context, "Wir besprechen das Projekt.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wie lautet seine geheime PIN?", forceAnswer: true);
        Assert.IsFalse(result.IsAnswerable);
        Assert.AreEqual(0, result.Sources.Count);
    }

    [TestMethod]
    public async Task UnknownSourceIdsCannotProduceAnswerEvenWithRepeatedInvalidEditor()
    {
        string invalid = Answer("Er heißt Max.", "answered", "S9999");
        var model = Script(invalid, invalid, invalid);
        using var context = Context(model);
        var conversation = await Save(context, "Hallo zusammen.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wer ist er?");
        Assert.IsFalse(result.IsAnswerable);
    }

    [TestMethod]
    public async Task ThinkingBlocksAreRemovedInsideStructuredAnswers()
    {
        var model = Script(Answer("<think>secret</think>Der Start ist Montag."),
            "<think>internal</think>" + Answer("<think>more secrets</think>Der Start ist Montag."));
        using var context = Context(model);
        var conversation = await Save(context, "Der Start ist Montag.");
        var result = await Service(context).AnswerAsync(conversation.Id, "Wann?");
        Assert.AreEqual("Der Start ist Montag.", result.Text);
        Assert.IsFalse(result.Text.Contains("secret"));
    }

    [TestMethod]
    public async Task EmptyTranscriptDoesNotInvokeModelAndReportsCompletion()
    {
        var model = Script();
        using var context = Context(model);
        var conversation = await Save(context);
        var stages = new List<ConversationQuestionStage>();
        var result = await Service(context).AnswerAsync(conversation.Id, "Worum geht es?",
            progress: new CaptureProgress(p => stages.Add(p.Stage)));
        Assert.IsFalse(result.IsAnswerable);
        Assert.AreEqual(0, model.Calls.Count);
        Assert.AreEqual(ConversationQuestionStage.Completed, stages.Last());
    }

    [TestMethod]
    public async Task LongTranscriptIndexesEverySectionCachesOnlyNotesAndInvalidatesOnEdit()
    {
        var model = Adaptive();
        var pipeline = new ContextualQuestionPipeline(model);
        var conversation = LongConversation();
        var answer = await pipeline.AnswerAsync(conversation, new Dictionary<Guid, string>(), "Was wurde entschieden?", [], null, default, false);
        Assert.IsTrue(answer.IsAnswerable);
        var indexCalls = model.Calls.Where(c => c.Prompt.Contains("SECTION ORIGINALS:")).ToArray();
        Assert.IsTrue(indexCalls.Length > 1);
        foreach (var segment in conversation.TranscriptSegments)
            Assert.IsTrue(indexCalls.Any(c => c.Prompt.Contains(segment.CurrentText)), segment.CurrentText);
        int firstIndexCount = indexCalls.Length;
        int firstDraftCount = model.Calls.Count(c => c.Prompt.Contains("DRAFT the most useful"));
        await pipeline.AnswerAsync(conversation, new Dictionary<Guid, string>(), "Warum?", [], null, default, false);
        Assert.AreEqual(firstIndexCount, model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:")));
        Assert.AreEqual(firstDraftCount + 1, model.Calls.Count(c => c.Prompt.Contains("DRAFT the most useful")));
        conversation.TranscriptSegments[^1].EditText("KORREKTUR: Es beginnt Dienstag.", DateTimeOffset.UtcNow);
        await pipeline.AnswerAsync(conversation, new Dictionary<Guid, string>(), "Wann?", [], null, default, false);
        Assert.IsTrue(model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:")) > firstIndexCount);
        Assert.IsTrue(model.Calls.Any(c => c.Prompt.Contains("KORREKTUR: Es beginnt Dienstag.")));
    }

    [TestMethod]
    public async Task LongTranscriptSearchesOriginalsBeforeFinalRefusal()
    {
        bool scanned = false;
        var model = Adaptive(call =>
        {
            var match = Regex.Match(call.Prompt, @"SECTION (\d+) ORIGINALS:");
            if (match.Success) { scanned = true; return "{\"sections\":[" + match.Groups[1].Value + "]}"; }
            if (call.System.Contains("CONTEXTUAL CONVERSATION"))
                return scanned ? Answer("Er nennt den Starttermin.") : Missing;
            return null;
        });
        var pipeline = new ContextualQuestionPipeline(model);
        var result = await pipeline.AnswerAsync(LongConversation(), new Dictionary<Guid, string>(), "Wann?", [], null, default, false);
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsTrue(scanned);
        int sections = model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:"));
        Assert.AreEqual(sections, model.Calls.Count(c => Regex.IsMatch(c.Prompt, @"SECTION \d+ ORIGINALS:")));
    }

    [TestMethod]
    public async Task IndexCacheIsIsolatedByConversationId()
    {
        var model = Adaptive();
        var pipeline = new ContextualQuestionPipeline(model);
        await pipeline.AnswerAsync(LongConversation(), new Dictionary<Guid, string>(), "Wann?", [], null, default, false);
        int first = model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:"));
        await pipeline.AnswerAsync(LongConversation(), new Dictionary<Guid, string>(), "Wann?", [], null, default, false);
        Assert.AreEqual(first * 2, model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:")));
    }

    [TestMethod]
    public async Task CancellationDuringIndexDoesNotCachePartialNotesOrLeaveGateLocked()
    {
        using var cancellation = new CancellationTokenSource();
        bool cancel = true;
        var model = Adaptive(call =>
        {
            if (cancel && call.Prompt.Contains("SECTION ORIGINALS:"))
            { cancel = false; cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return null;
        });
        var pipeline = new ContextualQuestionPipeline(model);
        var conversation = LongConversation();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => pipeline.AnswerAsync(
            conversation, new Dictionary<Guid, string>(), "Wann?", [], null, cancellation.Token, false));
        var result = await pipeline.AnswerAsync(conversation, new Dictionary<Guid, string>(), "Wann?", [], null, default, false);
        Assert.IsTrue(result.IsAnswerable);
        Assert.IsTrue(model.Calls.Count(c => c.Prompt.Contains("SECTION ORIGINALS:")) > 2);
    }

    [TestMethod]
    public async Task MultilingualLongInputsAndHugeHistoryStayWithinEveryCallBudget()
    {
        var model = Adaptive();
        var conversation = Create(Enumerable.Range(0, 15).Select(i =>
            $"Abschnitt {i}: " + string.Concat(Enumerable.Repeat("你好🙂 Привет Grüße ", 70))).ToArray());
        var pipeline = new ContextualQuestionPipeline(model);
        await pipeline.AnswerAsync(conversation, new Dictionary<Guid, string>(), new string('漢', 900),
            Enumerable.Range(0, 4).Select(_ => new ConversationQuestionTurn(new string('漢', 500), new string('語', 900))).ToArray(),
            null, default, false);
        Assert.IsTrue(model.Calls.All(c => Encoding.UTF8.GetByteCount(c.System + c.Prompt) <= ContextualQuestionPipeline.MaximumInputBytes));
    }

    [TestMethod]
    public void SplittingPreservesEntireOversizedSegmentIncludingEmojiAndRepeatedCorrections()
    {
        string input = string.Concat(Enumerable.Repeat("🙂你好 Korrektur: vier. ", 300));
        var conversation = Create(input, "Eine 3 ist zu hart, eine 4.", "Eine 3 ist zu hart, eine 4.");
        var sources = ContextualQuestionPipeline.BuildSources(conversation, new Dictionary<Guid, string>());
        Assert.AreEqual(input, string.Concat(sources.Where(s => s.SegmentId == conversation.TranscriptSegments[0].Id).Select(s => s.Text)));
        Assert.AreEqual(2, sources.Count(s => s.Text == "Eine 3 ist zu hart, eine 4."));
        Assert.IsTrue(sources.All(s => Encoding.UTF8.GetByteCount(s.Text) <= 1_200));
        Assert.IsFalse(sources.Any(s => s.Text.Contains('\uFFFD')));
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"status\":\"answered\",\"claims\":[]}")]
    [DataRow("{\"status\":\"answered\",\"claims\":[null]}")]
    [DataRow("{\"status\":\"answered\",\"claims\":[{\"text\":\"x\",\"sourceIds\":[\"S9999\"]}]}")]
    [DataRow("{\"status\":\"answered\",\"claims\":[{\"text\":\"x\",\"sourceIds\":[null]}]}")]
    [DataRow("{\"status\":\"answered\",\"claims\":[{\"text\":\"<think>unfinished\",\"sourceIds\":[\"S0001\"]}]}")]
    public void InvalidContractsCannotCertifyAnswers(string output)
    {
        var sources = ContextualQuestionPipeline.BuildSources(Create("Hallo."), new Dictionary<Guid, string>());
        Assert.IsNull(ContextualQuestionPipeline.ParseDocument(output, sources));
    }

    [TestMethod]
    public void UnclosedThinkingCannotLeak()
    {
        Assert.AreEqual("", ContextualQuestionPipeline.Clean("<think>private chain"));
        Assert.AreEqual("public", ContextualQuestionPipeline.Clean("hidden</think>public"));
        Assert.AreEqual("public", ContextualQuestionPipeline.Clean("<THINK x='1'>hidden</THINK>public"));
    }

    private static PersistenceTestContext Context(Model model) => new(services =>
    {
        services.RemoveAll<ILocalLanguageModelService>();
        services.AddSingleton<ILocalLanguageModelService>(model);
    });
    private static ILocalConversationQuestionService Service(PersistenceTestContext context) => context.GetRequiredService<ILocalConversationQuestionService>();
    private static async Task<Conversation> Save(PersistenceTestContext context, params string[] texts)
    {
        var conversation = Create(texts);
        foreach (Guid id in conversation.SpeakerIds)
            await context.GetRequiredService<ISpeakerRepository>().UpsertAsync(Speaker.CreateUnknown(id, 1));
        await context.GetRequiredService<IConversationRepository>().UpsertAsync(conversation);
        return conversation;
    }
    private static Conversation Create(params string[] texts)
    {
        var conversation = new Conversation(Guid.NewGuid(), "Recording", DateTimeOffset.UtcNow, ConversationSource.ImportedAudio);
        Guid speaker = Guid.NewGuid();
        for (int i = 0; i < texts.Length; i++)
            conversation.AddTranscriptSegment(new TranscriptSegment(Guid.NewGuid(), speaker,
                TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds(i * 5 + 5), texts[i]));
        return conversation;
    }
    private static Conversation LongConversation() => Create(Enumerable.Range(0, 45).Select(i =>
        $"Abschnitt {i}: " + string.Concat(Enumerable.Repeat("Wir erörtern den Termin und die Gründe. ", 10))).ToArray());
    private static Model Script(params string[] outputs)
    {
        var queue = new Queue<string>(outputs);
        return new(_ => queue.Dequeue());
    }
    private static Model Adaptive(Func<Call, string?>? custom = null) => new(call =>
    {
        string? result = custom?.Invoke(call);
        if (result is not null) return result;
        if (call.System.Contains("NAVIGATION INDEX")) return "Besprechung des Termins mit Gründen und abschließender Korrektur.";
        if (call.System.Contains("SELECT CONTEXT"))
        {
            var ids = Regex.Matches(call.Prompt, "\"section\":(\\d+)").Select(m => int.Parse(m.Groups[1].Value)).Take(4);
            return JsonSerializer.Serialize(new { sections = ids });
        }
        return Answer("Der Sprecher bespricht den Termin.");
    });
    private sealed record Call(string System, string Prompt);
    private sealed class Model(Func<Call, string> respond) : ILocalLanguageModelService
    {
        public bool IsModelInstalled => true;
        public string ModelDisplayName => "Scripted Qwen test double";
        public long ModelDownloadSizeBytes => 0;
        public List<Call> Calls { get; } = [];
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, int maximumOutputTokens,
            IProgress<LocalLanguageModelProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(Encoding.UTF8.GetByteCount(systemPrompt + userPrompt) <= ContextualQuestionPipeline.MaximumInputBytes);
            Assert.IsFalse(systemPrompt.Contains("MEETINGRECALL CONTEXTUAL SUMMARY"));
            var call = new Call(systemPrompt, userPrompt); Calls.Add(call);
            return Task.FromResult(respond(call));
        }
    }
    private sealed class CaptureProgress(Action<ConversationQuestionProgress> action) : IProgress<ConversationQuestionProgress>
    {
        public void Report(ConversationQuestionProgress value) => action(value);
    }
}
