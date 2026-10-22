using System.Text.Json;
using MeetingRecall.Core.Settings;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Settings;

internal sealed class ApplicationSettingsService(AppDataPaths paths)
    : IApplicationSettingsService
{
    private const int CurrentFormatVersion = 1;
    private readonly object _gate = new();
    private ApplicationSettings? _current;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ApplicationSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= LoadCore();
            }
        }
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        Validate(settings);
        cancellationToken.ThrowIfCancellationRequested();

        paths.EnsureCreated();
        string temporaryPath = paths.ApplicationSettingsFilePath + ".partial";

        if (settings == ApplicationSettings.Default)
        {
            TryDelete(temporaryPath);
            TryDelete(paths.ApplicationSettingsFilePath);
            lock (_gate)
            {
                _current = settings;
            }

            return;
        }

        var document = new SettingsDocument(CurrentFormatVersion, settings);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

        TryDelete(temporaryPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, paths.ApplicationSettingsFilePath, overwrite: true);
            lock (_gate)
            {
                _current = settings;
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public ApplicationSettings Reload()
    {
        lock (_gate)
        {
            _current = LoadCore();
            return _current;
        }
    }

    private ApplicationSettings LoadCore()
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.ApplicationSettingsFilePath))
        {
            return ApplicationSettings.Default;
        }

        try
        {
            using FileStream stream = File.Open(
                paths.ApplicationSettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            SettingsDocument? document = JsonSerializer.Deserialize<SettingsDocument>(stream, JsonOptions);
            if (document is null || document.FormatVersion != CurrentFormatVersion)
            {
                return ApplicationSettings.Default;
            }

            Validate(document.Settings);
            return Normalize(document.Settings);
        }
        catch
        {
            return ApplicationSettings.Default;
        }
    }

    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        int skipIntervalSeconds = IsValidSkipInterval(settings.SkipIntervalSeconds)
            ? settings.SkipIntervalSeconds
            : ApplicationSettings.Default.SkipIntervalSeconds;
        return settings with
        {
            Theme = ApplicationTheme.Dark,
            SkipIntervalSeconds = skipIntervalSeconds
        };
    }

    private static void Validate(ApplicationSettings settings)
    {
        if (!Enum.IsDefined(settings.Language))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Die gewählte Sprache ist ungültig.");
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Das gewählte Design ist ungültig.");
        }

        if (!IsValidSkipInterval(settings.SkipIntervalSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Die gewählte Sprungweite ist ungültig.");
        }
    }

    private static bool IsValidSkipInterval(int seconds)
    {
        return seconds is 3 or 5 or 10 or 15 or 30;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A later save can clean up an abandoned temporary file.
        }
    }

    private sealed record SettingsDocument(
        int FormatVersion,
        ApplicationSettings Settings);
}
