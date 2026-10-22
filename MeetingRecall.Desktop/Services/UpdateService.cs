using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRecall.Desktop.Services;

public enum UpdateCheckStatus
{
    Available,
    UpToDate,
    Unavailable
}

public sealed record UpdateRelease(
    Version Version,
    string DownloadUrl,
    string Sha256,
    long Size);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateRelease? Release = null,
    string? Message = null);

/// <summary>
/// Checks only GitHub's latest *stable* release. The separate release repository
/// is intentionally not created or published by this feature.
/// </summary>
public sealed class UpdateService : IDisposable
{
    // Public release-only repository; never embed a token for a private repository.
    private const string ReleaseRepository = "TNT-Timo/MeetingRecall-Releases";
    private const long MaximumInstallerSize = 2L * 1024 * 1024 * 1024;
    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{ReleaseRepository}/releases/latest");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly string _ignoredVersionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingRecall",
        "ignored-update-version.txt");

    public UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MeetingRecall", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Version InstalledVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    // Keep the cached installer only until its version is installed.
    public void CleanupInstalledUpdateInstallers()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingRecall", "Updates");
        try
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string path in Directory.EnumerateFiles(directory, "MeetingRecall-Setup-*.exe"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                const string prefix = "MeetingRecall-Setup-";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !Version.TryParse(name[prefix.Length..], out Version? version)
                    || version > InstalledVersion)
                    continue;

                try { File.Delete(path); }
                catch (IOException) { } // Installer still in use: retry next startup.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool IsIgnored(Version version)
    {
        try
        {
            return File.Exists(_ignoredVersionFile)
                && Version.TryParse(File.ReadAllText(_ignoredVersionFile).Trim(), out var ignored)
                && ignored == version;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IgnoreVersion(Version version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ignoredVersionFile)!);
            File.WriteAllText(_ignoredVersionFile, version.ToString());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(UpdateCheckStatus.Unavailable,
                    Message: "Es wurde noch keine öffentliche Stable-Updatequelle eingerichtet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(UpdateCheckStatus.Unavailable,
                    Message: $"GitHub hat die Update-Prüfung abgelehnt (HTTP {(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease
                || string.IsNullOrWhiteSpace(release.TagName)
                || !Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version))
            {
                return new(UpdateCheckStatus.Unavailable,
                    Message: "GitHub hat keine gültige Stable-Version zurückgegeben.");
            }

            if (version <= InstalledVersion)
            {
                return new(UpdateCheckStatus.UpToDate);
            }

            var asset = release.Assets?.FirstOrDefault(item =>
                item.Name is not null
                && item.Name.StartsWith("MeetingRecall-Setup-", StringComparison.OrdinalIgnoreCase)
                && item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (asset is null || asset.Size <= 0 || asset.Size > MaximumInstallerSize
                || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !downloadUri.AbsolutePath.StartsWith(
                    $"/{ReleaseRepository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                return new(UpdateCheckStatus.Unavailable,
                    Message: "Der neue Release enthält keinen gültigen MeetingRecall-Installer.");
            }

            const string digestPrefix = "sha256:";
            if (asset.Digest is null
                || !asset.Digest.StartsWith(digestPrefix, StringComparison.OrdinalIgnoreCase)
                || asset.Digest.Length != digestPrefix.Length + 64
                || !asset.Digest[digestPrefix.Length..].All(Uri.IsHexDigit))
            {
                return new(UpdateCheckStatus.Unavailable,
                    Message: "Dem Release fehlt eine gültige SHA-256-Prüfsumme für den Installer.");
            }

            return new(UpdateCheckStatus.Available,
                new UpdateRelease(version, downloadUri.AbsoluteUri,
                    asset.Digest[digestPrefix.Length..], asset.Size));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException or IOException or JsonException)
        {
            return new(UpdateCheckStatus.Unavailable,
                Message: "Die Update-Prüfung ist derzeit nicht möglich. Bitte Internetverbindung prüfen.");
        }
    }

    // Keep verified installers across application restarts, not in the temporary folder.
    private static string GetInstallerPath(UpdateRelease release) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingRecall", "Updates",
            $"MeetingRecall-Setup-{release.Version}.exe");

    public async Task<string?> GetCachedInstallerAsync(
        UpdateRelease release, CancellationToken cancellationToken = default)
    {
        string path = GetInstallerPath(release);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != release.Size)
                return null;

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                actual, Convert.FromHexString(release.Sha256)) ? path : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // An already verified download is immediately reusable after a crash/restart.
        string? cached = await GetCachedInstallerAsync(release, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Report(100);
            return cached;
        }

        string installerPath = GetInstallerPath(release);
        string directory = Path.GetDirectoryName(installerPath)!;
        Directory.CreateDirectory(directory);
        // Only incomplete downloads use a throwaway path; verified EXEs are retained.
        string partialPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");

        try
        {
            using var response = await _httpClient.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long length && length != release.Size)
            {
                throw new InvalidDataException("Die Downloadgröße stimmt nicht mit dem Release überein.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > release.Size || total > MaximumInstallerSize)
                {
                    throw new InvalidDataException("Die heruntergeladene Datei ist zu groß.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(total * 100d / release.Size);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != release.Size || !CryptographicOperations.FixedTimeEquals(
                    hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
            {
                throw new InvalidDataException("Die Integritätsprüfung des Updates ist fehlgeschlagen.");
            }

            // Close the output stream before renaming (required on Windows).
            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(partialPath, installerPath, overwrite: true);
            return installerPath;
        }
        catch
        {
            try { File.Delete(partialPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
