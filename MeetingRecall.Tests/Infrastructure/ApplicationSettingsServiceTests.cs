using MeetingRecall.Core.Settings;
using MeetingRecall.Infrastructure.Settings;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ApplicationSettingsServiceTests
{
    private string _rootDirectory = null!;

    [TestInitialize]
    public void SetUp()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall-tests",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Reload_MigratesLegacyLightThemeToDarkTheme()
    {
        var paths = new AppDataPaths(_rootDirectory);
        paths.EnsureCreated();
        File.WriteAllText(
            paths.ApplicationSettingsFilePath,
            """
            {
              "formatVersion": 1,
              "settings": {
                "language": 1,
                "theme": 1,
                "encryptBackupsByDefault": true
              }
            }
            """);

        var service = new ApplicationSettingsService(paths);

        ApplicationSettings settings = service.Reload();

        Assert.AreEqual(ApplicationTheme.Dark, settings.Theme);
        Assert.AreEqual(ApplicationLanguage.German, settings.Language);
        Assert.AreEqual(15, settings.SkipIntervalSeconds);
        Assert.IsFalse(settings.LiveTranscriptEnabled);
        Assert.IsTrue(settings.EncryptBackupsByDefault);
    }

    [TestMethod]
    public async Task SaveAsync_NormalizesLegacyLightThemeToDarkBeforePersisting()
    {
        var paths = new AppDataPaths(_rootDirectory);
        var service = new ApplicationSettingsService(paths);
        var legacySettings = new ApplicationSettings(
            ApplicationLanguage.English,
            ApplicationTheme.Light,
            EncryptBackupsByDefault: false)
        {
            SkipIntervalSeconds = 30,
            LiveTranscriptEnabled = true
        };

        await service.SaveAsync(legacySettings);

        Assert.AreEqual(ApplicationTheme.Dark, service.Current.Theme);
        Assert.AreEqual(30, service.Current.SkipIntervalSeconds);
        Assert.IsTrue(service.Current.LiveTranscriptEnabled);
        string json = await File.ReadAllTextAsync(paths.ApplicationSettingsFilePath);
        StringAssert.Contains(json, "\"theme\": 2");
        StringAssert.Contains(json, "\"skipIntervalSeconds\": 30");
        StringAssert.Contains(json, "\"liveTranscriptEnabled\": true");
        Assert.IsFalse(json.Contains("\"theme\": 1", StringComparison.Ordinal));
    }

}
