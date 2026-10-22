using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Infrastructure.Intelligence;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ContextualSummaryPipelineTests
{
    private const string NestedRecording = "[00:00] Erzähler: Heute spiele ich Parasocial. "
        + "Night Security fand ich vorher sehr gut.\n"
        + "[10:00] Figur im Spiel: Ich streame jetzt Acamanto. 751 Leute schauen zu. "
        + "Morgen mache ich weiter.\n"
        + "[12:00] Erzähler: Wir spielen eine Streamerin, die verfolgt wird.\n"
        + "[02:02:00] Erzähler: Parasocial ist unfertig. Fehlende Texturen und schlechte Jumpscares. "
        + "Erst dachte ich drei Punkte; mein endgültiges Urteil ist vier von zehn.";

    [TestMethod]
    public async Task ShortRecording_SendsCompleteOriginalToDraftAndEditor()
    {
        var fake = new RecordingModel((_, _) => Document());
        await new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, default);
        Assert.AreEqual(2, fake.Calls.Count);
        foreach (var call in fake.Calls)
        {
            StringAssert.Contains(call.User, NestedRecording);
            StringAssert.Contains(call.User, "OUTPUT LANGUAGE: de");
            StringAssert.Contains(call.System, "A nested title is not the main title");
        }
    }

    [TestMethod]
    public async Task EditorReceivesWrongDraftAndOriginalRelationshipAndItsRevisionIsUsed()
    {
        var fake = new RecordingModel((_, prompt) => prompt.StartsWith("EDITOR")
            ? Document("Parasocial: Enttäuschung über unfertigen Horror", "Der Erzähler vergibt vier von zehn Punkten.")
            : Document("Acamanto-Livestream", "Es gab 751 Zuschauer."));
        var result = await new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, default);
        Assert.AreEqual("Parasocial: Enttäuschung über unfertigen Horror", result.Title);
        StringAssert.Contains(result.Overview!, "vier von zehn");
        StringAssert.Contains(fake.Calls[1].User, "Acamanto-Livestream");
        StringAssert.Contains(fake.Calls[1].User, "DRAFT TO CHECK (not evidence)");
        StringAssert.Contains(fake.Calls[1].User, "corrected document");
    }

    [TestMethod]
    public async Task LongRecording_CoversEverySectionAndConsolidatesAllNotes()
    {
        string source = string.Join("\n", Enumerable.Range(0, 300)
            .Select(i => $"MARK{i:000}: {new string('a', 180)} Ein Gesprächsabschnitt.\n"));
        int mapCount = 0;
        var fake = new RecordingModel((_, prompt) =>
        {
            if (prompt.StartsWith("NOTES") && prompt.Contains("CONDENSE CHRONOLOGICAL NOTES"))
                return string.Join(" ", Regex.Matches(prompt, @"NOTE_\d+")
                    .Select(x => x.Value).Distinct());
            if (prompt.StartsWith("NOTES")) return $"NOTE_{++mapCount} " + new string('x', 650);
            return Document();
        });
        await new ContextualSummaryPipeline(fake).GenerateAsync([source], "de", null, default);
        var mapCalls = fake.Calls.Where(x => x.User.StartsWith("NOTES")
            && !x.User.Contains("CONDENSE CHRONOLOGICAL NOTES")).ToArray();
        string allMapInputs = string.Join("", mapCalls.Select(x => x.User));
        for (int i = 0; i < 300; i++) StringAssert.Contains(allMapInputs, $"MARK{i:000}");
        Assert.IsTrue(fake.Calls.Any(x => x.User.Contains("CONDENSE CHRONOLOGICAL NOTES")));
        string finalDraft = fake.Calls.Single(x => x.User.StartsWith("DRAFT")).User;
        for (int i = 1; i <= mapCount; i++) StringAssert.Contains(finalDraft, $"NOTE_{i}");
        Assert.IsTrue(fake.Calls.All(x => Encoding.UTF8.GetByteCount(x.System + x.User)
            <= ContextualSummaryPipeline.MaximumInputBytes));
    }

    [TestMethod]
    public async Task OversizedButUsefulNotes_ArePreservedWithoutRestartingFromRawSource()
    {
        var fake = new RecordingModel((_, prompt) => prompt.StartsWith("NOTES")
            ? "Useful complete section note. " + new string('n', 1500)
            : Document());
        await new ContextualSummaryPipeline(fake).GenerateAsync([new string('a', 5500)], "en", null, default);
        Assert.AreEqual(2, fake.Calls.Count(x => x.User.StartsWith("NOTES")));
        StringAssert.Contains(fake.Calls.Single(x => x.User.StartsWith("DRAFT")).User,
            "Useful complete section note.");
    }

    [TestMethod]
    public async Task CorrectedOutcomeRemainsInOriginalContextEvenDuringLongRecording()
    {
        string source = "OPENING: The current work is OuterStory.\n"
            + string.Concat(Enumerable.Repeat("The character plays InnerStory in a fictional stream.\n", 200))
            + "FINAL: Correction: the final score is 4 out of 10, not 3.";
        var fake = new RecordingModel((_, prompt) => prompt.StartsWith("NOTES")
            ? "A review of OuterStory. InnerStory is fictional content inside it. Final score 4 out of 10."
            : Document());
        await new ContextualSummaryPipeline(fake).GenerateAsync([source], "en", null, default);
        foreach (var call in fake.Calls)
        {
            StringAssert.Contains(call.User, "The current work is OuterStory");
            StringAssert.Contains(call.User, "final score is 4 out of 10, not 3");
        }
    }

    [TestMethod]
    public async Task BrokenEditorFormat_PreservesModelDraftInsteadOfSourceSentenceFallback()
    {
        var fake = new RecordingModel((_, prompt) => prompt.StartsWith("EDITOR") ? "broken" : Document());
        var result = await new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, default);
        Assert.AreEqual("Parasocial: Kritik am Horrorspiel", result.Title);
        Assert.AreEqual(3, fake.Calls.Count);
    }

    [TestMethod]
    public async Task BrokenDraftFormat_IsRetriedBeforeEditing()
    {
        int calls = 0;
        var fake = new RecordingModel((_, _) => ++calls == 1 ? "{" : Document());
        var result = await new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, default);
        Assert.IsNotNull(result);
        Assert.AreEqual(3, fake.Calls.Count);
        Assert.IsTrue(fake.Calls[2].User.StartsWith("EDITOR"));
    }

    [TestMethod]
    public async Task UnreadableDraft_DoesNotInventExtractiveSummary()
    {
        var fake = new RecordingModel((_, _) => "<think>unfinished");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, default));
        Assert.AreEqual(2, fake.Calls.Count);
    }

    [TestMethod]
    public async Task CancellationDuringEditor_IsNotSwallowedByDraftFallback()
    {
        using var cancel = new CancellationTokenSource();
        var fake = new RecordingModel((_, prompt) =>
        {
            if (prompt.StartsWith("EDITOR")) { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
            return Document();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de", null, cancel.Token));
    }

    [TestMethod]
    public async Task FirstModelCall_ForwardsDownloadProgress()
    {
        var updates = new List<MeetingIntelligenceProgress>();
        var fake = new RecordingModel((_, _) => Document());
        await new ContextualSummaryPipeline(fake).GenerateAsync([NestedRecording], "de",
            new CaptureProgress(updates), default);
        Assert.IsTrue(updates.Any(x => x.Stage == MeetingIntelligenceStage.DownloadingModel
            && x.DownloadedBytes == 100 && x.BytesPerSecond == 20));
    }

    [TestMethod]
    public void SplitUtf8_PreservesEveryCharacterAndUnicodeWithinBudget()
    {
        string input = string.Concat(Enumerable.Repeat("日本語 😀 Grüße\n", 50));
        var parts = ContextualSummaryPipeline.SplitByUtf8Budget(input, 35);
        Assert.AreEqual(input, string.Concat(parts));
        Assert.IsTrue(parts.All(x => Encoding.UTF8.GetByteCount(x) <= 35));
        Assert.IsFalse(parts.Any(x => x.Contains('\uFFFD')));
    }

    [TestMethod]
    public void DocumentParsing_KeepsGeneratedTitleAndAllowsParaphrase()
    {
        Assert.IsTrue(ContextualSummaryPipeline.TryReadDocument(Document("Unfertiger Horror", "Das Spiel wirkt unfertig."), out var result));
        Assert.AreEqual("Unfertiger Horror", result!.Title);
        Assert.AreEqual("Das Spiel wirkt unfertig.", result.Overview);
    }

    [TestMethod]
    public void DocumentParsing_DoesNotFillEmptySectionsWithIncidentalFacts()
    {
        Assert.IsTrue(ContextualSummaryPipeline.TryReadDocument(Document(points: []), out var result));
        Assert.AreEqual(0, result!.KeyPoints!.Length);
        Assert.AreEqual(0, result.Decisions!.Length);
        Assert.AreEqual(0, result.ActionItems!.Length);
    }

    [TestMethod]
    public void DocumentParsing_RemovesDuplicatePointsAndThinkingInsideFields()
    {
        string raw = "<think>hidden</think>" + Document("<think>private</think>Titel",
            "&lt;think&gt;private&lt;/think&gt;Überblick.", ["Kritik", "kritik"]);
        Assert.IsTrue(ContextualSummaryPipeline.TryReadDocument(raw, out var result));
        Assert.AreEqual("Titel", result!.Title);
        Assert.AreEqual("Überblick.", result.Overview);
        Assert.AreEqual(1, result.KeyPoints!.Length);
    }

    [TestMethod]
    public void IncompleteThinking_IsNeverDisplayedAsAnAnswer()
    {
        Assert.AreEqual("", ContextualSummaryPipeline.CleanModelText("<think>private unfinished reasoning"));
        Assert.AreEqual("Antwort", ContextualSummaryPipeline.CleanModelText("private</think>Antwort"));
    }

    [TestMethod]
    public void InvalidFieldTypes_AreRejectedAsFormatErrors()
    {
        Assert.IsFalse(ContextualSummaryPipeline.TryReadDocument("{\"title\":42,\"overview\":\"Text\"}", out _));
    }

    internal static string Document(string title = "Parasocial: Kritik am Horrorspiel",
        string overview = "Ein Let’s Play behandelt Parasocial und kritisiert technische Mängel.",
        string[]? points = null) => JsonSerializer.Serialize(new
        {
            title, overview, keyPoints = points ?? ["Fehlende Texturen und schwache Jumpscares."],
            decisions = Array.Empty<string>(), actionItems = Array.Empty<object>()
        });

    internal sealed class RecordingModel(Func<string, string, string> respond) : ILocalLanguageModelService
    {
        public bool IsModelInstalled => true;
        public string ModelDisplayName => "Test model";
        public long ModelDownloadSizeBytes => 0;
        public List<(string System, string User)> Calls { get; } = [];
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, int maximumOutputTokens,
            IProgress<LocalLanguageModelProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((systemPrompt, userPrompt));
            progress?.Report(new(LocalLanguageModelStage.DownloadingModel, 0.5, 100, 200, 20));
            return Task.FromResult(respond(systemPrompt, userPrompt));
        }
    }

    private sealed class CaptureProgress(List<MeetingIntelligenceProgress> values)
        : IProgress<MeetingIntelligenceProgress>
    {
        public void Report(MeetingIntelligenceProgress value) => values.Add(value);
    }
}
