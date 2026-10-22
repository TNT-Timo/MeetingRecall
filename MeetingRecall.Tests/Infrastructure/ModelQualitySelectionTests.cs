using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class FixedModelSelectionTests
{
    [TestMethod]
    public void ServicesExposeOnlyTheFixedModels()
    {
        var paths = new AppDataPaths(Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall-ModelSelectionTests",
            Guid.NewGuid().ToString("N")));
        using var languageModel = new QwenLocalLanguageModelService(paths);
        var whisperModel = new WhisperModelManager(paths);

        Assert.AreEqual(
            "Whisper Large-v3 (mehrsprachig)",
            whisperModel.ModelDisplayName);
        Assert.AreEqual("Qwen3-14B Q4_K_M", languageModel.ModelDisplayName);
        Assert.AreEqual(9_001_752_960, languageModel.ModelDownloadSizeBytes);
    }
}
