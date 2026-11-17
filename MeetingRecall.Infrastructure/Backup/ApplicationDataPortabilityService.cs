using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Backup;
using MeetingRecall.Infrastructure.Persistence;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Backup;

internal sealed class ApplicationDataPortabilityService(
    AppDataPaths paths,
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory) : IApplicationDataPortabilityService
{
    private const int CurrentBackupFormatVersion = 1;
    private const string ProductName = "MeetingRecall";
    private const string ManifestEntryName = "manifest.json";
    private const string PayloadPrefix = "payload/";
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<bool> HasUserDataAsync(CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();
        cancellationToken.ThrowIfCancellationRequested();

        using (SqliteConnection connection = connectionFactory.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM conversations)
                  + (SELECT COUNT(*) FROM conversation_groups)
                  + (SELECT COUNT(*) FROM speakers)
                  + (SELECT COUNT(*) FROM audio_tracks);
                """;
            long rowCount = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (rowCount > 0)
            {
                return true;
            }
        }

        return EnumeratePersistentSourceFiles()
            .Any(file => !IsDatabaseFile(file.RelativePath));
    }

    public Task<BackupInspection> InspectAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ValidateFilePath(backupPath, nameof(backupPath));
        cancellationToken.ThrowIfCancellationRequested();

        using var archive = OpenArchive(backupPath);
        ZipEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Die Datei enthält kein MeetingRecall-Manifest.");

        if (manifestEntry.IsCrypted)
        {
            return Task.FromResult(new BackupInspection(
                RequiresPassword: true,
                CreatedAtUtc: null,
                ApplicationVersion: null,
                SchemaVersion: null,
                FileCount: null,
                TotalBytes: null));
        }

        BackupManifest manifest = ReadManifest(archive, manifestEntry, password: null);
        ValidateManifest(manifest);
        return Task.FromResult(ToInspection(manifest, requiresPassword: false));
    }

    public async Task<BackupOperationResult> ExportAsync(
        string destinationPath,
        string? password = null,
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFilePath(destinationPath, nameof(destinationPath));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? snapshotRoot = null;
        string? validationRoot = null;
        string partialPath = destinationPath + ".partial";
        try
        {
            databaseInitializer.Initialize();
            progress?.Report(new DataPortabilityProgress(DataPortabilityStage.Snapshotting));

            snapshotRoot = CreateSiblingWorkingDirectory("snapshot");
            await CreateConsistentSnapshotAsync(snapshotRoot, progress, cancellationToken).ConfigureAwait(false);

            SnapshotFile[] files = await BuildSnapshotInventoryAsync(
                snapshotRoot,
                progress,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
            var manifest = new BackupManifest(
                CurrentBackupFormatVersion,
                ProductName,
                GetApplicationVersion(),
                SqliteSchema.CurrentVersion,
                createdAtUtc,
                files.Length,
                files.Sum(file => file.SizeBytes),
                files.Select(file => new BackupFileEntry(
                    file.RelativePath,
                    file.SizeBytes,
                    file.Sha256)).ToArray());

            string? destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            TryDeleteFile(partialPath);
            await WriteArchiveAsync(
                partialPath,
                snapshotRoot,
                manifest,
                NormalizePassword(password),
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new DataPortabilityProgress(DataPortabilityStage.Validating));
            validationRoot = CreateSiblingWorkingDirectory("validation");
            BackupManifest validatedManifest = await ExtractAndValidateArchiveAsync(
                partialPath,
                NormalizePassword(password),
                validationRoot,
                progress,
                cancellationToken).ConfigureAwait(false);

            File.Move(partialPath, destinationPath, overwrite: true);
            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Completed,
                1,
                validatedManifest.FileCount,
                validatedManifest.FileCount));

            return ToResult(validatedManifest, !string.IsNullOrWhiteSpace(password));
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(snapshotRoot);
            TryDeleteDirectory(validationRoot);
            _operationGate.Release();
        }
    }

    public async Task<BackupOperationResult> ImportAsync(
        string backupPath,
        string? password = null,
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFilePath(backupPath, nameof(backupPath));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? stagingRoot = null;
        string? rollbackRoot = null;
        bool currentRootMoved = false;
        bool stagingRootActivated = false;
        try
        {
            progress?.Report(new DataPortabilityProgress(DataPortabilityStage.Inspecting));
            stagingRoot = CreateSiblingWorkingDirectory("restore");
            BackupManifest manifest = await ExtractAndValidateArchiveAsync(
                backupPath,
                NormalizePassword(password),
                stagingRoot,
                progress,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DataPortabilityProgress(DataPortabilityStage.Restoring));
            CheckpointDatabase();

            rollbackRoot = CreateSiblingPath("rollback");
            TryDeleteDirectory(rollbackRoot);

            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Move(paths.RootDirectory, rollbackRoot);
                currentRootMoved = true;
            }

            Directory.Move(stagingRoot, paths.RootDirectory);
            stagingRootActivated = true;
            stagingRoot = null;

            databaseInitializer.Reinitialize();
            ValidateDatabase(paths.DatabaseFilePath);
            TryDeleteDirectory(rollbackRoot);
            rollbackRoot = null;

            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Completed,
                1,
                manifest.FileCount,
                manifest.FileCount));
            return ToResult(manifest, IsArchiveEncrypted(backupPath));
        }
        catch
        {
            if (stagingRootActivated)
            {
                TryDeleteDirectory(paths.RootDirectory);
            }

            if (currentRootMoved && !string.IsNullOrWhiteSpace(rollbackRoot) && Directory.Exists(rollbackRoot))
            {
                Directory.Move(rollbackRoot, paths.RootDirectory);
                rollbackRoot = null;
                try
                {
                    databaseInitializer.Reinitialize();
                }
                catch
                {
                    // The original import exception remains the primary failure.
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(rollbackRoot);
            _operationGate.Release();
        }
    }

    public async Task ResetAllAsync(
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? rollbackRoot = null;
        var movedEntries = new List<RootEntryMove>();
        bool allEntriesMoved = false;
        bool resetCompleted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DataPortabilityProgress(DataPortabilityStage.Resetting));
            CheckpointDatabase();

            rollbackRoot = CreateSiblingPath("reset-rollback");
            TryDeleteDirectory(rollbackRoot);
            Directory.CreateDirectory(rollbackRoot);
            MoveRootContentsToRollback(
                paths.RootDirectory,
                rollbackRoot,
                movedEntries);
            allEntriesMoved = true;

            paths.EnsureCreated();
            databaseInitializer.Reinitialize();
            ValidateDatabase(paths.DatabaseFilePath);
            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Completed,
                1));
            resetCompleted = true;
            // The reset is committed once the fresh database is validated.
            // Cleanup is deliberately best-effort: a virus scanner or a stale
            // model handle must not turn a completed destructive reset into a
            // failed operation and trigger an unsafe rollback.
            TryDeleteDirectory(rollbackRoot);
            rollbackRoot = null;
        }
        catch (Exception resetException)
        {
            try
            {
                if (movedEntries.Count > 0)
                {
                    if (allEntriesMoved)
                    {
                        DeleteDirectoryContentsStrict(paths.RootDirectory);
                    }

                    RestoreRootContents(movedEntries);
                    databaseInitializer.Reinitialize();
                    if (!string.IsNullOrWhiteSpace(rollbackRoot)
                        && Directory.Exists(rollbackRoot))
                    {
                        Directory.Delete(rollbackRoot, recursive: false);
                    }

                    rollbackRoot = null;
                }
            }
            catch (Exception restoreException)
            {
                throw new InvalidOperationException(
                    "MeetingRecall konnte nicht zurückgesetzt werden und der vorherige Zustand konnte nicht vollständig automatisch wiederhergestellt werden. Der Sicherungsordner wurde absichtlich nicht gelöscht.",
                    new AggregateException(resetException, restoreException));
            }

            throw;
        }
        finally
        {
            if (resetCompleted)
            {
                TryDeleteDirectory(rollbackRoot);
            }

            _operationGate.Release();
        }
    }

    private async Task CreateConsistentSnapshotAsync(
        string snapshotRoot,
        IProgress<DataPortabilityProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(snapshotRoot);
        foreach (string directoryName in new[] { "Database", "Meetings", "Models", "Logs", "Temp", "Exports" })
        {
            Directory.CreateDirectory(Path.Combine(snapshotRoot, directoryName));
        }

        PersistentSourceFile[] sourceFiles = EnumeratePersistentSourceFiles()
            .Where(file => !IsDatabaseFile(file.RelativePath))
            .ToArray();

        for (int index = 0; index < sourceFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistentSourceFile source = sourceFiles[index];
            string destination = ToSafeDestination(snapshotRoot, source.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileAsync(source.AbsolutePath, destination, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Snapshotting,
                sourceFiles.Length == 0 ? 1 : (index + 1d) / sourceFiles.Length,
                index + 1,
                sourceFiles.Length,
                source.RelativePath));
        }

        string snapshotDatabase = Path.Combine(snapshotRoot, "Database", "MeetingRecall.db");
        CreateDatabaseSnapshot(snapshotDatabase);
    }

    private void CreateDatabaseSnapshot(string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        TryDeleteFile(destinationPath);

        using SqliteConnection source = connectionFactory.OpenConnection();
        using (SqliteCommand checkpoint = source.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(FULL);";
            checkpoint.ExecuteNonQuery();
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        using var destination = new SqliteConnection(builder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private async Task<SnapshotFile[]> BuildSnapshotInventoryAsync(
        string snapshotRoot,
        IProgress<DataPortabilityProgress>? progress,
        CancellationToken cancellationToken)
    {
        string[] files = Directory
            .EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new SnapshotFile[files.Length];

        for (int index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(snapshotRoot, files[index]));
            string sha256 = await ComputeSha256Async(files[index], cancellationToken).ConfigureAwait(false);
            long size = new FileInfo(files[index]).Length;
            result[index] = new SnapshotFile(files[index], relativePath, size, sha256);

            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Hashing,
                files.Length == 0 ? 1 : (index + 1d) / files.Length,
                index + 1,
                files.Length,
                relativePath));
        }

        return result;
    }

    private static async Task WriteArchiveAsync(
        string archivePath,
        string snapshotRoot,
        BackupManifest manifest,
        string? password,
        IProgress<DataPortabilityProgress>? progress,
        CancellationToken cancellationToken)
    {
        bool encrypted = !string.IsNullOrWhiteSpace(password);
        await using var fileStream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipOutputStream(fileStream)
        {
            IsStreamOwner = false,
            Password = password
        };
        archive.SetLevel(3);

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var manifestEntry = new ZipEntry(ManifestEntryName)
        {
            DateTime = DateTime.Now,
            Size = manifestBytes.Length,
            AESKeySize = encrypted ? 256 : 0
        };
        archive.PutNextEntry(manifestEntry);
        await archive.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
        archive.CloseEntry();

        for (int index = 0; index < manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupFileEntry file = manifest.Files[index];
            string sourcePath = ToSafeDestination(snapshotRoot, file.RelativePath);
            var entry = new ZipEntry(PayloadPrefix + file.RelativePath)
            {
                DateTime = File.GetLastWriteTime(sourcePath),
                Size = file.SizeBytes,
                AESKeySize = encrypted ? 256 : 0
            };
            archive.PutNextEntry(entry);
            await using (var input = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(archive, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            }

            archive.CloseEntry();
            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Packaging,
                manifest.FileCount == 0 ? 1 : (index + 1d) / manifest.FileCount,
                index + 1,
                manifest.FileCount,
                file.RelativePath));
        }

        archive.Finish();
        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BackupManifest> ExtractAndValidateArchiveAsync(
        string archivePath,
        string? password,
        string destinationRoot,
        IProgress<DataPortabilityProgress>? progress,
        CancellationToken cancellationToken)
    {
        TryDeleteDirectory(destinationRoot);
        Directory.CreateDirectory(destinationRoot);

        using ZipFile archive = OpenArchive(archivePath);
        ZipEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Die Datei enthält kein MeetingRecall-Manifest.");
        if (manifestEntry.IsCrypted && string.IsNullOrWhiteSpace(password))
        {
            throw new UnauthorizedAccessException("Diese Sicherung ist passwortgeschützt.");
        }

        archive.Password = password;
        BackupManifest manifest = ReadManifest(archive, manifestEntry, password);
        ValidateManifest(manifest);

        for (int index = 0; index < manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupFileEntry expected = manifest.Files[index];
            ZipEntry entry = archive.GetEntry(PayloadPrefix + expected.RelativePath)
                ?? throw new InvalidDataException($"Die Sicherungsdatei fehlt: {expected.RelativePath}");
            if (!entry.IsFile)
            {
                throw new InvalidDataException($"Der Sicherungseintrag ist keine Datei: {expected.RelativePath}");
            }

            string destinationPath = ToSafeDestination(destinationRoot, expected.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            try
            {
                using Stream input = archive.GetInputStream(entry);
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    copied += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (copied != expected.SizeBytes
                    || !string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Prüfsummenfehler bei {expected.RelativePath}.");
                }
            }
            catch (ZipException exception) when (manifestEntry.IsCrypted)
            {
                throw new UnauthorizedAccessException(
                    "Das Sicherungspasswort ist falsch oder die Datei wurde beschädigt.",
                    exception);
            }

            progress?.Report(new DataPortabilityProgress(
                DataPortabilityStage.Validating,
                manifest.FileCount == 0 ? 1 : (index + 1d) / manifest.FileCount,
                index + 1,
                manifest.FileCount,
                expected.RelativePath));
        }

        ValidateDatabase(Path.Combine(destinationRoot, "Database", "MeetingRecall.db"));
        return manifest;
    }

    private IEnumerable<PersistentSourceFile> EnumeratePersistentSourceFiles()
    {
        if (!Directory.Exists(paths.RootDirectory))
        {
            yield break;
        }

        foreach (string filePath in Directory.EnumerateFiles(paths.RootDirectory, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(filePath);
            }
            catch
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string relativePath = NormalizeRelativePath(Path.GetRelativePath(paths.RootDirectory, filePath));
            if (ShouldExclude(relativePath))
            {
                continue;
            }

            yield return new PersistentSourceFile(filePath, relativePath);
        }
    }

    private static bool ShouldExclude(string relativePath)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        if (string.Equals(segments[0], "Temp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = segments[^1];
        if (fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(segments[0], "Exports", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".mrbackup", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDatabaseFile(string relativePath)
    {
        return string.Equals(
            NormalizeRelativePath(relativePath),
            "Database/MeetingRecall.db",
            StringComparison.OrdinalIgnoreCase);
    }

    private void CheckpointDatabase()
    {
        databaseInitializer.Initialize();
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    private static void ValidateDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            throw new InvalidDataException("Die Sicherung enthält keine MeetingRecall-Datenbank.");
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            string? result = Convert.ToString(integrity.ExecuteScalar());
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Die gesicherte Datenbank ist beschädigt: {result}");
            }
        }

        using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        int schemaVersion = Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion < 1 || schemaVersion > SqliteSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Die Datenbankschemaversion {schemaVersion} wird nicht unterstützt.");
        }
    }

    private static BackupManifest ReadManifest(
        ZipFile archive,
        ZipEntry manifestEntry,
        string? password)
    {
        if (manifestEntry.IsCrypted && string.IsNullOrWhiteSpace(password))
        {
            throw new UnauthorizedAccessException("Diese Sicherung ist passwortgeschützt.");
        }

        archive.Password = password;
        try
        {
            using Stream manifestStream = archive.GetInputStream(manifestEntry);
            return JsonSerializer.Deserialize<BackupManifest>(manifestStream, JsonOptions)
                ?? throw new InvalidDataException("Das Sicherungsmanifest ist leer.");
        }
        catch (ZipException exception) when (manifestEntry.IsCrypted)
        {
            throw new UnauthorizedAccessException(
                "Das Sicherungspasswort ist falsch oder die Datei wurde beschädigt.",
                exception);
        }
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != CurrentBackupFormatVersion)
        {
            throw new InvalidDataException(
                $"Die Sicherungsformatversion {manifest.FormatVersion} wird nicht unterstützt.");
        }

        if (!string.Equals(manifest.Product, ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Die Datei ist keine MeetingRecall-Sicherung.");
        }

        if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > SqliteSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Die Sicherung verwendet die nicht unterstützte Datenbankschemaversion {manifest.SchemaVersion}.");
        }

        if (manifest.FileCount != manifest.Files.Count
            || manifest.TotalBytes != manifest.Files.Sum(file => file.SizeBytes))
        {
            throw new InvalidDataException("Die Dateistatistik des Sicherungsmanifests ist inkonsistent.");
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BackupFileEntry file in manifest.Files)
        {
            string normalized = NormalizeRelativePath(file.RelativePath);
            EnsureSafeRelativePath(normalized);
            if (!uniquePaths.Add(normalized))
            {
                throw new InvalidDataException($"Die Sicherung enthält den Pfad mehrfach: {normalized}");
            }

            if (file.SizeBytes < 0
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"Ungültige Dateiinformationen für {normalized}.");
            }
        }

        if (!uniquePaths.Contains("Database/MeetingRecall.db"))
        {
            throw new InvalidDataException("Die Sicherung enthält keine MeetingRecall-Datenbank.");
        }
    }

    private static ZipFile OpenArchive(string backupPath)
    {
        try
        {
            return new ZipFile(File.Open(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch (Exception exception) when (exception is not FileNotFoundException)
        {
            throw new InvalidDataException("Die Sicherungsdatei konnte nicht geöffnet werden.", exception);
        }
    }

    private static bool IsArchiveEncrypted(string backupPath)
    {
        using ZipFile archive = OpenArchive(backupPath);
        return archive.GetEntry(ManifestEntryName)?.IsCrypted == true;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CreateSiblingWorkingDirectory(string purpose)
    {
        string path = CreateSiblingPath(purpose);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateSiblingPath(string purpose)
    {
        string fullRoot = Path.GetFullPath(paths.RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetDirectoryName(fullRoot)
            ?? throw new InvalidOperationException("Der MeetingRecall-Datenordner besitzt keinen übergeordneten Ordner.");
        string rootName = Path.GetFileName(fullRoot);
        return Path.Combine(parent, $".{rootName}.{purpose}-{Guid.NewGuid():N}");
    }

    private static void MoveRootContentsToRollback(
        string rootDirectory,
        string rollbackDirectory,
        ICollection<RootEntryMove> movedEntries)
    {
        Directory.CreateDirectory(rootDirectory);
        string[] entries = Directory
            .EnumerateFileSystemEntries(rootDirectory)
            .ToArray();
        foreach (string sourcePath in entries)
        {
            string destinationPath = Path.Combine(
                rollbackDirectory,
                Path.GetFileName(sourcePath));
            bool isDirectory = Directory.Exists(sourcePath);
            if (isDirectory)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }

            movedEntries.Add(new RootEntryMove(
                sourcePath,
                destinationPath,
                isDirectory));
        }
    }

    private static void RestoreRootContents(
        IReadOnlyList<RootEntryMove> movedEntries)
    {
        foreach (RootEntryMove entry in movedEntries.Reverse())
        {
            if (entry.IsDirectory)
            {
                Directory.Move(entry.RollbackPath, entry.OriginalPath);
            }
            else
            {
                File.Move(entry.RollbackPath, entry.OriginalPath);
            }
        }
    }

    private static void DeleteDirectoryContentsStrict(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            Directory.CreateDirectory(rootDirectory);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(rootDirectory))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static string ToSafeDestination(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        EnsureSafeRelativePath(normalized);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Ein Sicherungspfad verlässt den zulässigen Datenordner.");
        }

        return destination;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static void EnsureSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Ein Sicherungspfad ist ungültig.");
        }

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Ein Sicherungspfad ist unsicher.");
        }
    }

    private static string? NormalizePassword(string? password)
    {
        return string.IsNullOrWhiteSpace(password) ? null : password;
    }

    private static string GetApplicationVersion()
    {
        return typeof(ApplicationDataPortabilityService).Assembly.GetName().Version?.ToString()
            ?? "0.0.0.0";
    }

    private static BackupInspection ToInspection(BackupManifest manifest, bool requiresPassword)
    {
        return new BackupInspection(
            requiresPassword,
            manifest.CreatedAtUtc,
            manifest.ApplicationVersion,
            manifest.SchemaVersion,
            manifest.FileCount,
            manifest.TotalBytes);
    }

    private static BackupOperationResult ToResult(BackupManifest manifest, bool encrypted)
    {
        return new BackupOperationResult(
            manifest.CreatedAtUtc,
            manifest.FileCount,
            manifest.TotalBytes,
            encrypted);
    }

    private static void ValidateFilePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort; the primary operation reports its own error.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; abandoned staging folders are harmless.
        }
    }

    private sealed record PersistentSourceFile(string AbsolutePath, string RelativePath);

    private sealed record RootEntryMove(
        string OriginalPath,
        string RollbackPath,
        bool IsDirectory);

    private sealed record SnapshotFile(
        string AbsolutePath,
        string RelativePath,
        long SizeBytes,
        string Sha256);

    private sealed record BackupManifest(
        int FormatVersion,
        string Product,
        string ApplicationVersion,
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<BackupFileEntry> Files);

    private sealed record BackupFileEntry(
        string RelativePath,
        long SizeBytes,
        string Sha256);
}
