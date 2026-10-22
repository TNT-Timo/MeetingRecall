using System.Text;
using System.Text.RegularExpressions;

namespace MeetingRecall.Infrastructure.Transcription;

internal sealed record TranscriptFilterResult<T>(
    IReadOnlyList<T> Items,
    int SuppressedCount);

internal static class TranscriptRepetitionFilter
{
    private const int MinimumCycleRepetitions = 3;
    private const int MaximumCycleLength = 6;
    private const int MinimumLongPhraseBurstRepetitions = 5;
    private const int MinimumShortPhraseBurstRepetitions = 8;
    private const int ExtremeBurstRepetitions = 8;
    private const int MinimumTightShortBurstRepetitions = 5;
    private const int MaximumInlineCycleLength = 3;
    private const int UntimedBurstWindowItems = 48;
    private const int UntimedTightBurstWindowItems = 12;
    private const int MinimumRepeatedWordSpan = 4;
    private const int MaximumRepeatedWordSpan = 24;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TightBurstWindow = TimeSpan.FromSeconds(30);
    private static readonly Regex WordRegex = new(
        "[\\p{L}\\p{N}]+",
        RegexOptions.CultureInvariant);

    public static string CollapseAdjacentPhrases(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string current = CollapseAdjacentWordCycles(Regex.Replace(
            value.Trim(),
            "(?<phrase>(?:\\*[^*]{3,}\\*|[^.!?]{8,}[.!?]))\\s*\\k<phrase>",
            "${phrase}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        string previous;
        do
        {
            previous = current;
            current = Regex.Replace(
                current,
                "(?<phrase>(?:\\*[^*]{3,}\\*|[^.!?]{8,}[.!?]))\\s*\\k<phrase>",
                "${phrase}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        while (!string.Equals(previous, current, StringComparison.Ordinal));

        MatchCollection sentenceMatches = Regex.Matches(
            current,
            "[^.!?]+(?:[.!?]+|$)",
            RegexOptions.CultureInvariant);
        if (sentenceMatches.Count < 2)
        {
            return Regex.Replace(current, "\\s+", " ").Trim();
        }

        string[] sentences = sentenceMatches
            .Cast<Match>()
            .Select(match => match.Value.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToArray();
        string[] normalized = sentences.Select(Normalize).ToArray();
        var kept = new List<string>(sentences.Length);
        for (int index = 0; index < sentences.Length;)
        {
            int cycleLength = FindInlineCycleLength(normalized, index);
            if (cycleLength == 0)
            {
                kept.Add(sentences[index++]);
                continue;
            }

            kept.AddRange(sentences.Skip(index).Take(cycleLength));
            int cursor = index + cycleLength;
            while (cursor + cycleLength <= sentences.Length
                   && InlineBlocksMatch(normalized, index, cursor, cycleLength))
            {
                cursor += cycleLength;
            }

            index = cursor;
        }

        return CollapseAdjacentWordCycles(
            Regex.Replace(string.Join(" ", kept), "\\s+", " ").Trim());
    }

    public static string TrimLeadingWordOverlap(
        string previous,
        string current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        MatchCollection previousWords = WordRegex.Matches(previous);
        MatchCollection currentWords = WordRegex.Matches(current);
        int maximum = Math.Min(
            MaximumRepeatedWordSpan,
            Math.Min(previousWords.Count, currentWords.Count));
        for (int length = maximum; length >= MinimumRepeatedWordSpan; length--)
        {
            int previousStart = previousWords.Count - length;
            bool matches = true;
            for (int offset = 0; offset < length; offset++)
            {
                if (!TokenEquals(
                        previousWords[previousStart + offset].Value,
                        currentWords[offset].Value))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            Match lastOverlapWord = currentWords[length - 1];
            return current[(lastOverlapWord.Index + lastOverlapWord.Length)..]
                .TrimStart()
                .TrimStart(',', '.', ';', ':', '-', '–', '—')
                .TrimStart();
        }

        return current.Trim();
    }

    private static string CollapseAdjacentWordCycles(string value)
    {
        string current = value;
        while (true)
        {
            MatchCollection words = WordRegex.Matches(current);
            bool removed = false;
            for (int start = 0; start < words.Count && !removed; start++)
            {
                int maximum = Math.Min(
                    MaximumRepeatedWordSpan,
                    (words.Count - start) / 2);
                for (int length = maximum;
                     length >= MinimumRepeatedWordSpan;
                     length--)
                {
                    bool matches = true;
                    for (int offset = 0; offset < length; offset++)
                    {
                        if (!TokenEquals(
                                words[start + offset].Value,
                                words[start + length + offset].Value))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (!matches)
                    {
                        continue;
                    }

                    Match repeatedStart = words[start + length];
                    int nextWordIndex = start + (2 * length);
                    int repeatedEnd = nextWordIndex < words.Count
                        ? words[nextWordIndex].Index
                        : current.Length;
                    current = current.Remove(
                        repeatedStart.Index,
                        repeatedEnd - repeatedStart.Index);
                    current = Regex.Replace(current, "\\s+", " ").Trim();
                    removed = true;
                    break;
                }
            }

            if (!removed)
            {
                return current;
            }
        }
    }

    private static bool TokenEquals(string left, string right) =>
        string.Equals(
            left.Normalize(NormalizationForm.FormKC),
            right.Normalize(NormalizationForm.FormKC),
            StringComparison.OrdinalIgnoreCase);

    private static int FindInlineCycleLength(string[] text, int start)
    {
        int maximum = Math.Min(
            MaximumInlineCycleLength,
            (text.Length - start) / 2);
        for (int length = 1; length <= maximum; length++)
        {
            int cycleCharacterCount = text
                .Skip(start)
                .Take(length)
                .Sum(sentence => sentence.Length);
            if ((length > 1 || text[start].Length >= 8)
                && cycleCharacterCount >= 8
                && InlineBlocksMatch(text, start, start + length, length))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool InlineBlocksMatch(
        IReadOnlyList<string> text,
        int left,
        int right,
        int length)
    {
        for (int offset = 0; offset < length; offset++)
        {
            string leftText = text[left + offset];
            string rightText = text[right + offset];
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal)
                && !IsSimilar(leftText, rightText))
            {
                return false;
            }
        }

        return true;
    }

    public static TranscriptFilterResult<T> Filter<T>(
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        Func<T, TimeSpan>? startSelector = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textSelector);
        TranscriptFilterResult<T> burstFiltered = FilterRecurringBursts(
            items,
            textSelector,
            startSelector);
        IReadOnlyList<T> cycleInput = burstFiltered.Items;
        if (cycleInput.Count < MinimumCycleRepetitions)
        {
            return burstFiltered;
        }

        string[] normalized = cycleInput
            .Select(item => Normalize(textSelector(item)))
            .ToArray();
        var cycleFiltered = new List<T>(cycleInput.Count);
        int suppressed = burstFiltered.SuppressedCount;

        for (int index = 0; index < cycleInput.Count;)
        {
            int cycleLength = FindCycleLength(normalized, index);
            if (cycleLength > 0
                && !IsInsideBurstWindow(
                    cycleInput,
                    index,
                    index + (2 * cycleLength),
                    startSelector))
            {
                cycleLength = 0;
            }

            if (cycleLength == 0)
            {
                cycleFiltered.Add(cycleInput[index++]);
                continue;
            }

            for (int offset = 0; offset < cycleLength; offset++)
            {
                cycleFiltered.Add(cycleInput[index + offset]);
            }

            int cursor = index + cycleLength;
            while (cursor + cycleLength <= cycleInput.Count
                   && IsInsideBurstWindow(
                       cycleInput,
                       index,
                       cursor + cycleLength - 1,
                       startSelector)
                   && BlocksMatch(normalized, index, cursor, cycleLength))
            {
                suppressed += cycleLength;
                cursor += cycleLength;
            }

            index = cursor;
        }

        return new TranscriptFilterResult<T>(cycleFiltered, suppressed);
    }

    private static TranscriptFilterResult<T> FilterRecurringBursts<T>(
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        Func<T, TimeSpan>? startSelector)
    {
        var occurrences = new Dictionary<string, List<int>>(
            StringComparer.Ordinal);
        for (int index = 0; index < items.Count; index++)
        {
            string normalized = Normalize(textSelector(items[index]));
            if (!IsBurstCandidate(normalized)
                && !IsTightShortBurstCandidate(normalized))
            {
                continue;
            }

            if (!occurrences.TryGetValue(normalized, out List<int>? indexes))
            {
                indexes = [];
                occurrences[normalized] = indexes;
            }

            indexes.Add(index);
        }

        var suppressedIndexes = new HashSet<int>();
        foreach ((string phrase, List<int> indexes) in occurrences)
        {
            bool isTightShortBurst = !IsBurstCandidate(phrase);
            int minimumRepetitions = isTightShortBurst
                ? MinimumTightShortBurstRepetitions
                : CountWords(phrase) <= 3
                    ? MinimumShortPhraseBurstRepetitions
                    : MinimumLongPhraseBurstRepetitions;
            TimeSpan window = isTightShortBurst
                ? TightBurstWindow
                : BurstWindow;
            int untimedWindowItems = isTightShortBurst
                ? UntimedTightBurstWindowItems
                : UntimedBurstWindowItems;
            for (int left = 0; left < indexes.Count;)
            {
                int right = left;
                while (right + 1 < indexes.Count
                       && IsInsideBurstWindow(
                           items,
                           indexes[left],
                           indexes[right + 1],
                           startSelector,
                           window,
                           untimedWindowItems))
                {
                    right++;
                }

                int repetitionCount = right - left + 1;
                if (repetitionCount < minimumRepetitions)
                {
                    left++;
                    continue;
                }

                int firstSuppressed = !isTightShortBurst
                                      && repetitionCount >= ExtremeBurstRepetitions
                    ? left
                    : left + 1;
                for (int occurrence = firstSuppressed; occurrence <= right; occurrence++)
                {
                    suppressedIndexes.Add(indexes[occurrence]);
                }

                left = right + 1;
            }
        }

        if (suppressedIndexes.Count == 0)
        {
            return new TranscriptFilterResult<T>(items, 0);
        }

        T[] kept = items
            .Where((_, index) => !suppressedIndexes.Contains(index))
            .ToArray();
        return new TranscriptFilterResult<T>(kept, suppressedIndexes.Count);
    }

    private static bool IsBurstCandidate(string normalized) =>
        normalized.Length >= 18 && CountWords(normalized) >= 2;

    private static bool IsTightShortBurstCandidate(string normalized) =>
        normalized.Length >= 2 && CountWords(normalized) <= 3;

    private static int CountWords(string normalized) =>
        normalized.Count(character => character == ' ') + 1;

    private static bool IsInsideBurstWindow<T>(
        IReadOnlyList<T> items,
        int firstIndex,
        int lastIndex,
        Func<T, TimeSpan>? startSelector)
        => IsInsideBurstWindow(
            items,
            firstIndex,
            lastIndex,
            startSelector,
            BurstWindow,
            UntimedBurstWindowItems);

    private static bool IsInsideBurstWindow<T>(
        IReadOnlyList<T> items,
        int firstIndex,
        int lastIndex,
        Func<T, TimeSpan>? startSelector,
        TimeSpan window,
        int untimedWindowItems)
    {
        if (startSelector is null)
        {
            return lastIndex - firstIndex <= untimedWindowItems;
        }

        TimeSpan elapsed = startSelector(items[lastIndex])
            - startSelector(items[firstIndex]);
        return elapsed >= TimeSpan.Zero && elapsed <= window;
    }

    private static int FindCycleLength(string[] text, int start)
    {
        int maximum = Math.Min(
            MaximumCycleLength,
            (text.Length - start) / MinimumCycleRepetitions);
        for (int length = 1; length <= maximum; length++)
        {
            if (BlocksMatch(text, start, start + length, length)
                && BlocksMatch(text, start, start + (2 * length), length))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool BlocksMatch(
        IReadOnlyList<string> text,
        int left,
        int right,
        int length)
    {
        for (int offset = 0; offset < length; offset++)
        {
            if (!IsSimilar(text[left + offset], text[right + offset]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSimilar(string left, string right)
    {
        if (left.Length < 8 || right.Length < 8)
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        HashSet<string> leftTokens = left
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> rightTokens = right
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return false;
        }

        int intersection = leftTokens.Count(rightTokens.Contains);
        int union = leftTokens.Count + rightTokens.Count - intersection;
        return intersection / (double)union >= 0.86;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool previousWhitespace = true;
        foreach (char character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWhitespace = false;
            }
            else if (!previousWhitespace)
            {
                builder.Append(' ');
                previousWhitespace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
