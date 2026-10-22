using System.Globalization;
using System.Text;
using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Search;

public static class ConversationSearchMatcher
{
    public static bool IsMatch(
        Conversation conversation,
        string? query,
        bool exactPhrase)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        string normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        string searchableText = Normalize(string.Join(
            '\n',
            conversation.Title,
            conversation.DetectedLanguage ?? string.Empty,
            string.Join(
                '\n',
                conversation.TranscriptSegments.Select(
                    segment => segment.CurrentText))));

        if (exactPhrase)
        {
            return searchableText.Contains(
                normalizedQuery,
                StringComparison.Ordinal);
        }

        string[] queryTokens = Tokenize(normalizedQuery);
        string[] searchableTokens = Tokenize(searchableText);
        return queryTokens.All(queryToken =>
            searchableTokens.Any(searchableToken =>
                TokensMatch(queryToken, searchableToken)));
    }

    private static bool TokensMatch(string queryToken, string searchableToken)
    {
        if (string.Equals(
                queryToken,
                searchableToken,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (queryToken.Length >= 4
            && searchableToken.Length >= 4
            && (searchableToken.StartsWith(
                    queryToken,
                    StringComparison.Ordinal)
                || queryToken.StartsWith(
                    searchableToken,
                    StringComparison.Ordinal)))
        {
            return true;
        }

        int commonPrefixLength = 0;
        int maximumPrefixLength = Math.Min(
            queryToken.Length,
            searchableToken.Length);
        while (commonPrefixLength < maximumPrefixLength
               && queryToken[commonPrefixLength]
               == searchableToken[commonPrefixLength])
        {
            commonPrefixLength++;
        }

        return commonPrefixLength >= 5
            && maximumPrefixLength - commonPrefixLength <= 2;
    }

    private static string[] Tokenize(string value)
    {
        return value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool previousWasSeparator = true;
        foreach (char character in value.Normalize(
                     NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }
}
