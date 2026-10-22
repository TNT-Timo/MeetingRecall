using System.Globalization;
using System.Text;
using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Transcription;

public static class TranscriptExportFormatter
{
    public static string FormatPlainText(
        Conversation conversation,
        IReadOnlyDictionary<Guid, Speaker> speakers)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(speakers);

        var output = new StringBuilder();
        output.AppendLine(conversation.Title);
        output.AppendLine(
            $"Aufgenommen: {conversation.RecordedAt:dd.MM.yyyy HH:mm}");
        output.AppendLine();

        foreach (TranscriptSegment segment in conversation.TranscriptSegments)
        {
            output.Append('[');
            output.Append(FormatTimestamp(segment.StartTime));
            output.Append("] ");
            output.AppendLine(ResolveSpeakerName(segment, speakers));
            output.AppendLine(segment.CurrentText);
            output.AppendLine();
        }

        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatMarkdown(
        Conversation conversation,
        IReadOnlyDictionary<Guid, Speaker> speakers)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(speakers);

        var output = new StringBuilder();
        output.Append("# ");
        output.AppendLine(EscapeMarkdown(conversation.Title));
        output.AppendLine();
        output.Append("_Aufgenommen: ");
        output.Append(conversation.RecordedAt.ToString(
            "dd.MM.yyyy HH:mm",
            CultureInfo.CurrentCulture));
        output.AppendLine("_");
        output.AppendLine();

        foreach (TranscriptSegment segment in conversation.TranscriptSegments)
        {
            output.Append("**");
            output.Append(FormatTimestamp(segment.StartTime));
            output.Append(" · ");
            output.Append(EscapeMarkdown(
                ResolveSpeakerName(segment, speakers)));
            output.AppendLine("**");
            output.AppendLine();
            output.AppendLine(EscapeMarkdown(segment.CurrentText));
            output.AppendLine();
        }

        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string ResolveSpeakerName(
        TranscriptSegment segment,
        IReadOnlyDictionary<Guid, Speaker> speakers) =>
        speakers.TryGetValue(segment.SpeakerId, out Speaker? speaker)
            ? speaker.DisplayName
            : "Person";

    private static string FormatTimestamp(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal);
}
