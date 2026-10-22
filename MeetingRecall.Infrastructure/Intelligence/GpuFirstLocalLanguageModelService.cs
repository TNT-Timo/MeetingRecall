using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeetingRecall.Core.Intelligence;

namespace MeetingRecall.Infrastructure.Intelligence;

internal sealed class GpuFirstLocalLanguageModelService(
    SummaryLanguageModelManager modelManager)
    : ILocalLanguageModelService,
      IDisposable
{
    private static readonly JsonSerializerOptions WorkerJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> EnglishCueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "from", "that", "this", "was", "were", "has", "have",
        "their", "they", "them", "about", "into", "would", "could", "should", "answered",
        "participants", "developer", "team", "game", "quiz", "children"
    };

    private static readonly HashSet<string> FramingStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "einem", "einen",
        "und", "oder", "mit", "von", "über", "auf", "im", "in", "am", "an", "zu", "zum",
        "zur", "für", "ist", "sind", "war", "waren", "wird", "wurde", "wurden", "werden",
        "the", "and", "with", "from", "about", "into", "for", "this", "that", "was", "were"
    };

    private const int MaximumGlobalTranscriptCharacters = 80_000;

    private const string EvidenceSelectionProfile = """
        MEETINGRECALL QUALITY PROFILE. Every natural-language value you emit MUST be German.
        The JSON field names stay unchanged.

        Read the whole supplied section before choosing evidence. Preserve a balanced set of:
        1. ORIENTATION: what kind of meeting/video/interview/quiz/comedy/storytelling content this
           is, the central people, subject, purpose and governing setup;
        2. SUBSTANCE: the central topics, positions, representative personal information and
           mechanisms that characterize what actually happened;
        3. OUTCOMES: results, decisions, tasks, deadlines, changes, scores and consequences.

        Orientation is not filler. If the section clearly identifies the central subject,
        format, project/product/person or a major stage of a longer development, preserve that
        context instead of filling every slot with isolated details. A later synthesis step must
        be able to tell a reader WHAT the whole content is about without already knowing it.
        If the recording explicitly names its format or purpose, preserve that framing as one of
        the evidence items even when its category is context.

        NON-LITERAL CONTENT IS NOT A REAL-WORLD FACT ABOUT A PARTICIPANT. Jokes, punchlines,
        sarcasm, role-play, fictional examples, quoted claims, deliberate exaggeration and
        hypothetical setups must be represented as performed/discussed content. Never turn a
        dramatic joke setup such as "Person X hat eine tödliche Krankheit" into the literal
        claim that Person X is actually ill. If such material is summary-worthy, phrase the
        claim at discourse level, for example "Die Gruppe macht einen Witz über ...". Use the
        surrounding turns and explicit format cues to decide what is literal.

        For comedy, entertainment, storytelling, podcast or similar formats, the repeated core
        activity and explicit format are high-value orientation. A single shocking, medical,
        political, sexual or otherwise sensational line must never outrank a clearly stated or
        repeatedly demonstrated format merely because the isolated sentence sounds serious.

        Do not let concrete but local trivia crowd out orientation. For quizzes/interviews,
        relationship to the moderator, participant characteristics when central, career wishes,
        a used joker/help mechanism and the final achieved score can be useful. Individual
        general-knowledge answers, shape facts, city names and politician-name questions are
        normally trivia and must not dominate when stronger information exists.

        For retrospectives/reviews, preserve evidence for both the initial state/problem and the
        later development/recovery when the section supports it. A DLC, platform, metric or one
        late feature is normally a stage/example, not automatically the subject of the whole
        video.

        Never invent context. Keep claims concise, grounded and in German.
        """;

    private const string SummaryQualityProfile = """
        MEETINGRECALL QUALITY PROFILE. Every natural-language output value MUST be German.
        Brand names and proper names may remain unchanged.

        If a GLOBALER RAHMEN AUS DEM GESAMTTRANSKRIPT is supplied, use it as the primary guide
        for relevance and scope. It tells you what the conversation is as a whole; it is NOT a
        factual source. Every displayed statement still has to be directly supported by the
        supplied grounded Evidence-IDs.

        DISCOURSE MODE MATTERS. Distinguish what really happened in the recording from what was
        merely said inside a joke, punchline, story, quotation, role-play, fictional example or
        hypothetical setup. Never restate performed or non-literal content as a real-world fact
        about a participant. If the global frame identifies comedy, jokes or storytelling, the
        title and overview should describe that format/activity; isolated sensational lines are
        normally examples within the format, not headline facts.

        Think of title and overview as information for a reader with ZERO prior context. The
        reader must not have to guess what "das Spiel", "das Team", "die Performance" or an
        isolated DLC/feature refers to when the central subject is supported by evidence.

        TITLE:
        - Answer "Was ist das insgesamt?" in roughly 3-10 words.
        - Make it self-contained: name the central subject/product/person/event when grounded.
        - Prefer subject + format/broad arc over a score, reward, single answer, correction,
          joke, DLC, platform, metric or late side remark.
        - Never copy a raw transcript turn or evidence claim as the title.
        - For a quiz/interview, describe the format and participants when supported.
        - For comedy/joke content, title the episode/activity, not one joke setup or punchline.
        - For a retrospective, describe the broad development over time, e.g. problem ->
          recovery/comeback, instead of titling one intermediate stage.
        - If the global frame contains a titleDirection, treat that as the semantic target,
          while wording only with facts that are grounded by Evidence-IDs.

        OVERVIEW:
        - Use 1-2 natural German sentences.
        - Explain the overall format/setting, central participants or subject, core activity or
          purpose and, where useful, the governing setup or main outcome.
        - For comedy/storytelling, describe what the participants do and the recurring material;
          do not present a joke premise, role-play claim or exaggeration as participant biography.
        - For a retrospective or story over time, cover at least the starting problem/state AND
          the later development/result when both are supported. Do not summarize only the final
          DLC, one platform comparison or one local passage.
        - The overview must make sense to somebody who has never heard the source audio/video.
        - Follow overviewBlueprint/arc from the global frame as structure, not as factual proof.
        - Never paste dialogue.

        KEY POINTS:
        - Prefer about 3-6 genuinely useful concrete points when supported; never pad.
        - Preserve strong final results and other memorable participant/project information.
        - Do not let isolated general-knowledge trivia or sensational non-literal lines dominate.
        - Avoid semantic and bilingual duplicates.
        - Preserve an explicit role/gender exactly when grounded; do not turn "Moderator" into
          "Moderatorin" without evidence.

        Decisions and actionItems remain restricted to explicit decisions and assignments.
        Preserve numbers, names, negations and uncertainty exactly.
        """;

    private const string TitleQualityProfile = """
        Gib ausschließlich deutsche Titel aus. Titel das Thema, Format oder den Zweck des
        gesamten Gesprächs. Keine Rohzitate, keine englischen Varianten und kein isoliertes
        Detail als Haupttitel. Bei Comedy, Witzen, Rollenspiel oder Storytelling darf eine
        Pointe oder erfundene Behauptung niemals als reale Tatsache über eine Person im Titel
        erscheinen. Bevorzuge eine kurze natürliche Nominalphrase.
        """;

    private const string GlobalFrameSystemPrompt = """
        Du bist ein globaler Transkript-Analyst für Qwen3. Diese Aufgabe darf bewusst Thinking
        verwenden. Lies das gesamte bereitgestellte Transkript als eine zusammenhängende Einheit,
        bevor du antwortest. Bestimme die Informationshierarchie und den roten Faden über Anfang,
        Mitte und Ende.

        Deine wichtigste Frage lautet: "Was müsste ein Leser ohne jedes Vorwissen als Erstes
        wissen, damit der Rest Sinn ergibt?" Identifiziere den konkreten Hauptgegenstand (z. B.
        Produktname, Projekt, Person, Quiz/Interview, Comedy-/Witzeformat) und danach dessen
        breite Entwicklung oder wiederkehrende Kernaktivität.

        Bestimme ausdrücklich den DISKURSMODUS: sachliche Besprechung, Interview, Quiz, Comedy/
        Witze, Storytelling, Rollenspiel, Unterricht, Review oder etwas anderes. Einzelne Sätze
        können absichtlich unwahr, übertrieben, hypothetisch, zitiert oder nur Teil einer Pointe
        sein. Behandle solche Inhalte NICHT als Tatsachen über Teilnehmer. Bei einer Witzeepisode
        ist die zentrale Aktivität typischerweise das Erzählen, Erklären und Reagieren auf Witze;
        ein dramatischer Witz-Setup ist kein medizinischer, biografischer oder sonstiger
        Hauptsachverhalt.

        Trenne konsequent zwischen dem Gesamtgegenstand und bloßen Beispielen/Einzeldetails.
        Bei einem Quiz ist eine einzelne Wissensfrage kein Hauptthema. Bei einer Retrospektive
        ist ein einzelnes spätes Feature/DLC, eine Plattform oder eine Kennzahl kein Hauptthema,
        wenn der größere Bogen von Problemen zu späteren Verbesserungen reicht. Bei einem Meeting
        ist eine einzelne Kennzahl nicht automatisch der Meetingzweck. Bei Comedy, Storytelling
        oder Rollenspiel ist eine auffällige einzelne Behauptung nicht automatisch wahr und nicht
        automatisch das Thema.

        Gewichte explizite Selbstbeschreibung des Formats und über viele Passagen wiederholte
        Aktivität höher als den ersten schockierenden oder emotionalen Satz. Formuliere
        titleDirection und overviewBlueprint so, dass ein Leser die Quelle NICHT kennen muss.
        Keine Formulierung wie "das Spiel" ohne vorher den konkreten Gegenstand zu nennen, wenn
        dieser im Transkript erkennbar ist.

        Antworte nach dem Denken mit genau einem JSON-Objekt. Alle natürlichen Texte darin sind
        Deutsch. Erfinde nichts und formuliere knapp.
        """;

    private const string GlobalFrameCriticSystemPrompt = """
        Du bist der zweite, skeptische Redaktionspass für eine lokale Zusammenfassung. Prüfe den
        vorgeschlagenen globalen Rahmen erneut gegen das vollständige Transkript. Korrigiere ihn,
        wenn er ein lokales Detail, DLC, eine Plattform, einzelne Quizfrage, Kennzahl oder späte
        Passage fälschlich zum Hauptthema gemacht hat.

        Prüfe zusätzlich den DISKURSMODUS. Suche gezielt nach dramatischen, medizinischen,
        politischen, sexuellen oder persönlichen Behauptungen, die im Kontext nur Witz-Setup,
        Pointe, Sarkasmus, Rollenspiel, Zitat, Fiktion oder hypothetisches Beispiel sind. Solche
        Inhalte gehören in nonLiteralOrPerformedTopics bzw. incidentalTopics und dürfen weder
        subject noch titleDirection kapern. Bei Comedy/Witze muss die wiederkehrende Aktivität
        des Erzählens/Reagierens stärker gewichtet werden als eine einzelne Pointe.

        Der finale Rahmen muss die Informationshierarchie treffen: konkreter Hauptgegenstand,
        Format/Zweck und bei zeitlicher Entwicklung der Bogen von Ausgangslage über Wendepunkt
        bis zum späteren Zustand. titleDirection und overviewBlueprint müssen für Leser ohne
        Vorwissen funktionieren. Keine neuen Fakten erfinden. Ausgabe ausschließlich als JSON.
        """;

    private const string FramingCorrectionSystemPrompt = """
        Du bist ein deutscher Schlussredakteur. Der Inhalt der Kernpunkte ist bereits fest und
        darf nicht verändert werden. Repariere ausschließlich Titel und Kurzfassung so, dass sie
        den globalen Gegenstand und den breiten roten Faden treffen.

        Wenn der globale Rahmen Comedy, Witze, Storytelling, Rollenspiel oder anderes
        nicht-wörtliches Material erkennt, darfst du eine Pointe, erfundene Behauptung oder
        Übertreibung niemals als reale Tatsache über einen Teilnehmer formulieren. Im Zweifel
        lasse das isolierte Detail aus Titel/Kurzfassung weg und beschreibe das belegte Format.

        Der globale Rahmen ist nur Strukturhilfe. Jede Formulierung muss weiterhin durch die im
        SUMMARY-REQUEST enthaltenen GROUNDED EVIDENCE-IDs belegbar sein. Verwende ausschließlich
        vorhandene E-IDs. Erfinde keine Namen, Rollen, Orte, Institutionen, Altersangaben,
        Zeitpunkte oder Ereignisse. Titel und Kurzfassung müssen vollständig auf Deutsch sein.
        Ausgabe genau als JSON mit title und overview, ohne Kommentar.
        """;

    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly List<string> _recentTranscriptSections = [];
    private bool _disposed;

    public bool IsModelInstalled => modelManager.IsModelInstalled;
    public string ModelDisplayName => modelManager.ModelDisplayName;
    public long ModelDownloadSizeBytes => modelManager.ModelDownloadSizeBytes;

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        if (maximumOutputTokens < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        bool isEvidenceRequest = IsEvidenceRequest(systemPrompt);
        bool isSummaryRequest = IsSummaryRequest(systemPrompt);
        bool isTitleRequest = IsTitleRequest(systemPrompt);
        bool isGroundedQaRequest = IsGroundedQaRequest(systemPrompt);
        bool isGroundedQaVerificationRequest =
            IsGroundedQaVerificationRequest(systemPrompt);
        (systemPrompt, userPrompt) = ApplyQualityProfile(systemPrompt, userPrompt);

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isEvidenceRequest)
            {
                CaptureTranscriptSection(userPrompt);
            }

            string modelPath = await modelManager
                .EnsureCurrentModelInstalledAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.LoadingModel));

            string? globalFrame = null;
            if (isSummaryRequest)
            {
                globalFrame = await BuildGlobalFrameAsync(modelPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(globalFrame))
                {
                    userPrompt = $$"""
                        GLOBALER RAHMEN AUS DEM GESAMTTRANSKRIPT
                        (primäre Relevanz-/Strukturhilfe; KEINE neue Faktenquelle):
                        {{globalFrame}}

                        REDAKTIONSREGELN:
                        - Der Titel muss für einen Leser ohne Vorwissen den Hauptgegenstand nennen
                          und den titleDirection semantisch treffen, soweit Evidence ihn stützt.
                        - Die Kurzfassung muss den overviewBlueprint bzw. den breiten arc abdecken,
                          nicht nur eine einzelne Passage.
                        - Bei Retrospektiven sind DLCs, Plattformen, einzelne Performance-Werte
                          und Schlussbemerkungen normalerweise Stationen, nicht der Gesamttitel.
                        - Bei Comedy, Witzen, Storytelling oder Rollenspiel schlagen Format und
                          wiederkehrende Kernaktivität einzelne Setups, Pointen und Schockzeilen.
                        - Inhalte aus nonLiteralOrPerformedTopics dürfen niemals als reale Fakten
                          über Teilnehmer formuliert werden. Wenn die GROUNDED EVIDENCE den
                          nicht-wörtlichen Kontext nicht sauber trägt, lass das Detail lieber weg.
                        - Rahmendetails dürfen NUR ausgegeben werden, wenn sie zusätzlich durch
                          die unten vorhandenen GROUNDED EVIDENCE-IDs gestützt sind.

                        {{userPrompt}}
                        """;
                }
            }

            // Keep compact Q&A in non-thinking mode, but do not force greedy TopK=1
            // decoding for the actual answer. Natural paraphrasing and role inference
            // need Qwen's normal non-thinking sampling. Only the independent verifier
            // uses deterministic decoding as the safety boundary.
            bool disableThinking = isGroundedQaRequest
                || modelManager.CurrentModelDisablesThinking;
            bool qwenThinkingEnabled = !disableThinking
                && modelManager.ModelDisplayName.StartsWith(
                    "Qwen3",
                    StringComparison.OrdinalIgnoreCase);
            int workerOutputTokens = qwenThinkingEnabled ? -1 : maximumOutputTokens;

            WorkerAttempt attempt = await TryGenerateOnGpuAsync(
                    modelPath,
                    systemPrompt,
                    userPrompt,
                    workerOutputTokens,
                    disableThinking,
                    isGroundedQaVerificationRequest,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Result is null)
            {
                string error = attempt.Error ?? "The GPU worker failed without diagnostics.";
                Debug.WriteLine("LOCAL LLM GPU WORKER FAILED — NO CPU FALLBACK. " + error);
                throw new InvalidOperationException(
                    "Das ausgewählte Sprachmodell konnte nicht auf der GPU gestartet werden. "
                    + "Es wurde bewusst kein CPU-Fallback verwendet.\n\n"
                    + error);
            }

            string selectedOutput = attempt.Result.Output;
            if ((isSummaryRequest || isTitleRequest)
                && HasObviousEnglishNaturalLanguage(selectedOutput))
            {
                WorkerAttempt germanRetry = await TryGenerateOnGpuAsync(
                        modelPath,
                        systemPrompt
                        + "\n\nHARTER FEHLERKORREKTUR-PASS: Alle natürlichen Ausgabewerte ausschließlich auf Deutsch.",
                        userPrompt
                        + "\n\nDie vorige Ausgabe enthielt Englisch. Erzeuge das geforderte JSON vollständig neu und ausschließlich auf Deutsch.",
                        maximumOutputTokens,
                        disableThinking: true,
                        deterministicSampling: false,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (germanRetry.Result is not null
                    && !HasObviousEnglishNaturalLanguage(germanRetry.Result.Output))
                {
                    selectedOutput = germanRetry.Result.Output;
                }
            }

            if (isSummaryRequest
                && !string.IsNullOrWhiteSpace(globalFrame)
                && ShouldRepairGlobalFraming(selectedOutput, globalFrame))
            {
                string? repaired = await TryRepairGlobalFramingAsync(
                        modelPath,
                        globalFrame,
                        userPrompt,
                        selectedOutput,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(repaired))
                {
                    selectedOutput = repaired;
                }
            }

            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.Generating,
                Fraction: 1));
            Debug.WriteLine(
                $"Local LLM runtime: {attempt.Result.Runtime} · GPU worker; "
                + $"full model offload · {modelManager.ModelDisplayName}; "
                + $"thinking={(qwenThinkingEnabled ? "on" : "off")}; "
                + $"output-budget={(workerOutputTokens < 0 ? "natural completion" : workerOutputTokens)}.");
            return selectedOutput;
        }
        finally
        {
            if (isSummaryRequest)
            {
                _recentTranscriptSections.Clear();
            }

            _inferenceGate.Release();
        }
    }

    private async Task<string?> BuildGlobalFrameAsync(
        string modelPath,
        CancellationToken cancellationToken)
    {
        if (_recentTranscriptSections.Count == 0
            || !modelManager.ModelDisplayName.StartsWith(
                "Qwen3",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string transcript = BuildGlobalTranscript(_recentTranscriptSections);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        string contract = """
            {
              "subject": "konkreter Hauptgegenstand, mit Eigennamen wenn eindeutig",
              "contentType": "Format/Art des Gesprächs oder Inhalts",
              "discourseMode": "z. B. sachlich, Interview, Quiz, Comedy/Witze, Storytelling, Rollenspiel",
              "purpose": "Kernaktivität/Zweck",
              "dominantActivity": "was über den größten Teil des Inhalts wiederholt getan wird",
              "arcStart": "Ausgangslage oder Anfangszustand",
              "arcMiddle": "zentrale Entwicklung/Wendepunkte",
              "arcEnd": "späterer Zustand/Ergebnis",
              "mainArc": "roter Faden des gesamten Inhalts in einem Satz",
              "titleDirection": "3-10 Wörter als semantische Titelrichtung, kein Rohzitat",
              "overviewBlueprint": "1-2 Sätze: was ist das, worum geht es, welcher breite Bogen",
              "mustMention": ["wenige Elemente, ohne die Titel/Überblick unverständlich wären"],
              "centralTopics": ["wenige zentrale Themen"],
              "incidentalTopics": ["auffällige lokale Details, die NICHT den Titel kapern dürfen"],
              "nonLiteralOrPerformedTopics": ["Witz-Setups, Pointen, Rollenspiel, Zitate oder hypothetische Inhalte, die keine realen Fakten sind"]
            }
            """;

        string prompt = $$"""
            Lies das folgende Gesamttranskript zuerst vollständig. Erstelle erst danach die
            Informationshierarchie. Suche bewusst nach dem roten Faden über Anfang, Mitte und
            Ende. Ein Leser kennt die Quelle, den Dateinamen und das Video NICHT.

            Bestimme zuerst den Diskursmodus und die wiederkehrende Kernaktivität. Prüfe bei
            auffälligen Behauptungen immer den umgebenden Kontext: Ist das eine reale Aussage,
            oder Teil eines Witzes, einer Pointe, eines Zitats, Rollenspiels, einer Übertreibung,
            Fiktion oder eines hypothetischen Beispiels? Nicht-wörtliche Inhalte dürfen niemals
            als Biografie, Diagnose oder reale Eigenschaft eines Teilnehmers behandelt werden.

            Bei Comedy/Witze/Storytelling frage ausdrücklich:
            1. Wie bezeichnet die Quelle ihr eigenes Format?
            2. Welche Aktivität wiederholt sich über den größten Teil des Transkripts?
            3. Welche auffälligen Aussagen sind nur Setups/Pointen/gespielter Inhalt?
            4. Welche wenigen wiederkehrenden Themen charakterisieren die Episode wirklich?
            Ein einzelner schockierender Witz-Setup ist niemals automatisch das Hauptthema.

            Bei einer Retrospektive/Review frage ausdrücklich:
            1. Welcher konkrete Gegenstand wird insgesamt behandelt?
            2. Was war die Ausgangslage bzw. das Hauptproblem?
            3. Was änderte sich im Verlauf?
            4. Wie ist der spätere Zustand bzw. das Ergebnis?
            Eine Erweiterung, Plattform, einzelne Zahl oder Schlussaussage ist nur dann das
            Hauptthema, wenn sie tatsächlich den überwiegenden Inhalt bestimmt.

            Bei Quiz/Interview frage entsprechend nach Format, Beteiligten, Spielregel/Zweck
            und Ergebnis, statt einzelne Wissensfragen zum Thema zu machen.

            JSON-VERTRAG:
            {{contract}}

            GESAMTTRANSKRIPT:
            {{transcript}}
            """;

        WorkerAttempt frameAttempt = await TryGenerateOnGpuAsync(
                modelPath,
                GlobalFrameSystemPrompt,
                prompt,
                maximumOutputTokens: -1,
                disableThinking: false,
                deterministicSampling: false,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (frameAttempt.Result is null)
        {
            Debug.WriteLine(
                "Global Qwen framing pass failed; continuing without global frame. "
                + frameAttempt.Error);
            return null;
        }

        string? firstFrame = ExtractJsonAfterThinking(frameAttempt.Result.Output);
        if (!IsValidJsonObject(firstFrame))
        {
            Debug.WriteLine(
                "Global Qwen framing pass returned no complete JSON; continuing without it.");
            return null;
        }

        string criticPrompt = $$"""
            Prüfe den folgenden ersten globalen Rahmen nochmals gegen dasselbe vollständige
            Transkript. Suche gezielt nach dem typischen Fehler, dass ein konkretes spätes Detail
            fälschlich den Gesamttitel übernimmt. Wenn der Inhalt eine Entwicklung erzählt, muss
            mainArc diese Entwicklung abbilden. Wenn der zentrale Gegenstand einen Eigennamen hat,
            soll subject ihn nennen.

            Prüfe zusätzlich jede auffällige persönliche, medizinische oder dramatische Aussage:
            Ist sie im Kontext wirklich als reale Tatsache gemeint oder nur Witz, Pointe,
            Sarkasmus, Rollenspiel, Zitat, Fiktion oder Hypothese? Trage solche Inhalte in
            nonLiteralOrPerformedTopics ein und verhindere, dass sie subject/titleDirection
            dominieren. Bei Comedy/Witze müssen Format und dominante Aktivität klar erkennbar
            bleiben.

            Korrigiere titleDirection und overviewBlueprint so, dass sie ohne Vorwissen
            verständlich sind. Gib genau dasselbe JSON-Schema zurück.

            JSON-VERTRAG:
            {{contract}}

            ERSTER RAHMEN:
            {{firstFrame}}

            GESAMTTRANSKRIPT:
            {{transcript}}
            """;

        WorkerAttempt criticAttempt = await TryGenerateOnGpuAsync(
                modelPath,
                GlobalFrameCriticSystemPrompt,
                criticPrompt,
                maximumOutputTokens: -1,
                disableThinking: false,
                deterministicSampling: false,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (criticAttempt.Result is not null)
        {
            string? refinedFrame = ExtractJsonAfterThinking(criticAttempt.Result.Output);
            if (IsValidJsonObject(refinedFrame))
            {
                return refinedFrame;
            }
        }

        return firstFrame;
    }

    private async Task<string?> TryRepairGlobalFramingAsync(
        string modelPath,
        string globalFrame,
        string summaryPrompt,
        string originalOutput,
        CancellationToken cancellationToken)
    {
        string? originalJson = ExtractJsonAfterThinking(originalOutput);
        if (!IsValidJsonObject(originalJson))
        {
            return null;
        }

        string repairPrompt = $$"""
            Der erste Entwurf hat die globale Informationshierarchie nicht zuverlässig getroffen.
            Repariere NUR title und overview. Die bereits erzeugten keyPoints, decisions und
            actionItems werden vom Programm unverändert übernommen.

            TITLE muss den konkreten Hauptgegenstand nennen und den breiten titleDirection treffen.
            OVERVIEW muss für einen Leser ohne Vorwissen verständlich sein und bei einer zeitlichen
            Entwicklung mindestens Ausgangsproblem/-zustand und spätere Entwicklung/Ergebnis
            abdecken, sofern die GROUNDED EVIDENCE das unterstützt.

            Wenn discourseMode Comedy/Witze, Storytelling, Rollenspiel oder anderes
            nicht-wörtliches Material bezeichnet, darf kein Eintrag aus
            nonLiteralOrPerformedTopics als reale Tatsache über eine Person erscheinen. Wenn die
            Evidence nur den dramatischen Satz selbst, aber nicht dessen nicht-wörtlichen Kontext
            trägt, lasse ihn aus Titel und Overview weg. Bevorzuge den belegten Format-/
            Aktivitätsrahmen.

            Verwende ausschließlich Evidence-IDs aus dem SUMMARY-REQUEST. Jeder komplette Satz
            muss durch die angegebenen IDs gestützt sein. Lieber etwas konservativer formulieren
            als eine unbelegte Zusatzinformation einführen.

            GLOBALER RAHMEN (nur Strukturhilfe):
            {{globalFrame}}

            BISHERIGER ENTWURF:
            {{originalJson}}

            SUMMARY-REQUEST MIT GROUNDED EVIDENCE:
            {{summaryPrompt}}

            JSON-VERTRAG:
            {
              "title": {"text": "deutscher globaler Titel", "evidenceIds": ["E001"]},
              "overview": {"text": "deutsche globale Kurzfassung", "evidenceIds": ["E001", "E002"]}
            }
            """;

        WorkerAttempt repairAttempt = await TryGenerateOnGpuAsync(
                modelPath,
                FramingCorrectionSystemPrompt,
                repairPrompt,
                maximumOutputTokens: 640,
                disableThinking: true,
                deterministicSampling: false,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (repairAttempt.Result is null)
        {
            return null;
        }

        string? correctionJson = ExtractJsonAfterThinking(repairAttempt.Result.Output);
        return MergeFramingFields(originalJson!, correctionJson);
    }

    internal static bool ShouldRepairGlobalFraming(
        string output,
        string globalFrame)
    {
        string? outputJson = ExtractJsonAfterThinking(output);
        if (!IsValidJsonObject(outputJson) || !IsValidJsonObject(globalFrame))
        {
            return false;
        }

        try
        {
            using JsonDocument outputDocument = JsonDocument.Parse(outputJson!);
            using JsonDocument frameDocument = JsonDocument.Parse(globalFrame);
            string title = GetNestedString(outputDocument.RootElement, "title", "text");
            string overview = GetNestedString(outputDocument.RootElement, "overview", "text");
            string subject = GetString(frameDocument.RootElement, "subject");
            string contentType = GetString(frameDocument.RootElement, "contentType");
            string discourseMode = GetString(frameDocument.RootElement, "discourseMode");
            string purpose = GetString(frameDocument.RootElement, "purpose");
            string dominantActivity = GetString(frameDocument.RootElement, "dominantActivity");
            string titleDirection = GetString(frameDocument.RootElement, "titleDirection");
            string mainArc = GetString(frameDocument.RootElement, "mainArc");

            string[] globalAnchors =
            [
                subject,
                contentType,
                discourseMode,
                purpose,
                dominantActivity,
                titleDirection,
                mainArc
            ];
            string[] incidentalAnchors = GetStringArray(
                    frameDocument.RootElement,
                    "incidentalTopics")
                .Concat(GetStringArray(
                    frameDocument.RootElement,
                    "nonLiteralOrPerformedTopics"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            bool knownSubjectMissing = !string.IsNullOrWhiteSpace(subject)
                && SemanticAnchorScore(title, subject) < 0.28;

            bool titleFollowsGlobalDirection =
                DistinctiveGlobalAnchorScore(title, globalAnchors, incidentalAnchors) >= 0.22;
            bool overviewFollowsGlobalDirection =
                DistinctiveGlobalAnchorScore(overview, globalAnchors, incidentalAnchors) >= 0.14;
            bool titleAnchoredInIncidental = incidentalAnchors.Any(item =>
                SemanticAnchorScore(title, item) >= 0.45);
            bool overviewAnchoredInIncidental = incidentalAnchors.Any(item =>
                SemanticAnchorScore(overview, item) >= 0.45);

            bool overviewLacksSubject = !string.IsNullOrWhiteSpace(subject)
                && SemanticAnchorScore(overview, subject) < 0.16;

            return knownSubjectMissing
                || overviewLacksSubject
                || (titleAnchoredInIncidental && !titleFollowsGlobalDirection)
                || (overviewAnchoredInIncidental && !overviewFollowsGlobalDirection);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? MergeFramingFields(
        string originalJson,
        string? correctionJson)
    {
        if (!IsValidJsonObject(correctionJson))
        {
            return null;
        }

        try
        {
            JsonObject? original = JsonNode.Parse(originalJson) as JsonObject;
            JsonObject? correction = JsonNode.Parse(correctionJson!) as JsonObject;
            if (original is null
                || correction?["title"] is not JsonObject correctedTitle
                || correction["overview"] is not JsonObject correctedOverview)
            {
                return null;
            }

            original["title"] = correctedTitle.DeepClone();
            original["overview"] = correctedOverview.DeepClone();
            return original.ToJsonString(WorkerJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValidJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetNestedString(
        JsonElement root,
        string objectName,
        string propertyName)
    {
        if (root.TryGetProperty(objectName, out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return GetString(nested, propertyName);
        }

        return string.Empty;
    }

    private static string GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> GetStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty);
    }

    private static double SemanticAnchorScore(string text, string anchor)
    {
        string[] anchorTokens = FramingTokens(anchor).ToArray();
        if (anchorTokens.Length == 0)
        {
            return 0;
        }

        string[] textTokens = FramingTokens(text).ToArray();
        int matches = anchorTokens.Count(anchorToken =>
            textTokens.Any(textToken => FramingTokensEquivalent(anchorToken, textToken)));
        return matches / (double)anchorTokens.Length;
    }

    private static double DistinctiveGlobalAnchorScore(
        string text,
        IEnumerable<string> globalAnchors,
        IEnumerable<string> incidentalAnchors)
    {
        string[] excludedTokens = incidentalAnchors
            .SelectMany(FramingTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] globalTokens = globalAnchors
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(FramingTokens)
            .Where(token => !excludedTokens.Any(excluded =>
                FramingTokensEquivalent(token, excluded)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (globalTokens.Length == 0)
        {
            globalTokens = globalAnchors
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(FramingTokens)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (globalTokens.Length == 0)
        {
            return 0;
        }

        string[] textTokens = FramingTokens(text).ToArray();
        int matches = globalTokens.Count(globalToken =>
            textTokens.Any(textToken => FramingTokensEquivalent(globalToken, textToken)));
        return matches / (double)globalTokens.Length;
    }

    private static IEnumerable<string> FramingTokens(string value) =>
        Regex.Matches(
                value.ToLowerInvariant(),
                "[\\p{L}\\p{N}]+",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(token =>
                (token.Length >= 4 || token.Any(char.IsDigit))
                && !FramingStopWords.Contains(token));

    private static bool FramingTokensEquivalent(string left, string right)
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

        int commonPrefix = 0;
        while (commonPrefix < minimum
               && char.ToLowerInvariant(left[commonPrefix])
                  == char.ToLowerInvariant(right[commonPrefix]))
        {
            commonPrefix++;
        }

        return commonPrefix >= minimum - 1
            || (minimum >= 8
                && commonPrefix >= 5
                && commonPrefix / (double)minimum >= 0.5);
    }

    private void CaptureTranscriptSection(string userPrompt)
    {
        if (userPrompt.Contains(
                "transcript section 1 of ",
                StringComparison.OrdinalIgnoreCase))
        {
            _recentTranscriptSections.Clear();
        }

        const string marker = "TRANSCRIPT SECTION:";
        int markerIndex = userPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return;
        }

        string section = userPrompt[(markerIndex + marker.Length)..].Trim();
        if (section.Length > 0)
        {
            _recentTranscriptSections.Add(section);
        }
    }

    private static string BuildGlobalTranscript(IEnumerable<string> sections)
    {
        var lines = new List<string>();
        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string section in sections)
        {
            foreach (string rawLine in section.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string? sourceId = TryExtractSourceId(line);
                if (sourceId is not null && !seenSourceIds.Add(sourceId))
                {
                    continue;
                }

                lines.Add(line);
            }
        }

        string transcript = string.Join(Environment.NewLine, lines);
        if (transcript.Length <= MaximumGlobalTranscriptCharacters)
        {
            return transcript;
        }

        const int edgeCharacters = 28_000;
        const int middleCharacters = 24_000;
        int middleStart = Math.Max(
            edgeCharacters,
            (transcript.Length / 2) - (middleCharacters / 2));
        return transcript[..edgeCharacters]
            + Environment.NewLine
            + "[... mittlerer Teil repräsentativ gekürzt ...]"
            + Environment.NewLine
            + transcript.Substring(middleStart, middleCharacters)
            + Environment.NewLine
            + "[... späterer Teil repräsentativ gekürzt ...]"
            + Environment.NewLine
            + transcript[^edgeCharacters..];
    }

    private static string? TryExtractSourceId(string line)
    {
        if (!line.StartsWith("[L", StringComparison.Ordinal))
        {
            return null;
        }

        int close = line.IndexOf(']');
        return close > 2 ? line[1..close] : null;
    }

    private static bool IsEvidenceRequest(string systemPrompt) =>
        systemPrompt.Contains(
            "selective transcript evidence analyst",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSummaryRequest(string systemPrompt) =>
        systemPrompt.Contains(
            "expert multilingual editor",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTitleRequest(string systemPrompt) =>
        systemPrompt.Contains(
            "concise multilingual titles",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGroundedQaRequest(string systemPrompt) =>
        systemPrompt.Contains(
            GroundedConversationQuestionService.GroundedQaProfileMarker,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGroundedQaVerificationRequest(string systemPrompt) =>
        systemPrompt.Contains(
            GroundedConversationQuestionService.GroundedQaVerificationMarker,
            StringComparison.OrdinalIgnoreCase);

    private static (string SystemPrompt, string UserPrompt) ApplyQualityProfile(
        string systemPrompt,
        string userPrompt)
    {
        if (IsEvidenceRequest(systemPrompt))
        {
            return (systemPrompt + "\n\n" + EvidenceSelectionProfile, userPrompt);
        }

        if (IsSummaryRequest(systemPrompt))
        {
            return (systemPrompt + "\n\n" + SummaryQualityProfile, userPrompt);
        }

        if (IsTitleRequest(systemPrompt))
        {
            return (systemPrompt + "\n\n" + TitleQualityProfile, userPrompt);
        }

        return (systemPrompt, userPrompt);
    }

    private static bool HasObviousEnglishNaturalLanguage(string output)
    {
        string? json = ExtractJsonAfterThinking(output);
        if (json is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var values = new List<string>();
            CollectStringValues(document.RootElement, values);
            return values.Any(HasObviousEnglish);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CollectStringValues(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CollectStringValues(property.Value, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectStringValues(item, values);
                }

                break;
            case JsonValueKind.String:
                string value = element.GetString() ?? string.Empty;
                if (!Regex.IsMatch(
                        value,
                        "^[EL]\\d{3,6}$",
                        RegexOptions.CultureInvariant))
                {
                    values.Add(value);
                }

                break;
        }
    }

    private static bool HasObviousEnglish(string value)
    {
        string[] words = Regex.Matches(
                value.ToLowerInvariant(),
                "[\\p{L}]+",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0)
        {
            return false;
        }

        int hits = words.Count(word => EnglishCueWords.Contains(word));
        return hits >= 2 && hits / (double)words.Length >= 0.08;
    }

    private static string? ExtractJsonAfterThinking(string output)
    {
        string candidate = output;
        int thinkEnd = candidate.LastIndexOf(
            "</think>",
            StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            candidate = candidate[(thinkEnd + "</think>".Length)..];
        }

        int start = -1;
        int depth = 0;
        bool insideString = false;
        bool escaped = false;
        for (int index = 0; index < candidate.Length; index++)
        {
            char character = candidate[index];
            if (insideString)
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
                    insideString = false;
                }

                continue;
            }

            if (character == '"')
            {
                insideString = true;
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
                    return candidate[start..(index + 1)];
                }
            }
        }

        return null;
    }

    private static async Task<WorkerAttempt> TryGenerateOnGpuAsync(
        string modelPath,
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        bool disableThinking,
        bool deterministicSampling,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        string workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "QwenWorker",
            "MeetingRecall.QwenWorker.exe");
        if (!File.Exists(workerPath))
        {
            return new WorkerAttempt(
                null,
                $"GPU worker not found: {workerPath}");
        }

        string workerDirectory = Path.GetDirectoryName(workerPath)
            ?? AppContext.BaseDirectory;
        string requestPath = Path.Combine(
            Path.GetTempPath(),
            $"MeetingRecall-llm-request-{Guid.NewGuid():N}.json");
        string resultPath = Path.Combine(
            Path.GetTempPath(),
            $"MeetingRecall-llm-result-{Guid.NewGuid():N}.json");
        var request = new WorkerRequest(
            modelPath,
            systemPrompt,
            userPrompt,
            maximumOutputTokens,
            disableThinking,
            deterministicSampling);

        try
        {
            await using (var requestStream = new FileStream(
                             requestPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        requestStream,
                        request,
                        WorkerJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await requestStream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                WorkingDirectory = workerDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(resultPath);

            Debug.WriteLine(
                $"Local LLM GPU worker starting from '{workerDirectory}' "
                + $"with model '{Path.GetFileName(modelPath)}'.");
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new WorkerAttempt(
                    null,
                    "GPU worker could not be started.");
            }

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task progressTask = PumpWorkerProgressAsync(
                process.StandardOutput,
                maximumOutputTokens,
                progress,
                cancellationToken);
            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(GetInferenceTimeout(maximumOutputTokens));

            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                return new WorkerAttempt(
                    null,
                    "GPU worker exceeded the inference timeout.");
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                throw;
            }

            string error = await errorTask.ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine(
                    "Local LLM GPU worker diagnostics:\n" + error.Trim());
            }

            if (process.ExitCode != 0 || !File.Exists(resultPath))
            {
                return new WorkerAttempt(
                    null,
                    $"GPU worker exit code {process.ExitCode}: {error.Trim()}");
            }

            await using FileStream resultStream = File.OpenRead(resultPath);
            WorkerResult? result = await JsonSerializer.DeserializeAsync<WorkerResult>(
                    resultStream,
                    WorkerJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(result.Output))
            {
                return new WorkerAttempt(
                    null,
                    "GPU worker returned no result.");
            }

            return new WorkerAttempt(result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WorkerAttempt(null, exception.ToString());
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
            TryDelete(resultPath + ".partial");
        }
    }

    private static async Task PumpWorkerProgressAsync(
        StreamReader output,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false)
               is { } line)
        {
            string[] values = line.Split('\t');
            if (values.Length != 2
                || !int.TryParse(values[0], out int generatedTokens)
                || !int.TryParse(values[1], out int completed))
            {
                continue;
            }

            double? fraction = completed == 1
                ? 1
                : maximumOutputTokens > 0
                    ? Math.Clamp(
                        generatedTokens / (double)maximumOutputTokens,
                        0,
                        0.99)
                    : null;
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.Generating,
                fraction));
        }
    }

    private static TimeSpan GetInferenceTimeout(int maximumOutputTokens) =>
        maximumOutputTokens < 0
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(Math.Clamp(
                6 + (maximumOutputTokens / 128d),
                8,
                20));

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The worker may already have terminated while cancellation was handled.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary worker files are best-effort cleanup only.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inferenceGate.Dispose();
    }

    private sealed record WorkerRequest(
        string ModelPath,
        string SystemPrompt,
        string UserPrompt,
        int MaximumOutputTokens,
        bool DisableThinking,
        bool DeterministicSampling);

    private sealed record WorkerAttempt(
        WorkerResult? Result,
        string? Error);

    private sealed record WorkerResult(
        string Runtime,
        string Output);
}
