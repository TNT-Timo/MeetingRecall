namespace MeetingRecall.Infrastructure.Storage;

public sealed class AppDataPaths
{
    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRootDirectory());
        DatabaseDirectory = Path.Combine(RootDirectory, "Database");
        MeetingsDirectory = Path.Combine(RootDirectory, "Meetings");
        ModelsDirectory = Path.Combine(RootDirectory, "Models");
        SettingsDirectory = Path.Combine(RootDirectory, "Settings");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        TempDirectory = Path.Combine(RootDirectory, "Temp");
        ExportsDirectory = Path.Combine(RootDirectory, "Exports");
        DatabaseFilePath = Path.Combine(DatabaseDirectory, "MeetingRecall.db");
        ApplicationSettingsFilePath = Path.Combine(SettingsDirectory, "application-settings.json");
    }

    public string RootDirectory { get; }

    public string DatabaseDirectory { get; }

    public string MeetingsDirectory { get; }

    public string ModelsDirectory { get; }

    public string SettingsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string ExportsDirectory { get; }

    public string DatabaseFilePath { get; }

    public string ApplicationSettingsFilePath { get; }

    public string GetMeetingDirectory(Guid conversationId)
    {
        ValidateIdentifier(conversationId);
        return Path.Combine(MeetingsDirectory, conversationId.ToString("N"));
    }

    public string GetMeetingAudioDirectory(Guid conversationId)
    {
        return Path.Combine(GetMeetingDirectory(conversationId), "Audio");
    }

    public string ToRelativePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string fullPath = Path.GetFullPath(absolutePath);
        string relativePath = Path.GetRelativePath(RootDirectory, fullPath);
        EnsureSafeRelativePath(relativePath);
        return relativePath.Replace('\\', '/');
    }

    public string ToAbsolutePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        EnsureSafeRelativePath(relativePath);

        string combined = Path.GetFullPath(Path.Combine(
            RootDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path escapes the MeetingRecall data directory.", nameof(relativePath));
        }

        return combined;
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(MeetingsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }

    private static string GetDefaultRootDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not provide a local application-data directory.");
        }

        return Path.Combine(localAppData, "MeetingRecall");
    }

    private static void EnsureSafeRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The path must be relative to the MeetingRecall data directory.", nameof(relativePath));
        }

        string[] segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The path escapes the MeetingRecall data directory.", nameof(relativePath));
        }
    }

    private static void ValidateIdentifier(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", nameof(id));
        }
    }
}
