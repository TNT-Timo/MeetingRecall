namespace MeetingRecall.Core.Settings;

public enum ApplicationLanguage
{
    System = 0,
    German = 1,
    English = 2
}

public enum ApplicationTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed record ApplicationSettings(
    ApplicationLanguage Language,
    ApplicationTheme Theme,
    bool EncryptBackupsByDefault)
{
    public int SkipIntervalSeconds { get; init; } = 15;

    public bool LiveTranscriptEnabled { get; init; }

    public static ApplicationSettings Default { get; } = new(
        ApplicationLanguage.German,
        ApplicationTheme.Dark,
        EncryptBackupsByDefault: true);
}

public interface IApplicationSettingsService
{
    ApplicationSettings Current { get; }

    Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default);

    ApplicationSettings Reload();
}
