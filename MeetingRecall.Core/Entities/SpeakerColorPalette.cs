namespace MeetingRecall.Core.Entities;

public static class SpeakerColorPalette
{
    private static readonly string[] Colors =
    [
        "#6F8FAF",
        "#729B7B",
        "#A58172",
        "#9279A6",
        "#B08A5A",
        "#568F8A",
        "#A06F82",
        "#7186B6",
        "#8D9068",
        "#5F93A0"
    ];

    public static IReadOnlyList<string> All => Colors;

    public static string ForSequence(int sequenceNumber)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber));
        }

        return Colors[(sequenceNumber - 1) % Colors.Length];
    }

    public static string Normalize(string colorHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colorHex);
        string normalized = colorHex.Trim().ToUpperInvariant();

        if (normalized.Length != 7
            || normalized[0] != '#'
            || !normalized.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A speaker color must use #RRGGBB format.", nameof(colorHex));
        }

        return normalized;
    }
}
