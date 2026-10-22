using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace MeetingRecall.QwenWorker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly List<nint> PreloadedCudaLibraries = [];

    public static async Task<int> Main(string[] args)
    {
        SetErrorMode(ErrorMode.FailCriticalErrors | ErrorMode.NoGpFaultErrorBox);
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: MeetingRecall.QwenWorker <request> <result>");
            return 2;
        }

        string requestPath = Path.GetFullPath(args[0]);
        string resultPath = Path.GetFullPath(args[1]);
        try
        {
            WorkerRequest request;
            await using (FileStream requestStream = File.OpenRead(requestPath))
            {
                request = await JsonSerializer.DeserializeAsync<WorkerRequest>(requestStream, JsonOptions)
                    ?? throw new InvalidDataException("The local GPU worker request is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.ModelPath)
                || !File.Exists(request.ModelPath)
                || string.IsNullOrWhiteSpace(request.SystemPrompt)
                || string.IsNullOrWhiteSpace(request.UserPrompt)
                || (request.MaximumOutputTokens != -1 && request.MaximumOutputTokens < 64))
            {
                throw new InvalidDataException("The local GPU worker request is invalid.");
            }

            string workerRoot = ConfigureCudaNativeRuntime();
            if (NativeLibraryConfig.LLama.LibraryHasLoaded)
            {
                throw new InvalidOperationException(
                    "The LLamaSharp native library was loaded before CUDA selection.");
            }

            Console.Error.WriteLine(
                "QwenWorker: forcing CUDA 12 through LLamaSharp's dependency-aware loader.");
            NativeLibraryConfig.LLama
                .WithCuda()
                .WithVulkan(false)
                .WithAutoFallback(false)
                .SkipCheck()
                .WithSearchDirectory(workerRoot)
                .WithSelectingPolicy(new Cuda12OnlySelectingPolicy())
                .WithLogCallback((level, message) =>
                    Console.Error.Write($"LLamaSharp {level}: {message}"));

            string modelFile = Path.GetFileName(request.ModelPath);
            bool qwenThinkingEnabled = !request.DisableThinking
                && modelFile.StartsWith("Qwen3-", StringComparison.OrdinalIgnoreCase);
            bool oversizedQwen72 = modelFile.Contains("72b", StringComparison.OrdinalIgnoreCase);
            var parameters = CreateModelParameters(
                request.ModelPath,
                qwenThinkingEnabled,
                oversizedQwen72);
            using LLamaWeights weights = LLamaWeights.LoadFromFile(parameters);

            nuint deviceCount = NativeApi.ggml_backend_dev_count();
            Console.Error.WriteLine(
                $"QwenWorker: registered GGML devices after model load: {deviceCount}.");
            if (deviceCount <= 1)
            {
                throw new InvalidOperationException(
                    "CUDA backend discovery failed: only the CPU GGML device was registered. "
                    + "The worker will not report this run as GPU inference.");
            }

            Console.Error.WriteLine(
                $"QwenWorker: CUDA model load succeeded with GpuLayerCount={parameters.GpuLayerCount}.");
            var executor = new StatelessExecutor(weights, parameters)
            {
                ApplyTemplate = true,
                SystemMessage = request.SystemPrompt
            };

            var inference = new InferenceParams
            {
                MaxTokens = request.MaximumOutputTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = CreateSamplingPipeline(
                    qwenThinkingEnabled,
                    request.DeterministicSampling,
                    request.ConservativeSummarySampling)
            };

            var output = new StringBuilder();
            var completion = new JsonObjectCompletionTracker(qwenThinkingEnabled);
            int generatedTokens = 0;
            var stopwatch = Stopwatch.StartNew();
            TimeSpan lastProgress = TimeSpan.Zero;
            string inferencePrompt = request.DisableThinking
                ? request.UserPrompt + "\n/no_think"
                : request.UserPrompt;

            Console.Error.WriteLine(
                $"QwenWorker: model={modelFile}; disable-thinking={request.DisableThinking}; "
                + $"context={parameters.ContextSize}; gpu-layers={parameters.GpuLayerCount}; "
                + $"sampling={(request.DeterministicSampling ? "grounded-deterministic" : request.ConservativeSummarySampling ? "summary-conservative" : qwenThinkingEnabled ? "qwen-thinking" : "qwen-nonthinking")}; "
                + $"max-tokens={(request.MaximumOutputTokens < 0 ? "natural-completion" : request.MaximumOutputTokens)}.");

            await foreach (string token in executor.InferAsync(inferencePrompt, inference))
            {
                output.Append(token);
                generatedTokens++;
                bool isComplete = completion.Append(token);
                if (stopwatch.Elapsed - lastProgress >= ProgressInterval || isComplete)
                {
                    Console.Out.WriteLine($"{generatedTokens}\t{(isComplete ? 1 : 0)}");
                    lastProgress = stopwatch.Elapsed;
                }

                if (isComplete)
                {
                    break;
                }
            }

            string generatedText = output.ToString().Trim();
            if (string.IsNullOrWhiteSpace(generatedText))
            {
                throw new InvalidDataException("The local GPU worker generated no output.");
            }

            var result = new WorkerResult("CUDA12", generatedText);
            string partialResultPath = resultPath + ".partial";
            await using (var resultStream = new FileStream(
                             partialResultPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(resultStream, result, JsonOptions);
                await resultStream.FlushAsync();
            }

            File.Move(partialResultPath, resultPath, overwrite: true);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 11;
        }
    }

    private static DefaultSamplingPipeline CreateSamplingPipeline(
        bool qwenThinkingEnabled,
        bool deterministicSampling,
        bool conservativeSummarySampling) =>
        deterministicSampling
            ? new DefaultSamplingPipeline
            {
                // Grounded question answering deliberately favors repeatable, conservative
                // decoding over creative variation. A second inference verifies every claim.
                Temperature = 0.1f,
                TopK = 1,
                TopP = 0.1f,
                MinP = 0,
                PresencePenalty = 0,
                RepeatPenalty = 1.0f,
                PenaltyCount = 256
            }
            : conservativeSummarySampling
            ? new DefaultSamplingPipeline
            {
                Temperature = 0.2f,
                TopK = 20,
                TopP = 0.8f,
                MinP = 0,
                PresencePenalty = 0,
                RepeatPenalty = 1.05f,
                PenaltyCount = 256
            }
            : qwenThinkingEnabled
            ? new DefaultSamplingPipeline
            {
                // Qwen3's recommended thinking profile.
                Temperature = 0.6f,
                TopK = 20,
                TopP = 0.95f,
                MinP = 0,
                PresencePenalty = 0,
                RepeatPenalty = 1.0f,
                PenaltyCount = 256
            }
            : new DefaultSamplingPipeline
            {
                // Qwen3's recommended non-thinking profile. A modest presence penalty is
                // intentionally used instead of the aggressive GGUF example value because
                // MeetingRecall must stay reliably German and factual.
                Temperature = 0.7f,
                TopK = 20,
                TopP = 0.8f,
                MinP = 0,
                PresencePenalty = 0.5f,
                RepeatPenalty = 1.0f,
                PenaltyCount = 256
            };

    private static ModelParams CreateModelParameters(
        string modelPath,
        bool qwenThinkingEnabled,
        bool oversizedQwen72) =>
        new(modelPath)
        {
            // Give the normal structured passes substantially more room than the old 8K
            // configuration. The dedicated global reasoning pass gets Qwen3's native 32K
            // working range without YaRN/RoPE scaling.
            ContextSize = (uint)(qwenThinkingEnabled ? 32_768 : 16_384),
            BatchSize = 512,
            UBatchSize = 256,
            // Kept for compatibility with the temporary historic 72B benchmark path.
            GpuLayerCount = oversizedQwen72 ? 18 : -1,
            FlashAttention = true,
            Threads = Math.Max(1, Environment.ProcessorCount - 1),
            BatchThreads = Math.Max(1, Environment.ProcessorCount - 1)
        };

    private static string ConfigureCudaNativeRuntime()
    {
        string workerRoot = Path.GetFullPath(AppContext.BaseDirectory);
        string cudaNativeDirectory = Path.Combine(
            workerRoot,
            "runtimes",
            "win-x64",
            "native",
            "cuda12");
        string cudaLlamaLibrary = Path.Combine(cudaNativeDirectory, "llama.dll");
        string cudaGgmlLibrary = Path.Combine(cudaNativeDirectory, "ggml.dll");
        string cudaBaseLibrary = Path.Combine(cudaNativeDirectory, "ggml-base.dll");
        string cudaBackendLibrary = Path.Combine(cudaNativeDirectory, "ggml-cuda.dll");
        foreach ((string path, string description) in new[]
                 {
                     (cudaLlamaLibrary, "llama.dll"),
                     (cudaGgmlLibrary, "ggml.dll"),
                     (cudaBaseLibrary, "ggml-base.dll"),
                     (cudaBackendLibrary, "ggml-cuda.dll")
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The bundled LLamaSharp CUDA 12 {description} is missing.",
                    path);
            }
        }

        Environment.CurrentDirectory = workerRoot;
        PrependPath(cudaNativeDirectory);
        string? toolkitRuntimeDirectory = EnumerateCudaBinaryDirectories()
            .FirstOrDefault(HasRequiredCudaRuntimeLibraries);
        if (toolkitRuntimeDirectory is null)
        {
            throw new FileNotFoundException(
                "A CUDA 12 runtime directory containing cudart64_12.dll, "
                + "cublasLt64_12.dll and cublas64_12.dll was not found.");
        }

        PrependPath(toolkitRuntimeDirectory);
        Console.Error.WriteLine(
            $"QwenWorker: CUDA toolkit runtime: {toolkitRuntimeDirectory}");
        PreloadCudaRuntimeLibraries(toolkitRuntimeDirectory);
        if (!SetDllDirectory(cudaNativeDirectory))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Could not register bundled CUDA native directory '{cudaNativeDirectory}' "
                + $"(Win32 {error}).");
        }

        Console.Error.WriteLine($"QwenWorker: LLamaSharp runtime root: {workerRoot}");
        Console.Error.WriteLine(
            $"QwenWorker: bundled CUDA native directory: {cudaNativeDirectory}");
        Console.Error.WriteLine(
            $"QwenWorker: CUDA native set complete: "
            + $"{File.Exists(cudaLlamaLibrary) && File.Exists(cudaGgmlLibrary) && File.Exists(cudaBaseLibrary) && File.Exists(cudaBackendLibrary)}");
        return workerRoot;
    }

    private static bool HasRequiredCudaRuntimeLibraries(string directory) =>
        File.Exists(Path.Combine(directory, "cudart64_12.dll"))
        && File.Exists(Path.Combine(directory, "cublasLt64_12.dll"))
        && File.Exists(Path.Combine(directory, "cublas64_12.dll"));

    private static void PreloadCudaRuntimeLibraries(string directory)
    {
        foreach (string fileName in new[]
                 {
                     "cudart64_12.dll",
                     "cublasLt64_12.dll",
                     "cublas64_12.dll"
                 })
        {
            string path = Path.Combine(directory, fileName);
            try
            {
                nint handle = NativeLibrary.Load(path);
                PreloadedCudaLibraries.Add(handle);
                Console.Error.WriteLine(
                    $"QwenWorker: preloaded CUDA dependency: {fileName}");
            }
            catch (Exception exception)
            {
                throw new DllNotFoundException(
                    $"Failed to preload CUDA dependency '{path}'.",
                    exception);
            }
        }
    }

    private static void PrependPath(string directory)
    {
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] entries = currentPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => string.Equals(
                entry,
                directory,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrWhiteSpace(currentPath)
                ? directory
                : directory + Path.PathSeparator + currentPath);
    }

    private static IEnumerable<string> EnumerateCudaBinaryDirectories()
    {
        var roots = new List<string>();
        AddCudaRoot(Environment.GetEnvironmentVariable("CUDA_PATH"), roots);
        foreach (System.Collections.DictionaryEntry variable
                 in Environment.GetEnvironmentVariables())
        {
            string name = variable.Key?.ToString() ?? string.Empty;
            if (name.StartsWith("CUDA_PATH_V", StringComparison.OrdinalIgnoreCase))
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

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "bin", "x64");
            yield return Path.Combine(root, "bin");
        }
    }

    private static void AddCudaRoot(string? value, ICollection<string> roots)
    {
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            roots.Add(Path.GetFullPath(value));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ErrorMode SetErrorMode(ErrorMode mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string pathName);

    [Flags]
    private enum ErrorMode : uint
    {
        FailCriticalErrors = 0x0001,
        NoGpFaultErrorBox = 0x0002
    }

    private sealed class Cuda12OnlySelectingPolicy : INativeLibrarySelectingPolicy
    {
        public IEnumerable<INativeLibrary> Apply(
            NativeLibraryConfig.Description description,
            SystemInfo systemInfo,
            NativeLogConfig.LLamaLogCallback? logCallback = null)
        {
            _ = systemInfo;
            _ = logCallback;
            yield return new NativeLibraryWithCuda(
                12,
                description.Library,
                description.AvxLevel,
                description.SkipCheck);
        }
    }

    private sealed record WorkerRequest(
        string ModelPath,
        string SystemPrompt,
        string UserPrompt,
        int MaximumOutputTokens,
        bool DisableThinking,
        bool DeterministicSampling = false,
        bool ConservativeSummarySampling = false);

    private sealed record WorkerResult(
        string Runtime,
        string Output);

    private sealed class JsonObjectCompletionTracker(bool waitForThinkingEnd)
    {
        private const string ThinkStart = "<think>";
        private const string ThinkEnd = "</think>";
        private int _depth;
        private bool _started;
        private bool _insideString;
        private bool _escaped;
        private bool _waitingForThinkingEnd = waitForThinkingEnd;
        private bool _sawThinkingStart;
        private string _tail = string.Empty;

        public bool Append(string value)
        {
            if (!_waitingForThinkingEnd)
            {
                return AppendJson(value);
            }

            string combined = _tail + value;
            if (!_sawThinkingStart)
            {
                int startIndex = combined.IndexOf(
                    ThinkStart,
                    StringComparison.OrdinalIgnoreCase);
                if (startIndex >= 0)
                {
                    _sawThinkingStart = true;
                }
                else
                {
                    int firstJson = combined.IndexOf('{');
                    int firstNonWhitespace = 0;
                    while (firstNonWhitespace < combined.Length
                           && char.IsWhiteSpace(combined[firstNonWhitespace]))
                    {
                        firstNonWhitespace++;
                    }

                    if (firstJson >= 0 && firstJson == firstNonWhitespace)
                    {
                        _waitingForThinkingEnd = false;
                        return AppendJson(combined[firstJson..]);
                    }
                }
            }

            if (_sawThinkingStart)
            {
                int endIndex = combined.IndexOf(
                    ThinkEnd,
                    StringComparison.OrdinalIgnoreCase);
                if (endIndex >= 0)
                {
                    _waitingForThinkingEnd = false;
                    string remainder = combined[(endIndex + ThinkEnd.Length)..];
                    return AppendJson(remainder);
                }
            }

            _tail = combined.Length <= 32
                ? combined
                : combined[^32..];
            return false;
        }

        private bool AppendJson(string value)
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
}
