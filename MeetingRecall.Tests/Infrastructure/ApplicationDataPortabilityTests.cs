using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Backup;
using MeetingRecall.Core.Enums;
using MeetingRecall.Infrastructure.Backup;
using MeetingRecall.Infrastructure.Persistence;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class ApplicationDataPortabilityTests
{
    [TestMethod]
    public async Task EncryptedExportImport_RoundTripsCompletePersistentWorkspace()
    {
        string testRoot = CreateTemporaryDirectory();
        string dataRoot = Path.Combine(testRoot, "MeetingRecall");
        string backupPath = Path.Combine(testRoot, "complete.mrbackup");

        try
        {
            TestEnvironment environment = CreateEnvironment(dataRoot);
            Guid conversationId = Guid.NewGuid();
            SeedConversation(environment, conversationId, "Backup-Test");

            byte[] audioBytes = [1, 3, 3, 7, 9, 2, 4];
            string audioPath = Path.Combine(
                environment.Paths.GetMeetingAudioDirectory(conversationId),
                "primary.wav");
            WriteFile(audioPath, audioBytes);
            WriteFile(
                Path.Combine(environment.Paths.ModelsDirectory, "Whisper", "ggml-small.bin"),
                [8, 6, 7, 5, 3, 0, 9]);
            WriteText(
                Path.Combine(environment.Paths.RootDirectory, "Settings", "preferences.json"),
                "{\"language\":\"de\"}");
            WriteText(Path.Combine(environment.Paths.LogsDirectory, "session.log"), "lokales log");
            WriteText(Path.Combine(environment.Paths.ExportsDirectory, "notes.txt"), "exportierter inhalt");
            WriteText(Path.Combine(environment.Paths.TempDirectory, "ignored.tmp"), "nicht sichern");

            BackupOperationResult exported = await environment.Service.ExportAsync(
                backupPath,
                "sicheres-passwort");

            Assert.IsTrue(exported.Encrypted);
            Assert.IsTrue(File.Exists(backupPath));
            Assert.IsTrue(exported.FileCount >= 6);

            BackupInspection inspection = await environment.Service.InspectAsync(backupPath);
            Assert.IsTrue(inspection.RequiresPassword);
            Assert.IsNull(inspection.FileCount);

            await environment.Service.ResetAllAsync();
            Assert.IsFalse(await environment.Service.HasUserDataAsync());
            Assert.IsFalse(File.Exists(audioPath));

            BackupOperationResult imported = await environment.Service.ImportAsync(
                backupPath,
                "sicheres-passwort");

            Assert.IsTrue(imported.Encrypted);
            Assert.AreEqual(1L, CountConversations(environment));
            CollectionAssert.AreEqual(audioBytes, File.ReadAllBytes(audioPath));
            Assert.AreEqual(
                "{\"language\":\"de\"}",
                File.ReadAllText(Path.Combine(
                    environment.Paths.RootDirectory,
                    "Settings",
                    "preferences.json")));
            Assert.AreEqual(
                "lokales log",
                File.ReadAllText(Path.Combine(environment.Paths.LogsDirectory, "session.log")));
            Assert.AreEqual(
                "exportierter inhalt",
                File.ReadAllText(Path.Combine(environment.Paths.ExportsDirectory, "notes.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(environment.Paths.TempDirectory, "ignored.tmp")));
        }
        finally
        {
            DeleteDirectory(testRoot);
        }
    }

    [TestMethod]
    public async Task Import_WithWrongPassword_PreservesCurrentWorkspace()
    {
        string testRoot = CreateTemporaryDirectory();
        string sourceRoot = Path.Combine(testRoot, "Source");
        string currentRoot = Path.Combine(testRoot, "Current");
        string backupPath = Path.Combine(testRoot, "protected.mrbackup");

        try
        {
            TestEnvironment source = CreateEnvironment(sourceRoot);
            SeedConversation(source, Guid.NewGuid(), "Gesicherter Zustand");
            WriteText(Path.Combine(source.Paths.ModelsDirectory, "model.bin"), "backup-model");
            await source.Service.ExportAsync(backupPath, "richtiges-passwort");

            TestEnvironment current = CreateEnvironment(currentRoot);
            SeedConversation(current, Guid.NewGuid(), "Aktueller Zustand");
            string markerPath = Path.Combine(current.Paths.RootDirectory, "Settings", "marker.txt");
            WriteText(markerPath, "muss erhalten bleiben");

            UnauthorizedAccessException? captured = null;
            try
            {
                await current.Service.ImportAsync(backupPath, "falsches-passwort");
            }
            catch (UnauthorizedAccessException exception)
            {
                captured = exception;
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual(1L, CountConversations(current));
            Assert.AreEqual("muss erhalten bleiben", File.ReadAllText(markerPath));
        }
        finally
        {
            DeleteDirectory(testRoot);
        }
    }

    [TestMethod]
    public async Task UnencryptedExport_CanBeInspectedWithoutPassword()
    {
        string testRoot = CreateTemporaryDirectory();
        string backupPath = Path.Combine(testRoot, "plain.mrbackup");

        try
        {
            TestEnvironment environment = CreateEnvironment(Path.Combine(testRoot, "MeetingRecall"));
            SeedConversation(environment, Guid.NewGuid(), "Unverschlüsselt");

            BackupOperationResult result = await environment.Service.ExportAsync(backupPath);
            BackupInspection inspection = await environment.Service.InspectAsync(backupPath);

            Assert.IsFalse(result.Encrypted);
            Assert.IsFalse(inspection.RequiresPassword);
            Assert.AreEqual(result.FileCount, inspection.FileCount);
            Assert.AreEqual(result.TotalBytes, inspection.TotalBytes);
            Assert.AreEqual(SqliteSchema.CurrentVersion, inspection.SchemaVersion);
            Assert.IsNotNull(inspection.CreatedAtUtc);
        }
        finally
        {
            DeleteDirectory(testRoot);
        }
    }

    [TestMethod]
    public async Task ResetAll_RemovesAllPersistentDataAndRecreatesUsableDatabase()
    {
        string testRoot = CreateTemporaryDirectory();

        try
        {
            TestEnvironment environment = CreateEnvironment(Path.Combine(testRoot, "MeetingRecall"));
            SeedConversation(environment, Guid.NewGuid(), "Reset-Test");
            WriteText(Path.Combine(environment.Paths.ModelsDirectory, "model.bin"), "model");
            WriteText(Path.Combine(environment.Paths.LogsDirectory, "log.txt"), "log");

            Assert.IsTrue(await environment.Service.HasUserDataAsync());
            await environment.Service.ResetAllAsync();

            Assert.IsFalse(await environment.Service.HasUserDataAsync());
            Assert.AreEqual(0L, CountConversations(environment));
            Assert.IsTrue(File.Exists(environment.Paths.DatabaseFilePath));
            Assert.IsTrue(Directory.Exists(environment.Paths.MeetingsDirectory));
            Assert.IsTrue(Directory.Exists(environment.Paths.ModelsDirectory));
            Assert.IsTrue(Directory.Exists(environment.Paths.ExportsDirectory));
        }
        finally
        {
            DeleteDirectory(testRoot);
        }
    }

    private static TestEnvironment CreateEnvironment(string root)
    {
        var paths = new AppDataPaths(root);
        var connectionFactory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(paths, connectionFactory);
        var service = new ApplicationDataPortabilityService(
            paths,
            initializer,
            connectionFactory);
        initializer.Initialize();
        return new TestEnvironment(paths, connectionFactory, initializer, service);
    }

    private static void SeedConversation(
        TestEnvironment environment,
        Guid conversationId,
        string title)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteConnection connection = environment.ConnectionFactory.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations (
                id,
                title,
                recorded_at,
                recorded_at_utc_ticks,
                source,
                processing_state,
                duration_ticks,
                detected_language,
                created_at_utc,
                updated_at_utc)
            VALUES (
                @id,
                @title,
                @recordedAt,
                @recordedAtTicks,
                @source,
                @processingState,
                @durationTicks,
                NULL,
                @createdAt,
                @updatedAt);
            """;
        command.Parameters.AddWithValue("@id", conversationId.ToString("D"));
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@recordedAt", now.ToString("O"));
        command.Parameters.AddWithValue("@recordedAtTicks", now.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("@source", (int)ConversationSource.ImportedAudio);
        command.Parameters.AddWithValue("@processingState", (int)ConversationProcessingState.Ready);
        command.Parameters.AddWithValue("@durationTicks", TimeSpan.FromMinutes(2).Ticks);
        command.Parameters.AddWithValue("@createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("@updatedAt", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static long CountConversations(TestEnvironment environment)
    {
        using SqliteConnection connection = environment.ConnectionFactory.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversations;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void WriteFile(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static void WriteText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Windows can briefly retain file handles after a failed encrypted import.
        }
    }

    private sealed record TestEnvironment(
        AppDataPaths Paths,
        SqliteConnectionFactory ConnectionFactory,
        SqliteDatabaseInitializer Initializer,
        ApplicationDataPortabilityService Service);
}
