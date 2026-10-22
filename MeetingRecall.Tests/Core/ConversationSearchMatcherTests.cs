using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Search;

namespace MeetingRecall.Tests.Core;

[TestClass]
public sealed class ConversationSearchMatcherTests
{
    [TestMethod]
    public void ExactSearch_FindsAContinuousPhraseInTranscript()
    {
        Conversation conversation = CreateConversation(
            "Projektgespräch",
            "Sana bereitet die Testdaten bis Freitag vor.");

        Assert.IsTrue(ConversationSearchMatcher.IsMatch(
            conversation,
            "testdaten bis freitag",
            exactPhrase: true));
        Assert.IsFalse(ConversationSearchMatcher.IsMatch(
            conversation,
            "freitag testdaten",
            exactPhrase: true));
    }

    [TestMethod]
    public void ContentSearch_FindsWordsInAnyOrderAndSimpleWordForms()
    {
        Conversation conversation = CreateConversation(
            "Datenbankmigration",
            "Am Freitag werden die stabilen Testdaten vorbereitet.");

        Assert.IsTrue(ConversationSearchMatcher.IsMatch(
            conversation,
            "Testdaten Freitag",
            exactPhrase: false));
        Assert.IsTrue(ConversationSearchMatcher.IsMatch(
            conversation,
            "vorbereiten Datenbank",
            exactPhrase: false));
        Assert.IsFalse(ConversationSearchMatcher.IsMatch(
            conversation,
            "Budget Freitag",
            exactPhrase: false));
    }

    [TestMethod]
    public void Search_IsCaseAndAccentInsensitive()
    {
        Conversation conversation = CreateConversation(
            "Übergabe",
            "Die nächste Prüfung beginnt später.");

        Assert.IsTrue(ConversationSearchMatcher.IsMatch(
            conversation,
            "UBERGABE PRÜFUNG",
            exactPhrase: false));
    }

    private static Conversation CreateConversation(
        string title,
        string transcript)
    {
        var conversation = new Conversation(
            Guid.NewGuid(),
            title,
            DateTimeOffset.UtcNow,
            ConversationSource.ImportedAudio);
        conversation.AddTranscriptSegment(new TranscriptSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            transcript));
        return conversation;
    }
}
