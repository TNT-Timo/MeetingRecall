using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Transcription;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class TranscriptExportFormatterTests
{
    [TestMethod]
    public void FormatMarkdown_UsesBoldSpeakerNamesAndCurrentText()
    {
        Guid speakerId = Guid.NewGuid();
        Speaker speaker = Speaker.CreateNativeMicrophoneOwner(
            speakerId,
            "Sana *Gast*");
        Conversation conversation = CreateConversation(
            speakerId,
            "Der überarbeitete Text");

        string exported = TranscriptExportFormatter.FormatMarkdown(
            conversation,
            new Dictionary<Guid, Speaker>
            {
                [speaker.Id] = speaker
            });

        StringAssert.Contains(exported, "# Beispielgespräch");
        StringAssert.Contains(exported, "**00:03 · Sana \\*Gast\\***");
        StringAssert.Contains(exported, "Der überarbeitete Text");
    }

    [TestMethod]
    public void FormatPlainText_RemainsReadableWithoutMarkdownSyntax()
    {
        Guid speakerId = Guid.NewGuid();
        Speaker speaker = Speaker.CreateUnknown(speakerId, 1);
        Conversation conversation = CreateConversation(
            speakerId,
            "Ein normaler Satz.");

        string exported = TranscriptExportFormatter.FormatPlainText(
            conversation,
            new Dictionary<Guid, Speaker>
            {
                [speaker.Id] = speaker
            });

        StringAssert.Contains(exported, "[00:03] Person 0001");
        StringAssert.Contains(exported, "Ein normaler Satz.");
        Assert.IsFalse(exported.Contains("**", StringComparison.Ordinal));
    }

    private static Conversation CreateConversation(
        Guid speakerId,
        string text)
    {
        var conversation = new Conversation(
            Guid.NewGuid(),
            "Beispielgespräch",
            new DateTimeOffset(2026, 8, 7, 12, 30, 0, TimeSpan.Zero),
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            speakerId,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(8),
            text));
        return conversation;
    }
}
