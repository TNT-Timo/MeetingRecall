using MeetingRecall.Core.Intelligence;
using MeetingRecall.Infrastructure.DependencyInjection;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class GlobalEditorialMeetingIntelligenceServiceTests
{
    [TestMethod]
    public void ThinkingOverride_IsScopedToFinalEditorialFlow()
    {
        var paths = new AppDataPaths(Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall-EditorialThinkingTests",
            Guid.NewGuid().ToString("N")));
        using var manager = new SummaryLanguageModelManager(paths);

        Assert.IsTrue(manager.CurrentModelDisablesThinking);
        using (manager.EnableThinkingForCurrentFlow())
        {
            Assert.IsFalse(manager.CurrentModelDisablesThinking);
            using (manager.EnableThinkingForCurrentFlow())
            {
                Assert.IsFalse(manager.CurrentModelDisablesThinking);
            }

            Assert.IsFalse(manager.CurrentModelDisablesThinking);
        }

        Assert.IsTrue(manager.CurrentModelDisablesThinking);
    }

    [TestMethod]
    public void InferDiscourseMode_ExplicitWitzeEpisode_WinsOverLocalClaims()
    {
        const string transcript = """
            Sahar hat angeblich eine ernste Krankheit.
            Herzlich Willkommen zu einer neuen Witze Episode.
            Danach erzählen und diskutieren wir verschiedene Witze und Wortspiele.
            """;

        Assert.AreEqual(
            "Comedy/Witze",
            GlobalEditorialMeetingIntelligenceService.InferDiscourseMode(
                "Gespräch",
                transcript));
    }

    [TestMethod]
    public void TitleGuard_RejectsPunchlineButAcceptsEpisodeFraming()
    {
        Assert.IsFalse(
            GlobalEditorialMeetingIntelligenceService.TitleMatchesDiscourseMode(
                "Auf dem Grabstein eines Wanderers steht: Er ist zu weit gegangen",
                "Comedy/Witze"));
        Assert.IsTrue(
            GlobalEditorialMeetingIntelligenceService.TitleMatchesDiscourseMode(
                "Witzeepisode mit Wortspielen und chaotischen Reaktionen",
                "Comedy/Witze"));
    }

    [TestMethod]
    public void NonLiteralGuard_RejectsMedicalJokeAsParticipantFact()
    {
        const string context =
            "Wir machen heute eine neue Witze Episode. Sahar hat eine tödliche Krankheit. "
            + "Danach wird gelacht und der nächste Witz erzählt.";

        Assert.IsTrue(
            GlobalEditorialMeetingIntelligenceService.IsUnsafeLiteralClaim(
                "Comedy/Witze",
                "literal",
                "Sahar muss zweimal täglich Medikamente gegen eine tödliche Krankheit einnehmen.",
                context));
        Assert.IsFalse(
            GlobalEditorialMeetingIntelligenceService.IsUnsafeLiteralClaim(
                "Comedy/Witze",
                "performed",
                "In einem Witz wird behauptet, Sahar müsse wegen einer tödlichen Krankheit Medikamente nehmen.",
                context));
    }

    [TestMethod]
    public void DuplicateGuard_CollapsesEquivalentPresenceStatements()
    {
        Assert.IsTrue(
            GlobalEditorialMeetingIntelligenceService.AreSemanticallyDuplicate(
                "Sixpack ist während des Gesprächs anwesend und beteiligt.",
                "Sixpack ist im Gespräch präsent."));
    }

    [TestMethod]
    public void InfrastructureRegistration_ProductionUsesGlobalEditorialWrapper()
    {
        var services = new ServiceCollection();
        services.AddMeetingRecallInfrastructure(Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall-EditorialRegistrationTests",
            Guid.NewGuid().ToString("N")));

        using ServiceProvider provider = services.BuildServiceProvider();
        ILocalMeetingIntelligenceService service =
            provider.GetRequiredService<ILocalMeetingIntelligenceService>();

        Assert.IsInstanceOfType<GlobalEditorialMeetingIntelligenceService>(service);
        Assert.IsTrue(services.Any(item =>
            item.ServiceType == typeof(LocalMeetingIntelligenceService)));
    }
}
