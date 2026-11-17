namespace MeetingRecall.Core.Backup;

public enum DataPortabilityStage
{
    Inspecting = 0,
    Snapshotting = 1,
    Hashing = 2,
    Packaging = 3,
    Validating = 4,
    Restoring = 5,
    Resetting = 6,
    Completed = 7
}

public sealed record DataPortabilityProgress(
    DataPortabilityStage Stage,
    double? Fraction = null,
    int ProcessedFiles = 0,
    int TotalFiles = 0,
    string? CurrentItem = null);

public sealed record BackupInspection(
    bool RequiresPassword,
    DateTimeOffset? CreatedAtUtc,
    string? ApplicationVersion,
    int? SchemaVersion,
    int? FileCount,
    long? TotalBytes);

public sealed record BackupOperationResult(
    DateTimeOffset CreatedAtUtc,
    int FileCount,
    long TotalBytes,
    bool Encrypted);

public interface IApplicationDataPortabilityService
{
    Task<bool> HasUserDataAsync(CancellationToken cancellationToken = default);

    Task<BackupInspection> InspectAsync(
        string backupPath,
        CancellationToken cancellationToken = default);

    Task<BackupOperationResult> ExportAsync(
        string destinationPath,
        string? password = null,
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<BackupOperationResult> ImportAsync(
        string backupPath,
        string? password = null,
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ResetAllAsync(
        IProgress<DataPortabilityProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
