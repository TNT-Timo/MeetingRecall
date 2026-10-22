using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class AppDataPathsTests
{
    [TestMethod]
    public void EnsureCreated_CreatesCompleteWorkspace()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new AppDataPaths(root);

            paths.EnsureCreated();

            Assert.IsTrue(Directory.Exists(paths.RootDirectory));
            Assert.IsTrue(Directory.Exists(paths.DatabaseDirectory));
            Assert.IsTrue(Directory.Exists(paths.MeetingsDirectory));
            Assert.IsTrue(Directory.Exists(paths.ModelsDirectory));
            Assert.IsTrue(Directory.Exists(paths.LogsDirectory));
            Assert.IsTrue(Directory.Exists(paths.TempDirectory));
            Assert.IsTrue(Directory.Exists(paths.ExportsDirectory));
            Assert.AreEqual(
                Path.Combine(paths.DatabaseDirectory, "MeetingRecall.db"),
                paths.DatabaseFilePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
