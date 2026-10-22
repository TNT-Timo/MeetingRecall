using MeetingRecall.Core.Intelligence;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Infrastructure.Models;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class LocalModelBundleTests
{
    [TestMethod]
    public void BundleIncludesEverySupportedRuntimeModel()
    {
        var paths = new AppDataPaths(Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall-ModelBundleTests",
            Guid.NewGuid().ToString("N")));
        var whisperModels = new WhisperModelManager(paths);
        using var speakerModels = new SpeakerDiarizationModelManager(paths);
        using var languageModels = new SummaryLanguageModelManager(paths);
        var bundle = new LocalModelBundleService(
            whisperModels,
            speakerModels,
            languageModels);

        Assert.AreEqual(4, bundle.ModelCount);
        Assert.AreEqual(12_199_731_936, bundle.TotalDownloadSizeBytes);
        Assert.IsFalse(bundle.AreAllModelsInstalled);
    }
}
