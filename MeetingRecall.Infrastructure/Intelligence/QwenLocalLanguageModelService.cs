using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Models;
using MeetingRecall.Infrastructure.Models;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Intelligence;

internal sealed class QwenLocalLanguageModelService :
    ILocalLanguageModelService,
    IDisposable
{
    private static readonly TimeSpan ProgressInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly LanguageModelConfiguration StrongModel = new(
        "Qwen3-14B Q4_K_M",
        "Qwen3-14B-Q4_K_M.gguf",
        new Uri(
            "https://huggingface.co/Qwen/Qwen3-14B-GGUF/resolve/main/"
            + "Qwen3-14B-Q4_K_M.gguf?download=true"),
        MinimumValidSize: 8_500_000_000,
        ApproximateSize: 9_001_752_960,
        Sha256: "500a8806e85ee9c83f3ae08420295592451379b4f8cf2d0f41c15dffeb6b81f0");
    private static readonly LanguageModelConfiguration[] SupportedModels =
        [StrongModel];
    private static readonly object NativeRuntimeGate = new();
    private static bool? _nativeCudaAvailable;

    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private LLamaWeights? _weights;
    private string? _weightsModelPath;
    private bool _weightsUseGpu;
    private bool _disposed;

    public QwenLocalLanguageModelService(AppDataPaths paths)
    {
        _paths = paths;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MeetingRecall/1.0 local-model-downloader");
    }

    private static LanguageModelConfiguration CurrentModel => StrongModel;

    public bool IsModelInstalled
    {
        get
        {
            try
            {
                LanguageModelConfiguration model = CurrentModel;
                string modelPath = GetModelPath(model);
                return IsInstalled(model, modelPath);
            }
            catch
            {
                return false;
            }
        }
    }

    public string ModelDisplayName => CurrentModel.DisplayName;

    public long ModelDownloadSizeBytes => CurrentModel.ApproximateSize;

    public long AllModelsDownloadSizeBytes =>
        SupportedModels.Sum(model => model.ApproximateSize);

    public bool AreAllModelsInstalled
    {
        get
        {
            try
            {
                return SupportedModels.All(model =>
                    IsInstalled(model, GetModelPath(model)));
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task EnsureAllModelsInstalledAsync(
        IProgress<LocalModelBundleProgress>? progress,
        int completedModelOffset,
        long completedByteOffset,
        int totalModelCount,
        long totalBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long familyCompletedBytes = 0;
        for (int index = 0; index < SupportedModels.Length; index++)
        {
            LanguageModelConfiguration model = SupportedModels[index];
            long modelByteOffset = completedByteOffset + familyCompletedBytes;
            int modelIndex = index;
            var modelProgress = new ImmediateProgress<LocalLanguageModelProgress>(item =>
            {
                long modelBytes = Math.Clamp(
                    item.DownloadedBytes,
                    0,
                    model.ApproximateSize);
                long overallBytes = modelByteOffset + modelBytes;
                progress?.Report(new LocalModelBundleProgress(
                    model.DisplayName,
                    completedModelOffset + modelIndex,
                    totalModelCount,
                    totalBytes > 0 ? overallBytes / (double)totalBytes : null,
                    overallBytes,
                    totalBytes,
                    item.BytesPerSecond));
            });

            await EnsureModelAsync(model, modelProgress, cancellationToken)
                .ConfigureAwait(false);
            familyCompletedBytes += model.ApproximateSize;
            long completedBytes = completedByteOffset + familyCompletedBytes;
            progress?.Report(new LocalModelBundleProgress(
                model.DisplayName,
                completedModelOffset + index + 1,
                totalModelCount,
                totalBytes > 0 ? completedBytes / (double)totalBytes : null,
                completedBytes,
                totalBytes));
        }
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        if (maximumOutputTokens is < 64 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LanguageModelConfiguration model = CurrentModel;
            string modelPath = await EnsureModelAsync(
                    model,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            LLamaWeights weights = await EnsureWeightsAsync(
                    modelPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.Generating));

            var parameters = CreateModelParameters(
                modelPath,
                _weightsUseGpu);
            var executor = new StatelessExecutor(weights, parameters)
            {
                ApplyTemplate = true,
                SystemMessage = systemPrompt
            };
            var inference = new InferenceParams
            {
                MaxTokens = maximumOutputTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.2f,
                    TopK = 10,
                    TopP = 0.8f,
                    MinP = 0,
                    PresencePenalty = 0,
                    RepeatPenalty = 1.03f,
                    PenaltyCount = 256
                }
            };

            var output = new StringBuilder();
            var completion = new JsonObjectCompletionTracker();
            int generatedTokens = 0;
            var generationStopwatch = Stopwatch.StartNew();
            TimeSpan lastProgress = TimeSpan.Zero;
            using var inferenceCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            inferenceCancellation.CancelAfter(GetInferenceTimeout(
                maximumOutputTokens));
            try
            {
                await foreach (string token in executor
                                   .InferAsync(
                                       userPrompt + "\n/no_think",
                                       inference,
                                       inferenceCancellation.Token)
                                   .ConfigureAwait(false))
                {
                    output.Append(token);
                    generatedTokens++;
                    bool isComplete = completion.Append(token);
                    if (generationStopwatch.Elapsed - lastProgress
                            >= ProgressInterval
                        || isComplete)
                    {
                        progress?.Report(new LocalLanguageModelProgress(
                            LocalLanguageModelStage.Generating,
                            isComplete
                                ? 1
                                : Math.Clamp(
                                    generatedTokens
                                    / (double)maximumOutputTokens,
                                    0,
                                    0.99)));
                        lastProgress = generationStopwatch.Elapsed;
                    }

                    if (isComplete)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Das lokale Sprachmodell hat das Zeitlimit für einen einzelnen Verarbeitungsschritt überschritten.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string result = output.ToString().Trim();
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidDataException(
                    "Das lokale Sprachmodell hat keine Antwort erzeugt.");
            }

            return result;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private static TimeSpan GetInferenceTimeout(int maximumOutputTokens) =>
        TimeSpan.FromMinutes(Math.Clamp(
            6 + (maximumOutputTokens / 128d),
            8,
            15));

    private async Task<string> EnsureModelAsync(
        LanguageModelConfiguration model,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        string modelPath = GetModelPath(model);
        if (IsInstalled(model, modelPath))
        {
            return modelPath;
        }

        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled(model, modelPath))
            {
                return modelPath;
            }

            string directory = Path.GetDirectoryName(modelPath)
                ?? throw new InvalidOperationException(
                    "Der Pfad des lokalen Sprachmodells ist ungültig.");
            Directory.CreateDirectory(directory);
            string partialPath = modelPath + ".download";
            await DownloadModelAsync(
                    model,
                    partialPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(partialPath, modelPath, overwrite: true);
            return modelPath;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private async Task DownloadModelAsync(
        LanguageModelConfiguration model,
        string partialPath,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        long existingLength = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        if (existingLength >= model.ApproximateSize * 1.2)
        {
            TryDelete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.Uri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool resuming = existingLength > 0
            && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming)
        {
            existingLength = 0;
        }

        long totalLength = response.Content.Headers.ContentRange?.Length
            ?? ((response.Content.Headers.ContentLength ?? model.ApproximateSize)
                + existingLength);
        progress?.Report(new LocalLanguageModelProgress(
            LocalLanguageModelStage.DownloadingModel,
            totalLength > 0 ? existingLength / (double)totalLength : null,
            existingLength,
            totalLength));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (resuming)
        {
            await AppendFileToHashAsync(partialPath, hash, cancellationToken)
                .ConfigureAwait(false);
        }

        await using Stream input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[1024 * 1024];
        long downloaded = existingLength;
        long downloadedThisRun = 0;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        int read;
        while ((read = await input
                   .ReadAsync(buffer.AsMemory(), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            downloaded += read;
            downloadedThisRun += read;

            if (stopwatch.Elapsed - lastReport >= ProgressInterval
                || downloaded >= totalLength)
            {
                double bytesPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                    ? downloadedThisRun / stopwatch.Elapsed.TotalSeconds
                    : 0;
                progress?.Report(new LocalLanguageModelProgress(
                    LocalLanguageModelStage.DownloadingModel,
                    totalLength > 0
                        ? Math.Clamp(downloaded / (double)totalLength, 0, 1)
                        : null,
                    downloaded,
                    totalLength,
                    bytesPerSecond));
                lastReport = stopwatch.Elapsed;
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (downloaded < model.MinimumValidSize)
        {
            throw new InvalidDataException(
                "Das heruntergeladene Qwen-Modell ist unvollständig.");
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
        if (!string.Equals(actualHash, model.Sha256, StringComparison.Ordinal))
        {
            TryDelete(partialPath);
            throw new InvalidDataException(
                "Die Prüfsumme des Qwen-Modells ist ungültig. Der unvollständige Download wurde entfernt.");
        }
    }

    private async Task<LLamaWeights> EnsureWeightsAsync(
        string modelPath,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_weights is not null
            && string.Equals(
                _weightsModelPath,
                modelPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return _weights;
        }

        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_weights is not null
                && string.Equals(
                    _weightsModelPath,
                    modelPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _weights;
            }

            _weights?.Dispose();
            _weights = null;
            _weightsModelPath = null;
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.LoadingModel));
            cancellationToken.ThrowIfCancellationRequested();
            LLamaWeights loaded;
            bool nativeCudaAvailable = ConfigureNativeRuntime();
            if (nativeCudaAvailable)
            {
                try
                {
                    loaded = await LoadWeightsAsync(modelPath, useGpu: true)
                        .ConfigureAwait(false);
                    _weightsUseGpu = true;
                    Debug.WriteLine(
                        "Qwen runtime: CUDA GPU with full model offload.");
                }
                catch (Exception gpuException)
                {
                    Debug.WriteLine(
                        "Qwen CUDA model offload failed; using CPU fallback. "
                        + gpuException);
                    loaded = await LoadWeightsAsync(modelPath, useGpu: false)
                        .ConfigureAwait(false);
                    _weightsUseGpu = false;
                }
            }
            else
            {
                loaded = await LoadWeightsAsync(modelPath, useGpu: false)
                    .ConfigureAwait(false);
                _weightsUseGpu = false;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                loaded.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _weights = loaded;
            _weightsModelPath = modelPath;
            return loaded;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private static bool ConfigureNativeRuntime()
    {
        lock (NativeRuntimeGate)
        {
            if (_nativeCudaAvailable.HasValue)
            {
                return _nativeCudaAvailable.Value;
            }

            if (NativeLibraryConfig.LLama.LibraryHasLoaded)
            {
                Debug.WriteLine(
                    "Qwen native runtime was loaded before GPU selection; using CPU fallback.");
                _nativeCudaAvailable = false;
                return false;
            }

            ConfigureCudaRuntimeSearchPath();
            NativeLibraryConfig.LLama
                .WithCuda()
                .WithVulkan(false)
                .WithAutoFallback(false)
                .SkipCheck()
                .WithLogCallback((level, message) =>
                    Debug.Write($"LLamaSharp {level}: {message}"));

            nuint deviceCount;
            try
            {
                deviceCount = NativeApi.ggml_backend_dev_count();
            }
            catch (Exception gpuException)
            {
                Debug.WriteLine(
                    "Qwen CUDA native backend initialization failed; "
                    + "using CPU fallback. "
                    + gpuException);
                _nativeCudaAvailable = false;
                return false;
            }

            _nativeCudaAvailable = deviceCount > 1;
            if (_nativeCudaAvailable.Value)
            {
                Debug.WriteLine(
                    $"Qwen CUDA backend verified: {deviceCount} devices "
                    + "(GPU and CPU).");
                return true;
            }

            Debug.WriteLine(
                "Qwen CUDA backend was not registered; only the CPU device "
                + "is available. Check the preceding LLamaSharp dependency "
                + "messages for ggml-cuda.dll. Using CPU fallback.");
            return false;
        }
    }

    private static void ConfigureCudaRuntimeSearchPath()
    {
        string? runtimeDirectory = EnumerateCudaBinaryDirectories()
            .FirstOrDefault(directory =>
                File.Exists(Path.Combine(directory, "cudart64_12.dll")));
        if (runtimeDirectory is null)
        {
            Debug.WriteLine(
                "Qwen CUDA 12 runtime directory containing cudart64_12.dll was not found.");
            return;
        }

        if (!SetDllDirectory(runtimeDirectory))
        {
            Debug.WriteLine(
                $"Qwen could not register CUDA runtime directory '{runtimeDirectory}' "
                + $"(Win32 {Marshal.GetLastWin32Error()}).");
            return;
        }

        Debug.WriteLine(
            $"Qwen using CUDA runtime directory: {runtimeDirectory}");
    }

    private static IEnumerable<string> EnumerateCudaBinaryDirectories()
    {
        var roots = new List<string>();
        AddCudaRoot(Environment.GetEnvironmentVariable("CUDA_PATH"), roots);
        foreach (System.Collections.DictionaryEntry variable in
                 Environment.GetEnvironmentVariables())
        {
            string name = variable.Key?.ToString() ?? string.Empty;
            if (name.StartsWith(
                    "CUDA_PATH_V",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddCudaRoot(variable.Value?.ToString(), roots);
            }
        }

        string? programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string toolkitRoot = Path.Combine(
                programFiles,
                "NVIDIA GPU Computing Toolkit",
                "CUDA");
            if (Directory.Exists(toolkitRoot))
            {
                foreach (string directory in Directory
                             .EnumerateDirectories(toolkitRoot)
                             .OrderByDescending(
                                 path => path,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    AddCudaRoot(directory, roots);
                }
            }
        }

        foreach (string root in roots.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "bin", "x64");
            yield return Path.Combine(root, "bin");
        }
    }

    private static void AddCudaRoot(
        string? value,
        ICollection<string> roots)
    {
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            roots.Add(Path.GetFullPath(value));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string pathName);

    private static Task<LLamaWeights> LoadWeightsAsync(
        string modelPath,
        bool useGpu) =>
        Task.Run(
            () => LLamaWeights.LoadFromFile(
                CreateModelParameters(modelPath, useGpu)),
            CancellationToken.None);

    private static ModelParams CreateModelParameters(
        string modelPath,
        bool useGpu) => new(modelPath)
    {
        ContextSize = 16_384,
        BatchSize = 512,
        UBatchSize = 256,
        GpuLayerCount = useGpu ? -1 : 0,
        FlashAttention = true,
        Threads = Math.Max(1, Environment.ProcessorCount - 1),
        BatchThreads = Math.Max(1, Environment.ProcessorCount - 1)
    };

    private static async Task AppendFileToHashAsync(
        string path,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream
                   .ReadAsync(buffer.AsMemory(), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
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
            // A later download attempt can retry cleanup.
        }
    }

    private string GetModelPath(LanguageModelConfiguration model) =>
        Path.Combine(
            _paths.ModelsDirectory,
            "Qwen3",
            model.FileName);

    private static bool IsInstalled(
        LanguageModelConfiguration model,
        string modelPath) =>
        File.Exists(modelPath)
        && new FileInfo(modelPath).Length >= model.MinimumValidSize;

    private sealed record LanguageModelConfiguration(
        string DisplayName,
        string FileName,
        Uri Uri,
        long MinimumValidSize,
        long ApproximateSize,
        string Sha256);

    private sealed class JsonObjectCompletionTracker
    {
        private int _depth;
        private bool _started;
        private bool _insideString;
        private bool _escaped;

        public bool Append(string value)
        {
            foreach (char character in value)
            {
                if (_insideString)
                {
                    if (_escaped)
                    {
                        _escaped = false;
                    }
                    else if (character == '\\')
                    {
                        _escaped = true;
                    }
                    else if (character == '"')
                    {
                        _insideString = false;
                    }

                    continue;
                }

                if (character == '"' && _started)
                {
                    _insideString = true;
                }
                else if (character == '{')
                {
                    _started = true;
                    _depth++;
                }
                else if (character == '}' && _started)
                {
                    _depth--;
                    if (_depth == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _weights?.Dispose();
        _httpClient.Dispose();
        _modelGate.Dispose();
        _inferenceGate.Dispose();
    }
}
