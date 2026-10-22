using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Models;
using MeetingRecall.Infrastructure.Models;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Intelligence;

internal sealed class SummaryLanguageModelManager : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private static readonly SummaryModelConfiguration Model = new(
        "Qwen3 14B · Q4_K_M",
        "Qwen3",
        "Qwen3-14B-Q4_K_M.gguf",
        new Uri("https://huggingface.co/Qwen/Qwen3-14B-GGUF/resolve/main/Qwen3-14B-Q4_K_M.gguf?download=true"),
        8_500_000_000,
        9_001_752_960,
        "500a8806e85ee9c83f3ae08420295592451379b4f8cf2d0f41c15dffeb6b81f0");

    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly AsyncLocal<int> _thinkingOverrideDepth = new();
    private bool _disposed;

    public SummaryLanguageModelManager(AppDataPaths paths)
    {
        _paths = paths;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MeetingRecall/1.0 local-model-downloader");
    }

    public string ModelDisplayName => Model.DisplayName;
    public long ModelDownloadSizeBytes => Model.ApproximateSize;
    internal string CurrentModelPath => GetModelPath(Model);

    public bool IsModelInstalled
    {
        get { try { return IsInstalled(Model, GetModelPath(Model)); } catch { return false; } }
    }

    // Normal extraction/synthesis stays in the fast non-thinking profile. A final whole-
    // transcript editorial pass may opt into Qwen3 thinking for its own async flow without
    // changing the behavior of concurrent chat or summary requests.
    internal bool CurrentModelDisablesThinking => _thinkingOverrideDepth.Value == 0;

    internal IDisposable EnableThinkingForCurrentFlow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thinkingOverrideDepth.Value = _thinkingOverrideDepth.Value + 1;
        return new ThinkingOverrideScope(this);
    }

    internal async Task<string> EnsureCurrentModelInstalledAsync(
        IProgress<LocalLanguageModelProgress>? progress, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await EnsureModelAsync(Model, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureBundleModelInstalledAsync(
        IProgress<LocalModelBundleProgress>? progress, int completedModelOffset,
        long completedByteOffset, int totalModelCount, long totalBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modelProgress = new ImmediateProgress<LocalLanguageModelProgress>(item =>
        {
            long modelBytes = Math.Clamp(item.DownloadedBytes, 0, Model.ApproximateSize);
            long overallBytes = completedByteOffset + modelBytes;
            progress?.Report(new LocalModelBundleProgress(
                Model.DisplayName, completedModelOffset, totalModelCount,
                totalBytes > 0 ? overallBytes / (double)totalBytes : null,
                overallBytes, totalBytes, item.BytesPerSecond));
        });
        await EnsureModelAsync(Model, modelProgress, cancellationToken).ConfigureAwait(false);
        long completedBytes = completedByteOffset + Model.ApproximateSize;
        progress?.Report(new LocalModelBundleProgress(
            Model.DisplayName, completedModelOffset + 1, totalModelCount,
            totalBytes > 0 ? completedBytes / (double)totalBytes : null,
            completedBytes, totalBytes));
    }

    private async Task<string> EnsureModelAsync(
        SummaryModelConfiguration model, IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        string modelPath = GetModelPath(model);
        if (IsInstalled(model, modelPath)) return modelPath;
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled(model, modelPath)) return modelPath;
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)
                ?? throw new InvalidOperationException("Der Pfad des lokalen Sprachmodells ist ungültig."));
            string partialPath = modelPath + ".download";
            await DownloadModelAsync(model, partialPath, progress, cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, modelPath, overwrite: true);
            return modelPath;
        }
        finally { _modelGate.Release(); }
    }

    private async Task DownloadModelAsync(
        SummaryModelConfiguration model, string partialPath,
        IProgress<LocalLanguageModelProgress>? progress, CancellationToken cancellationToken)
    {
        long existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength >= model.ApproximateSize * 1.2) { TryDelete(partialPath); existingLength = 0; }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.Uri);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        bool resuming = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming) existingLength = 0;
        long totalLength = response.Content.Headers.ContentRange?.Length
            ?? ((response.Content.Headers.ContentLength ?? model.ApproximateSize) + existingLength);
        progress?.Report(new LocalLanguageModelProgress(
            LocalLanguageModelStage.DownloadingModel,
            totalLength > 0 ? existingLength / (double)totalLength : null,
            existingLength, totalLength));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (resuming) await AppendFileToHashAsync(partialPath, hash, cancellationToken).ConfigureAwait(false);
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partialPath, resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[1024 * 1024];
        long downloaded = existingLength, downloadedThisRun = 0;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            downloaded += read;
            downloadedThisRun += read;
            if (stopwatch.Elapsed - lastReport >= ProgressInterval || downloaded >= totalLength)
            {
                double speed = stopwatch.Elapsed.TotalSeconds > 0 ? downloadedThisRun / stopwatch.Elapsed.TotalSeconds : 0;
                progress?.Report(new LocalLanguageModelProgress(
                    LocalLanguageModelStage.DownloadingModel,
                    totalLength > 0 ? Math.Clamp(downloaded / (double)totalLength, 0, 1) : null,
                    downloaded, totalLength, speed));
                lastReport = stopwatch.Elapsed;
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (downloaded < model.MinimumValidSize)
            throw new InvalidDataException($"Das heruntergeladene Sprachmodell '{model.DisplayName}' ist unvollständig.");
        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualHash, model.Sha256, StringComparison.Ordinal))
        {
            TryDelete(partialPath);
            throw new InvalidDataException($"Die Prüfsumme von '{model.DisplayName}' ist ungültig. Der unvollständige Download wurde entfernt.");
        }
    }

    private void ReleaseThinkingOverride()
    {
        int current = _thinkingOverrideDepth.Value;
        if (current > 0)
        {
            _thinkingOverrideDepth.Value = current - 1;
        }
    }

    private string GetModelPath(SummaryModelConfiguration model) =>
        Path.Combine(_paths.ModelsDirectory, model.DirectoryName, model.FileName);

    private static bool IsInstalled(SummaryModelConfiguration model, string path) =>
        File.Exists(path) && new FileInfo(path).Length >= model.MinimumValidSize;

    private static async Task AppendFileToHashAsync(string path, IncrementalHash hash, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _modelGate.Dispose();
    }

    private sealed class ThinkingOverrideScope(SummaryLanguageModelManager owner) : IDisposable
    {
        private SummaryLanguageModelManager? _owner = owner;

        public void Dispose()
        {
            SummaryLanguageModelManager? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseThinkingOverride();
        }
    }

    private sealed record SummaryModelConfiguration(
        string DisplayName, string DirectoryName, string FileName, Uri Uri,
        long MinimumValidSize, long ApproximateSize, string Sha256);
}
