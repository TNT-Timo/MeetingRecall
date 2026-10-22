using System.Diagnostics;
using System.Text.Json;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Models;
using MeetingRecall.Infrastructure.Storage;

namespace MeetingRecall.Infrastructure.Intelligence;

internal sealed class GpuFirstQwenLanguageModelService(
    QwenLocalLanguageModelService fallbackService,
    AppDataPaths paths)
    : ILocalLanguageModelService,
      IDisposable
{
    private static readonly JsonSerializerOptions WorkerJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private bool _disposed;

    public bool IsModelInstalled => fallbackService.IsModelInstalled;

    public string ModelDisplayName => fallbackService.ModelDisplayName;

    public long ModelDownloadSizeBytes => fallbackService.ModelDownloadSizeBytes;

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

        if (!fallbackService.IsModelInstalled)
        {
            return await fallbackService.GenerateAsync(
                    systemPrompt,
                    userPrompt,
                    maximumOutputTokens,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string modelPath = GetCurrentModelPath();
        if (!File.Exists(modelPath))
        {
            return await fallbackService.GenerateAsync(
                    systemPrompt,
                    userPrompt,
                    maximumOutputTokens,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.LoadingModel));

            WorkerAttempt workerAttempt = await TryGenerateOnGpuAsync(
                    modelPath,
                    systemPrompt,
                    userPrompt,
                    maximumOutputTokens,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (workerAttempt.Result is not null)
            {
                progress?.Report(new LocalLanguageModelProgress(
                    LocalLanguageModelStage.Generating,
                    Fraction: 1));
                Debug.WriteLine(
                    $"Qwen runtime: {workerAttempt.Result.Runtime} · GPU worker; "
                    + "full model offload.");
                return workerAttempt.Result.Output;
            }

            Debug.WriteLine(
                "QWEN GPU WORKER FAILED — CPU FALLBACK ACTIVE. "
                + workerAttempt.Error);
            return await fallbackService.GenerateAsync(
                    systemPrompt,
                    userPrompt,
                    maximumOutputTokens,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private string GetCurrentModelPath()
    {
        return Path.Combine(
            paths.ModelsDirectory,
            "Qwen3",
            "Qwen3-14B-Q4_K_M.gguf");
    }

    private static async Task<WorkerAttempt> TryGenerateOnGpuAsync(
        string modelPath,
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        string workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "QwenWorker",
            "MeetingRecall.QwenWorker.exe");
        if (!File.Exists(workerPath))
        {
            return new WorkerAttempt(
                null,
                $"GPU worker not found: {workerPath}");
        }

        string workerDirectory = Path.GetDirectoryName(workerPath)
            ?? AppContext.BaseDirectory;
        string requestPath = Path.Combine(
            Path.GetTempPath(),
            $"MeetingRecall-qwen-request-{Guid.NewGuid():N}.json");
        string resultPath = Path.Combine(
            Path.GetTempPath(),
            $"MeetingRecall-qwen-result-{Guid.NewGuid():N}.json");
        WorkerRequest request = CreateWorkerRequest(
            modelPath, systemPrompt, userPrompt, maximumOutputTokens);

        try
        {
            await using (var requestStream = new FileStream(
                             requestPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        requestStream,
                        request,
                        WorkerJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await requestStream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                WorkingDirectory = workerDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(resultPath);

            Debug.WriteLine(
                $"Qwen GPU worker starting from '{workerDirectory}'.");
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new WorkerAttempt(
                    null,
                    "GPU worker could not be started.");
            }

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task progressTask = PumpWorkerProgressAsync(
                process.StandardOutput,
                maximumOutputTokens,
                progress,
                cancellationToken);
            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(GetInferenceTimeout(maximumOutputTokens));

            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                return new WorkerAttempt(
                    null,
                    "GPU worker exceeded the inference timeout.");
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                throw;
            }

            string error = await errorTask.ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine(
                    "Qwen GPU worker diagnostics:\n" + error.Trim());
            }

            if (process.ExitCode != 0 || !File.Exists(resultPath))
            {
                return new WorkerAttempt(
                    null,
                    $"GPU worker exit code {process.ExitCode}: {error.Trim()}");
            }

            await using FileStream resultStream = File.OpenRead(resultPath);
            WorkerResult? result = await JsonSerializer.DeserializeAsync<WorkerResult>(
                    resultStream,
                    WorkerJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(result.Output))
            {
                return new WorkerAttempt(
                    null,
                    "GPU worker returned no result.");
            }

            return new WorkerAttempt(result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WorkerAttempt(null, exception.ToString());
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
            TryDelete(resultPath + ".partial");
        }
    }

    private static async Task PumpWorkerProgressAsync(
        StreamReader output,
        int maximumOutputTokens,
        IProgress<LocalLanguageModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false)
               is { } line)
        {
            string[] values = line.Split('\t');
            if (values.Length != 2
                || !int.TryParse(values[0], out int generatedTokens)
                || !int.TryParse(values[1], out int completed))
            {
                continue;
            }

            progress?.Report(new LocalLanguageModelProgress(
                LocalLanguageModelStage.Generating,
                completed == 1
                    ? 1
                    : Math.Clamp(
                        generatedTokens / (double)maximumOutputTokens,
                        0,
                        0.99)));
        }
    }

    private static TimeSpan GetInferenceTimeout(int maximumOutputTokens) =>
        TimeSpan.FromMinutes(Math.Clamp(
            6 + (maximumOutputTokens / 128d),
            8,
            15));

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The worker may already have terminated while cancellation was handled.
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
            // Temporary worker files are best-effort cleanup only.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inferenceGate.Dispose();
    }

    private static WorkerRequest CreateWorkerRequest(
        string modelPath, string systemPrompt, string userPrompt, int maximumOutputTokens)
    {
        bool summary = systemPrompt.Contains(
            ContextualSummaryPipeline.ProfileMarker, StringComparison.Ordinal);
        return new WorkerRequest(modelPath, systemPrompt, userPrompt, maximumOutputTokens,
            DisableThinking: summary, ConservativeSummarySampling: summary);
    }

    private sealed record WorkerRequest(
        string ModelPath,
        string SystemPrompt,
        string UserPrompt,
        int MaximumOutputTokens,
        bool DisableThinking = false,
        bool DeterministicSampling = false,
        bool ConservativeSummarySampling = false);

    private sealed record WorkerAttempt(
        WorkerResult? Result,
        string? Error);

    private sealed record WorkerResult(
        string Runtime,
        string Output);
}
