using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Infrastructure.Intelligence;

internal sealed class GlobalEditorialMeetingIntelligenceService(
    LocalMeetingIntelligenceService inner,
    IConversationRepository conversationRepository,
    ISpeakerRepository speakerRepository,
    IMeetingSummaryRepository summaryRepository,
    ILocalLanguageModelService languageModel,
    SummaryLanguageModelManager modelManager)
    : ILocalMeetingIntelligenceService
{
    private const int MaximumTranscriptCharacters = 80_000;
    private const int EditorialOutputTokens = 1_536;
    private const int MaximumKeyPoints = 6;
    private const int MaximumDecisions = 6;
    private const int MaximumActionItems = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "einem", "einen",
        "und", "oder", "aber", "dass", "ist", "sind", "war", "waren", "wird", "wurde",
        "wurden", "werden", "für", "mit", "von", "über", "auf", "im", "in", "am", "an",
        "zu", "zum", "zur", "bei", "als", "auch", "sich", "es", "er", "sie", "the", "and",
        "that", "this", "was", "were", "with", "from", "about", "for", "has", "have"
    };

    private const string EditorialSystemPrompt = """
        Du bist der abschließende Qualitätsredakteur für MeetingRecall. Das Transkript ist die
        einzige Faktenquelle. Der vorhandene Entwurf kann falsch gewichtet oder kontextuell falsch
        interpretiert sein und ist ausdrücklich KEINE Faktenquelle.

        Lies das gesamte bereitgestellte Transkript vor der Auswahl. Bestimme den Diskursmodus,
        den Hauptgegenstand, die wiederkehrende Kernaktivität und den roten Faden über Anfang,
        Mitte und Ende. Explizite Selbstbeschreibung des Formats und wiederholte Aktivität sind
        wichtiger als einzelne schockierende oder besonders zitierbare Sätze.

        Unterscheide strikt reale Aussagen von Witz, Pointe, Sarkasmus, Storytelling, Rollenspiel,
        Zitat, Übertreibung, Fiktion und Hypothese. Eine Pointe ist kein biografischer Fakt. Bei
        Comedy/Witze darf ein Witz-Setup niemals als Krankheit, Eigenschaft oder sonstige reale
        Tatsache über einen Teilnehmer ausgegeben werden. Wenn ein Witz als Kernpunkt relevant
        ist, muss der ausgegebene Text selbst klar als Witz, Wortspiel, Scherz oder Pointe
        formuliert sein.

        Titel: 3-10 Wörter, globales Thema/Format, kein Rohzitat und keine einzelne Pointe.
        Kurzfassung: 1-3 Sätze über Format/Gegenstand, zentrale Aktivität und breiten Verlauf.
        Kernpunkte: normalerweise 3-6 unterschiedliche, nützliche Punkte ohne semantische
        Duplikate. Entscheidungen und Aufgaben nur bei expliziter Entscheidung/Zuweisung.

        Jede ausgegebene Aussage muss sourceIds tragen, die die komplette Aussage direkt stützen.
        Erfinde keine IDs, Namen, Rollen, Zahlen, Daten oder Kausalität. Nicht-wörtlicher Inhalt
        bekommt kind "performed" und muss auch sprachlich als nicht-wörtlicher Inhalt markiert sein.
        Antworte nach dem internen Denken mit genau einem JSON-Objekt und ohne Markdown.
        """;

    public bool IsModelInstalled => inner.IsModelInstalled;
    public string ModelDisplayName => inner.ModelDisplayName;
    public long ModelDownloadSizeBytes => inner.ModelDownloadSizeBytes;

    public async Task<MeetingSummary> GenerateSummaryAsync(
        Guid conversationId,
        IProgress<MeetingIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        MeetingSummary draft = await inner
            .GenerateSummaryAsync(conversationId, progress, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Conversation? conversation = await conversationRepository
                .GetByIdAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);
            if (conversation is null)
            {
                return draft;
            }

            IReadOnlyList<Speaker> speakers = await speakerRepository
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<TranscriptLine> lines = BuildTranscriptLines(
                conversation,
                speakers,
                cancellationToken);
            if (lines.Count == 0)
            {
                return draft;
            }

            string transcript = RenderTranscript(lines);
            string prompt = BuildPrompt(
                transcript,
                draft,
                ResolveTargetLanguage(conversation),
                retryFeedback: null,
                previousOutput: null);

            progress?.Report(new MeetingIntelligenceProgress(
                MeetingIntelligenceStage.VerifyingSummary,
                0.94));

            string output = await GenerateEditorialAsync(
                prompt,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!TryCreateSummary(
                    conversation,
                    output,
                    lines,
                    out MeetingSummary? polished,
                    out string feedback))
            {
                string retryPrompt = BuildPrompt(
                    transcript,
                    draft,
                    ResolveTargetLanguage(conversation),
                    feedback,
                    output);
                output = await GenerateEditorialAsync(
                    retryPrompt,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (!TryCreateSummary(
                        conversation,
                        output,
                        lines,
                        out polished,
                        out feedback))
                {
                    Debug.WriteLine(
                        "Global editorial pass rejected twice; keeping grounded draft. " + feedback);
                    return draft;
                }
            }

            progress?.Report(new MeetingIntelligenceProgress(
                MeetingIntelligenceStage.Saving,
                0.99));
            await summaryRepository
                .UpsertAsync(polished!, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new MeetingIntelligenceProgress(
                MeetingIntelligenceStage.Completed,
                1));
            return polished!;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Global editorial pass failed; keeping grounded draft. " + exception);
            return draft;
        }
    }

    public Task<MeetingSummary> GenerateAnotherTitleAsync(
        Guid conversationId,
        IProgress<MeetingIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        inner.GenerateAnotherTitleAsync(conversationId, progress, cancellationToken);

    private async Task<string> GenerateEditorialAsync(
        string prompt,
        IProgress<MeetingIntelligenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelProgress = new InlineProgress<LocalLanguageModelProgress>(model =>
        {
            double? fraction = model.Stage == LocalLanguageModelStage.Generating
                ? 0.94 + (0.04 * Math.Clamp(model.Fraction ?? 0, 0, 1))
                : null;
            progress?.Report(new MeetingIntelligenceProgress(
                model.Stage == LocalLanguageModelStage.LoadingModel
                    ? MeetingIntelligenceStage.LoadingModel
                    : model.Stage == LocalLanguageModelStage.DownloadingModel
                        ? MeetingIntelligenceStage.DownloadingModel
                        : MeetingIntelligenceStage.VerifyingSummary,
                fraction,
                model.DownloadedBytes,
                model.TotalBytes,
                model.BytesPerSecond));
        });

        using (modelManager.EnableThinkingForCurrentFlow())
        {
            return await languageModel.GenerateAsync(
                    EditorialSystemPrompt,
                    prompt,
                    EditorialOutputTokens,
                    modelProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string BuildPrompt(
        string transcript,
        MeetingSummary draft,
        string targetLanguage,
        string? retryFeedback,
        string? previousOutput)
    {
        string draftJson = JsonSerializer.Serialize(new
        {
            title = draft.SuggestedTitle,
            overview = draft.Overview,
            keyPoints = draft.KeyPoints,
            decisions = draft.Decisions,
            actionItems = draft.ActionItems.Select(item => new
            {
                description = item.Description,
                owner = item.Owner
            })
        }, JsonOptions);

        string retry = string.IsNullOrWhiteSpace(retryFeedback)
            ? string.Empty
            : $$"""

                DER VORHERIGE GLOBALE ENTWURF WURDE LOKAL ABGELEHNT:
                {{retryFeedback}}

                VORHERIGE AUSGABE (KEINE Faktenquelle):
                {{previousOutput}}

                Behebe diese Fehler zwingend. Liefere danach das vollständige JSON erneut.
                """;

        return $$"""
            AUSGABESPRACHE: {{targetLanguage}}

            JSON-SCHEMA:
            {
              "discourseMode":"z. B. Comedy/Witze, Interview, Quiz, Meeting, Review, Storytelling",
              "title":{"text":"globaler Titel","kind":"orientation","sourceIds":["L000001"]},
              "overview":{"text":"globale Kurzfassung","kind":"orientation","sourceIds":["L000001","L000002"]},
              "keyPoints":[{"text":"nützlicher Kernpunkt","kind":"orientation|literal|performed|outcome","sourceIds":["L000001"]}],
              "decisions":[{"text":"explizite Entscheidung","kind":"decision","sourceIds":["L000001"]}],
              "actionItems":[{"description":"explizite Aufgabe","owner":"Name oder null","sourceIds":["L000001"]}]
            }

            VORHANDENER ENTWURF (KEINE Faktenquelle; nur als Fehlersignal betrachten):
            {{draftJson}}
            {{retry}}

            TRANSKRIPT (EINZIGE Faktenquelle):
            {{transcript}}
            """;
    }

    private static IReadOnlyList<TranscriptLine> BuildTranscriptLines(
        Conversation conversation,
        IReadOnlyList<Speaker> speakers,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, Speaker> speakersById = speakers.ToDictionary(speaker => speaker.Id);
        var filtered = TranscriptRepetitionFilter.Filter(
            conversation.TranscriptSegments,
            segment => segment.CurrentText);
        var lines = new List<TranscriptLine>(filtered.Items.Count);
        for (int index = 0; index < filtered.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptSegment segment = filtered.Items[index];
            string text = Clean(segment.CurrentText, 4_000);
            if (text.Length < 2)
            {
                continue;
            }

            string speaker = speakersById.TryGetValue(segment.SpeakerId, out Speaker? value)
                ? Clean(value.DisplayName, 120)
                : "Unbekannte Person";
            string sourceId = $"L{index + 1:000000}";
            lines.Add(new TranscriptLine(
                index,
                sourceId,
                speaker + ": " + text,
                $"[{sourceId}] [{FormatTime(segment.StartTime)}] {speaker}: {text}"));
        }

        return SelectRepresentativeLines(lines, MaximumTranscriptCharacters);
    }

    private static IReadOnlyList<TranscriptLine> SelectRepresentativeLines(
        IReadOnlyList<TranscriptLine> lines,
        int maximumCharacters)
    {
        if (lines.Sum(line => line.Rendered.Length + 1) <= maximumCharacters)
        {
            return lines;
        }

        int budget = maximumCharacters / 3;
        var selected = new Dictionary<int, TranscriptLine>();
        AddByBudget(lines, selected, budget, fromEnd: false);
        AddByBudget(lines, selected, budget, fromEnd: true);

        int used = 0;
        int middle = lines.Count / 2;
        for (int offset = 0; offset < lines.Count && used < budget; offset++)
        {
            foreach (int index in new[] { middle - offset, middle + offset })
            {
                if (index < 0 || index >= lines.Count || selected.ContainsKey(index))
                {
                    continue;
                }

                int length = lines[index].Rendered.Length + 1;
                if (used + length > budget)
                {
                    continue;
                }

                selected[index] = lines[index];
                used += length;
            }
        }

        return selected.Values.OrderBy(line => line.Sequence).ToArray();
    }

    private static void AddByBudget(
        IReadOnlyList<TranscriptLine> lines,
        IDictionary<int, TranscriptLine> selected,
        int budget,
        bool fromEnd)
    {
        int used = 0;
        IEnumerable<int> indexes = fromEnd
            ? Enumerable.Range(0, lines.Count).Reverse()
            : Enumerable.Range(0, lines.Count);
        foreach (int index in indexes)
        {
            int length = lines[index].Rendered.Length + 1;
            if (used + length > budget)
            {
                break;
            }

            selected[index] = lines[index];
            used += length;
        }
    }

    private static string RenderTranscript(IReadOnlyList<TranscriptLine> lines)
    {
        var builder = new StringBuilder();
        int? previous = null;
        foreach (TranscriptLine line in lines)
        {
            if (previous is int value && line.Sequence > value + 1)
            {
                builder.AppendLine("[... repräsentativ gekürzter Bereich ...]");
            }

            builder.AppendLine(line.Rendered);
            previous = line.Sequence;
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryCreateSummary(
        Conversation conversation,
        string output,
        IReadOnlyList<TranscriptLine> lines,
        out MeetingSummary? summary,
        out string feedback)
    {
        summary = null;
        var errors = new List<string>();
        string? json = ExtractJsonObject(output);
        if (json is null)
        {
            feedback = "Kein vollständiges JSON-Objekt gefunden.";
            return false;
        }

        EditorialDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<EditorialDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            feedback = "Ungültiges JSON: " + exception.Message;
            return false;
        }

        if (document is null)
        {
            feedback = "Leeres Editorial-Dokument.";
            return false;
        }

        Dictionary<string, TranscriptLine> sources = lines.ToDictionary(
            line => line.SourceId,
            StringComparer.Ordinal);
        string transcriptText = string.Join(" ", lines.Select(line => line.SupportText));
        string mode = InferDiscourseMode(Clean(document.DiscourseMode, 120), transcriptText);

        ValidatedText? title = Validate(document.Title, mode, sources, lines, 120, errors);
        ValidatedText? overview = Validate(document.Overview, mode, sources, lines, 4_000, errors);
        if (title is null || !TitleMatchesDiscourseMode(title.Text, mode))
        {
            errors.Add("Titel ist nicht global bzw. passt nicht zum Diskursmodus.");
            title = null;
        }

        if (overview is null || !OverviewMatchesDiscourseMode(overview.Text, mode))
        {
            errors.Add("Kurzfassung bildet Format/Gesamtaktivität nicht ab.");
            overview = null;
        }

        var keyPoints = new List<ValidatedText>();
        foreach (EditorialText item in (document.KeyPoints ?? []).Take(12))
        {
            ValidatedText? validated = Validate(item, mode, sources, lines, 1_000, errors);
            if (validated is null
                || keyPoints.Any(existing => AreSemanticallyDuplicate(existing.Text, validated.Text)))
            {
                continue;
            }

            keyPoints.Add(validated);
            if (keyPoints.Count >= MaximumKeyPoints)
            {
                break;
            }
        }

        if (lines.Count >= 30 && keyPoints.Count < 2)
        {
            errors.Add("Zu wenige belastbare Kernpunkte für ein längeres Transkript.");
        }

        if (IsComedyMode(mode)
            && lines.Count >= 30
            && keyPoints.Count >= 2
            && !HasBroadCoverage(keyPoints, lines))
        {
            errors.Add("Kernpunkte sind bei Comedy/Witze zu stark in einem lokalen Abschnitt gebündelt.");
        }

        var decisions = new List<string>();
        foreach (EditorialText item in (document.Decisions ?? []).Take(MaximumDecisions))
        {
            ValidatedText? validated = Validate(item, mode, sources, lines, 1_000, errors);
            if (validated is not null
                && !decisions.Any(existing => AreSemanticallyDuplicate(existing, validated.Text)))
            {
                decisions.Add(validated.Text);
            }
        }

        var actions = new List<MeetingActionItem>();
        foreach (EditorialAction item in (document.ActionItems ?? []).Take(MaximumActionItems))
        {
            ValidatedText? validated = Validate(
                new EditorialText
                {
                    Text = item.Description,
                    Kind = "action",
                    SourceIds = item.SourceIds
                },
                mode,
                sources,
                lines,
                1_000,
                errors);
            if (validated is null
                || actions.Any(existing => AreSemanticallyDuplicate(existing.Description, validated.Text)))
            {
                continue;
            }

            string owner = Clean(item.Owner, 120);
            string support = string.Join(" ", validated.SourceIds.Select(id => sources[id].SupportText));
            if (owner.Length > 0 && !support.Contains(owner, StringComparison.CurrentCultureIgnoreCase))
            {
                owner = string.Empty;
            }

            actions.Add(new MeetingActionItem(validated.Text, owner.Length == 0 ? null : owner));
        }

        if (title is null || overview is null || (lines.Count >= 30 && keyPoints.Count < 2))
        {
            feedback = string.Join(" ", errors.Distinct());
            return false;
        }

        summary = MeetingSummary.Create(
            conversation.Id,
            title.Text,
            overview.Text,
            keyPoints.Select(item => item.Text),
            decisions,
            actions,
            conversation.UpdatedAtUtc);
        feedback = string.Join(" ", errors.Distinct());
        return true;
    }

    private static ValidatedText? Validate(
        EditorialText? item,
        string mode,
        IReadOnlyDictionary<string, TranscriptLine> sources,
        IReadOnlyList<TranscriptLine> allLines,
        int maximumLength,
        ICollection<string> errors)
    {
        if (item is null)
        {
            return null;
        }

        string text = Clean(item.Text, maximumLength);
        string kind = Clean(item.Kind, 40).ToLowerInvariant();
        string[] sourceIds = (item.SourceIds ?? [])
            .Select(id => Clean(id, 16))
            .Where(sources.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (text.Length < 3 || sourceIds.Length == 0)
        {
            return null;
        }

        string support = string.Join(" ", sourceIds.Select(id => sources[id].SupportText));
        if (!ConcreteTokensSupported(text, support) || !HasLexicalSupport(text, support))
        {
            return null;
        }

        string context = BuildContext(sourceIds, sources, allLines);
        if (IsUnsafeLiteralClaim(mode, kind, text, context))
        {
            errors.Add("Nicht-wörtlicher Inhalt wurde als reale Teilnehmeraussage formuliert.");
            return null;
        }

        return new ValidatedText(text, sourceIds);
    }

    internal static string InferDiscourseMode(string candidateMode, string transcript)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(
                transcript,
                @"\b(?:witze?\s*-?\s*episode|witzepisode|witze?\s*-?\s*video|comedy(?:format|video)?)\b",
                options))
        {
            return "Comedy/Witze";
        }

        int humorHits = Regex.Matches(
            transcript,
            @"\b(?:witz\w*|scherz\w*|pointe\w*|wortspiel\w*|lachen\w*)\b",
            options).Count;
        return humorHits >= 10
            ? "Comedy/Witze"
            : string.IsNullOrWhiteSpace(candidateMode) ? "nicht eindeutig" : candidateMode.Trim();
    }

    internal static bool TitleMatchesDiscourseMode(string title, string discourseMode)
    {
        if (title.Contains('"') || title.Contains('„') || title.Contains('“'))
        {
            return false;
        }

        return !IsComedyMode(discourseMode)
            || Regex.IsMatch(
                title,
                @"\b(?:witz\w*|humor\w*|comedy|scherz\w*|wortspiel\w*)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool OverviewMatchesDiscourseMode(string overview, string discourseMode) =>
        !IsComedyMode(discourseMode)
        || Regex.IsMatch(
            overview,
            @"\b(?:witz\w*|humor\w*|comedy|scherz\w*|pointe\w*|wortspiel\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsUnsafeLiteralClaim(
        string discourseMode,
        string kind,
        string text,
        string sourceContext)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        bool performedKind = kind is "performed" or "nonliteral" or "story";
        bool framedAsPerformed = Regex.IsMatch(
            text,
            @"\b(?:witz\w*|scherz\w*|pointe\w*|wortspiel\w*|erzählt\w*|geschichte\w*|rollenspiel\w*|zitat\w*|hypothet\w*)\b",
            options);
        if (performedKind && !framedAsPerformed)
        {
            return true;
        }

        if (!IsComedyMode(discourseMode) || framedAsPerformed)
        {
            return false;
        }

        bool sensitive = Regex.IsMatch(
            text,
            @"\b(?:krank\w*|medikament\w*|diagnos\w*|tödlich\w*|gestorb\w*|tot\b|schwanger\w*|verhaft\w*|gefängnis\w*|drogen\w*|abhängig\w*|affäre\w*|scheidung\w*)\b",
            options);
        bool humorContext = Regex.IsMatch(
            sourceContext,
            @"\b(?:witz\w*|scherz\w*|pointe\w*|wortspiel\w*|lachen\w*|comedy\w*)\b",
            options);
        return sensitive && humorContext;
    }

    internal static bool AreSemanticallyDuplicate(string left, string right)
    {
        HashSet<string> leftWords = SignificantWords(left);
        HashSet<string> rightWords = SignificantWords(right);
        if (leftWords.Count == 0 || rightWords.Count == 0)
        {
            return false;
        }

        int overlap = leftWords.Count(leftWord => rightWords.Any(rightWord =>
            WordsEquivalent(leftWord, rightWord)));
        return overlap / (double)Math.Min(leftWords.Count, rightWords.Count) >= 0.60;
    }

    private static bool HasBroadCoverage(
        IReadOnlyList<ValidatedText> keyPoints,
        IReadOnlyList<TranscriptLine> lines)
    {
        Dictionary<string, int> order = lines.ToDictionary(
            line => line.SourceId,
            line => line.Sequence,
            StringComparer.Ordinal);
        int[] used = keyPoints
            .SelectMany(item => item.SourceIds)
            .Where(order.ContainsKey)
            .Select(id => order[id])
            .ToArray();
        if (used.Length < 2)
        {
            return false;
        }

        int totalSpan = Math.Max(1, lines[^1].Sequence - lines[0].Sequence);
        return used.Max() - used.Min() >= totalSpan * 0.20;
    }

    private static string BuildContext(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, TranscriptLine> sources,
        IReadOnlyList<TranscriptLine> lines)
    {
        HashSet<int> sequence = sourceIds
            .Where(sources.ContainsKey)
            .Select(id => sources[id].Sequence)
            .ToHashSet();
        return string.Join(
            " ",
            lines.Where(line => sequence.Any(value => Math.Abs(line.Sequence - value) <= 8))
                .Select(line => line.SupportText));
    }

    private static bool ConcreteTokensSupported(string text, string support)
    {
        HashSet<string> supported = ExtractConcreteTokens(support);
        return ExtractConcreteTokens(text).All(supported.Contains);
    }

    private static HashSet<string> ExtractConcreteTokens(string value) =>
        Regex.Matches(
                value,
                @"(?:[€$£]\s*)?\d[\d.,:/-]*(?:\s*(?:%|€|\$|£))?",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => new string(match.Value
                .Where(character => !char.IsWhiteSpace(character))
                .Select(char.ToLowerInvariant)
                .ToArray()))
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasLexicalSupport(string text, string support)
    {
        HashSet<string> textWords = SignificantWords(text);
        HashSet<string> supportWords = SignificantWords(support);
        if (textWords.Count == 0)
        {
            return false;
        }

        int overlap = textWords.Count(textWord => supportWords.Any(supportWord =>
            WordsEquivalent(textWord, supportWord)));
        return overlap >= Math.Max(1, (int)Math.Ceiling(textWords.Count * 0.10));
    }

    private static HashSet<string> SignificantWords(string value) =>
        Regex.Matches(
                value.ToLowerInvariant(),
                @"[\p{L}\p{M}\p{N}]+",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => word.Length > 2 && !StopWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool WordsEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (left.Any(char.IsDigit) || right.Any(char.IsDigit))
        {
            return false;
        }

        int minimum = Math.Min(left.Length, right.Length);
        if (minimum < 5)
        {
            return false;
        }

        if (left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int prefix = 0;
        while (prefix < minimum
               && char.ToLowerInvariant(left[prefix]) == char.ToLowerInvariant(right[prefix]))
        {
            prefix++;
        }

        return prefix >= minimum - 1
            || (minimum >= 8 && prefix >= 5 && prefix / (double)minimum >= 0.55);
    }

    private static bool IsComedyMode(string value) =>
        Regex.IsMatch(
            value,
            @"\b(?:comedy|witz\w*|humor\w*|scherz\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? ExtractJsonObject(string output)
    {
        string value = output;
        int thinkEnd = value.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            value = value[(thinkEnd + "</think>".Length)..];
        }

        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                if (depth == 0)
                {
                    start = index;
                }

                depth++;
            }
            else if (character == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    return value[start..(index + 1)];
                }
            }
        }

        return null;
    }

    private static string ResolveTargetLanguage(Conversation conversation)
    {
        string detected = Clean(conversation.DetectedLanguage, 35).ToLowerInvariant();
        return detected switch
        {
            "de" or "de-de" or "de-at" or "de-ch" => "German",
            "en" or "en-us" or "en-gb" => "English",
            "fr" or "fr-fr" => "French",
            "es" or "es-es" => "Spanish",
            "it" or "it-it" => "Italian",
            _ => "the dominant language of the transcript"
        };
    }

    private static string Clean(string? value, int maximumLength)
    {
        string normalized = Regex.Replace(
            value ?? string.Empty,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant).Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength].TrimEnd();
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private sealed record TranscriptLine(
        int Sequence,
        string SourceId,
        string SupportText,
        string Rendered);

    private sealed record ValidatedText(string Text, string[] SourceIds);

    private sealed class EditorialDocument
    {
        public string? DiscourseMode { get; init; }
        public EditorialText? Title { get; init; }
        public EditorialText? Overview { get; init; }
        public EditorialText[]? KeyPoints { get; init; }
        public EditorialText[]? Decisions { get; init; }
        public EditorialAction[]? ActionItems { get; init; }
    }

    private sealed class EditorialText
    {
        public string? Text { get; init; }
        public string? Kind { get; init; }
        public string[]? SourceIds { get; init; }
    }

    private sealed class EditorialAction
    {
        public string? Description { get; init; }
        public string? Owner { get; init; }
        public string[]? SourceIds { get; init; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
