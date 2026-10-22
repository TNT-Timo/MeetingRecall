using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class TranscriptRepetitionFilterTests
{
    [TestMethod]
    public void CollapseAdjacentPhrases_RemovesWhisperRepetitionsInsideSegment()
    {
        const string source =
            "Mir fehlt ein bisschen Polish. Weil es sehr grob designt. " +
            "Weil es sehr grob designt. Ja.";

        string result = TranscriptRepetitionFilter.CollapseAdjacentPhrases(source);

        Assert.AreEqual(
            "Mir fehlt ein bisschen Polish. Weil es sehr grob designt. Ja.",
            result);
    }

    [TestMethod]
    public void CollapseAdjacentPhrases_RemovesTwoSentenceDecoderCycle()
    {
        const string source =
            "Ist das Werbung für das Spiel? Find ich gut. " +
            "Ist das Werbung für das Spiel? Find ich gut. " +
            "Danach folgt neuer Inhalt.";

        string result = TranscriptRepetitionFilter.CollapseAdjacentPhrases(source);

        Assert.AreEqual(
            "Ist das Werbung für das Spiel? Find ich gut. Danach folgt neuer Inhalt.",
            result);
    }

    [TestMethod]
    public void CollapseAdjacentPhrases_RemovesCycleContainingShortSentence()
    {
        const string source =
            "Mit Asuka wahrscheinlich. " +
            "Du hast mich entblockt? Danke. Jetzt werde ich dich töten. " +
            "Du hast mich entblockt? Danke. Jetzt werde ich dich töten.";

        string result = TranscriptRepetitionFilter.CollapseAdjacentPhrases(source);

        Assert.AreEqual(
            "Mit Asuka wahrscheinlich. Du hast mich entblockt? Danke. " +
            "Jetzt werde ich dich töten.",
            result);
    }

    [TestMethod]
    public void CollapseAdjacentPhrases_PreservesShortRepeatedAcknowledgement()
    {
        const string source = "Ja. Ja. Danach geht das Gespräch weiter.";

        string result = TranscriptRepetitionFilter.CollapseAdjacentPhrases(source);

        Assert.AreEqual(source, result);
    }

    [TestMethod]
    public void CollapseAdjacentPhrases_RemovesRepeatedWordsWithoutFinalPunctuation()
    {
        const string source =
            "Hier steht 132 positive, eine negative. " +
            "Steht 132 positive eine negative, ich bin die zweite negative Review.";

        string result = TranscriptRepetitionFilter.CollapseAdjacentPhrases(source);

        Assert.AreEqual(
            "Hier steht 132 positive, eine negative. ich bin die zweite negative Review.",
            result);
    }

    [TestMethod]
    public void TrimLeadingWordOverlap_RemovesOnlyRepeatedSegmentSeam()
    {
        const string previous =
            "Das ist einfach null kritisieren, das ist so ein";
        const string current =
            "Kritisieren, das ist so ein liebloser Mist, den die raushauen.";

        string result = TranscriptRepetitionFilter.TrimLeadingWordOverlap(
            previous,
            current);

        Assert.AreEqual(
            "liebloser Mist, den die raushauen.",
            result);
    }

    [TestMethod]
    public void TrimLeadingWordOverlap_PreservesShortNaturalContinuation()
    {
        const string previous = "Er sagte: Das ist gut.";
        const string current = "Das ist gut gemeint, aber noch nicht fertig.";

        string result = TranscriptRepetitionFilter.TrimLeadingWordOverlap(
            previous,
            current);

        Assert.AreEqual(current, result);
    }

    [TestMethod]
    public void Filter_RemovesRepeatingThreeSegmentDecoderLoop()
    {
        string[] source =
        [
            "Ein normaler Einstieg.",
            "Was soll das heißen?",
            "Das habe ich nicht gesagt.",
            "Wir machen jetzt weiter.",
            "Was soll das heißen?",
            "Das habe ich nicht gesagt.",
            "Wir machen jetzt weiter.",
            "Was soll das heißen?",
            "Das habe ich nicht gesagt.",
            "Wir machen jetzt weiter.",
            "Was soll das heißen?",
            "Das habe ich nicht gesagt.",
            "Wir machen jetzt weiter.",
            "Anschließend folgt echter neuer Inhalt."
        ];

        TranscriptFilterResult<string> result =
            TranscriptRepetitionFilter.Filter(source, text => text);

        CollectionAssert.AreEqual(
            new[]
            {
                "Ein normaler Einstieg.",
                "Was soll das heißen?",
                "Das habe ich nicht gesagt.",
                "Wir machen jetzt weiter.",
                "Anschließend folgt echter neuer Inhalt."
            },
            result.Items.ToArray());
        Assert.AreEqual(9, result.SuppressedCount);
    }

    [TestMethod]
    public void Filter_PreservesOrdinaryNonCyclicConversation()
    {
        string[] source =
        [
            "Das erste Thema ist die Planung.",
            "Danach sprechen wir über den Zeitplan.",
            "Zum Schluss werden die nächsten Schritte verteilt."
        ];

        TranscriptFilterResult<string> result =
            TranscriptRepetitionFilter.Filter(source, text => text);

        CollectionAssert.AreEqual(source, result.Items.ToArray());
        Assert.AreEqual(0, result.SuppressedCount);
    }

    [TestMethod]
    public void Filter_RemovesSparseLongRunningWhisperHallucinationBurst()
    {
        var source = new List<TimedText>
        {
            new(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(51),
                "Bis hierhin enthält das Gespräch echten Inhalt.")
        };
        TimeSpan burstStart = TimeSpan.FromHours(1)
            + TimeSpan.FromMinutes(52);
        for (int cycle = 0; cycle < 8; cycle++)
        {
            TimeSpan start = burstStart + TimeSpan.FromSeconds(cycle * 30);
            source.Add(new TimedText(
                start,
                "Der jüngere Bruder war auch dabei."));
            source.Add(new TimedText(
                start + TimeSpan.FromSeconds(2),
                "Der jüngere Bruder war auch dabei."));
            source.Add(new TimedText(
                start + TimeSpan.FromSeconds(4),
                "Die Aussicht wurde von einer Online-Streamer herausgefunden."));
        }
        source.Add(new TimedText(
            TimeSpan.FromHours(2),
            "Danach folgt wieder ein eigenständiger Satz."));

        TranscriptFilterResult<TimedText> result =
            TranscriptRepetitionFilter.Filter(
                source,
                item => item.Text,
                item => item.Start);

        CollectionAssert.AreEqual(
            new[]
            {
                "Bis hierhin enthält das Gespräch echten Inhalt.",
                "Danach folgt wieder ein eigenständiger Satz."
            },
            result.Items.Select(item => item.Text).ToArray());
        Assert.AreEqual(24, result.SuppressedCount);
    }

    [TestMethod]
    public void Filter_PreservesOccasionalPhraseOutsideBurstWindow()
    {
        TimedText[] source = Enumerable.Range(0, 5)
            .Select(index => new TimedText(
                TimeSpan.FromMinutes(index * 15),
                "Dieser längere Hinweis wird später erneut erwähnt."))
            .ToArray();

        TranscriptFilterResult<TimedText> result =
            TranscriptRepetitionFilter.Filter(
                source,
                item => item.Text,
                item => item.Start);

        CollectionAssert.AreEqual(source, result.Items.ToArray());
        Assert.AreEqual(0, result.SuppressedCount);
    }

    [TestMethod]
    public void Filter_RemovesTightShortUtteranceBurstButKeepsOne()
    {
        TimedText[] source = Enumerable.Range(0, 7)
            .Select(index => new TimedText(
                TimeSpan.FromSeconds(index * 4),
                "Naja"))
            .ToArray();

        TranscriptFilterResult<TimedText> result =
            TranscriptRepetitionFilter.Filter(
                source,
                item => item.Text,
                item => item.Start);

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("Naja", result.Items[0].Text);
        Assert.AreEqual(6, result.SuppressedCount);
    }

    [TestMethod]
    public void Filter_PreservesShortUtterancesOutsideTightWindow()
    {
        TimedText[] source = Enumerable.Range(0, 5)
            .Select(index => new TimedText(
                TimeSpan.FromMinutes(index * 2),
                "Okay"))
            .ToArray();

        TranscriptFilterResult<TimedText> result =
            TranscriptRepetitionFilter.Filter(
                source,
                item => item.Text,
                item => item.Start);

        CollectionAssert.AreEqual(source, result.Items.ToArray());
        Assert.AreEqual(0, result.SuppressedCount);
    }

    private sealed record TimedText(TimeSpan Start, string Text);
}
