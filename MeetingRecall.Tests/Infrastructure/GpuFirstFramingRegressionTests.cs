using MeetingRecall.Infrastructure.Intelligence;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class GpuFirstFramingRegressionTests
{
    private const string ComedyFrame = """
        {
          "subject":"Witzeepisode mit Sahar",
          "contentType":"Comedy- und Witzevideo",
          "discourseMode":"Comedy/Witze",
          "purpose":"Zuschauerwitze erzählen und gemeinsam darauf reagieren",
          "dominantActivity":"Witze erzählen, erklären und kommentieren",
          "mainArc":"Die Gruppe erzählt und diskutiert über die Episode hinweg verschiedene Witze und Wortspiele.",
          "titleDirection":"Witzeepisode mit Sahar und chaotischen Reaktionen",
          "incidentalTopics":["eine angeblich tödliche Krankheit von Sahar als Witz-Setup"],
          "nonLiteralOrPerformedTopics":["Sahars angeblich tödliche Krankheit als Scherz"]
        }
        """;

    [TestMethod]
    public void ShouldRepairGlobalFraming_IncidentalComedyClaimWithSharedParticipantName_ReturnsTrue()
    {
        const string output = """
            {
              "title":{"text":"Sahar hat eine ernste, tödliche Krankheit","evidenceIds":["E001"]},
              "overview":{"text":"In einer Witzeepisode erzählen und kommentieren Sahar und die Gruppe verschiedene Witze.","evidenceIds":["E002"]}
            }
            """;

        Assert.IsTrue(
            GpuFirstLocalLanguageModelService.ShouldRepairGlobalFraming(
                output,
                ComedyFrame));
    }

    [TestMethod]
    public void ShouldRepairGlobalFraming_CorrectComedyFraming_ReturnsFalse()
    {
        const string output = """
            {
              "title":{"text":"Witzeepisode mit Sahar und chaotischen Reaktionen","evidenceIds":["E002"]},
              "overview":{"text":"In einer Witzeepisode erzählt und kommentiert die Gruppe gemeinsam verschiedene Witze und Wortspiele.","evidenceIds":["E002"]}
            }
            """;

        Assert.IsFalse(
            GpuFirstLocalLanguageModelService.ShouldRepairGlobalFraming(
                output,
                ComedyFrame));
    }
}
