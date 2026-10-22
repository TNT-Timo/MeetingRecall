# Contextual summaries (1.0.30)

The production summary pipeline now summarizes the saved transcript directly. It no longer ranks isolated evidence by numeric/category scores, supplements generated key points with raw claims, or replaces a rejected title/overview with incidental facts.

Short recordings fit directly into drafting and editorial passes. Long recordings are read in chronological sections. All section notes enter hierarchical consolidation; no top-k source selection drops the middle or the ending. Original opening/ending excerpts accompany each stage to preserve the main subject and the final state. The editor sees the draft and the source context together and returns a revised document.

Summary-specific GPU requests explicitly disable thinking and select a conservative sampling profile. Previously those requests left DisableThinking at its default, so the worker enabled thinking for Qwen3 and could spend the limited output budget on reasoning rather than the requested JSON. CPU inference already requests /no_think. Input size is bounded in UTF-8 bytes, conservatively reserving space in the 16K context for output. Unicode chunks reconstruct the entire input without dropping characters.

Malformed JSON gets one retry. An unreadable editor response preserves the generated draft, not a source-sentence fallback. If no draft can be decoded, existing saved summaries remain unchanged. Model-generated facts are not mathematically verified: the editorial pass is a model review, not a guarantee of factual accuracy. A real Qwen run is still required to measure quality.

## Acceptance cases for real-model evaluation

- Nested fiction: a narrator reviews an outer game; its fictional streamer plays an inner game. The title must describe the outer work. Fictional viewer counts and streaming plans must not become claims about the actual narrator.
- Changing judgment: distinguish praise for a previous work from criticism of the current work; preserve the last explicit rating and its reasons.
- Meeting: keep final corrected amounts, explicit decisions and owners; do not turn suggestions into commitments.
- Interview/documentary: preserve who alleges or reports something and what remains uncertain.
- Long recording: include substantive developments from the middle and the final conclusion, without routine play-by-play.

Compare the generated title, overview and points with the original recording/transcript. Score subject/attribution accuracy, factual support, coverage, weighting, redundancy and readability separately. The fake-model tests verify source delivery, revision selection, format handling and persistence; they do not establish model comprehension.

The removed summary tests asserted obsolete evidence-score ordering, E-ID normalization, extractive fallbacks and lexical rejection rules. The new pipeline/integration tests exercise the replacement workflow. Title-only updates, persistence, deletion and core summary entity tests remain.

Rollback baseline before this replacement: c7e41da4394f1a4c577269e7866af489fb53e478 (1.0.29).
