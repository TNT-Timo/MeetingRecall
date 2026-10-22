using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Models;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Infrastructure.Models;
using MeetingRecall.Infrastructure.Storage;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace MeetingRecall.Infrastructure.Transcription;

internal interface IWhisperTranscriptionEngine
{
    bool IsModelInstalled { get; }

    string ModelDisplayName { get; }

    long ModelDownloadSizeBytes => 0;

    string LastRuntimeDescription => string.Empty;

    Task<WhisperTranscriptionResult> TranscribeAsync(
        string normalizedWavePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed record WhisperTranscriptionResult(
    string? Language,
    IReadOnlyList<WhisperTranscriptSegment> Segments,
    int SuppressedRepetitionSegments = 0);

internal sealed record WhisperTranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text);

internal sealed class WhisperModelManager(AppDataPaths paths)
{
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
    private static readonly WhisperModelConfiguration Model = new(
        "Whisper Large-v3 (mehrsprachig)",
        "ggml-large-v3.bin",
        GgmlType.LargeV3,
        MinimumValidSize: 2_500_000_000,
        ExpectedSize: 3_095_033_483);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public string ModelDisplayName => Model.DisplayName;

    public long ModelDownloadSizeBytes => Model.ExpectedSize;

    public bool IsInstalled
    {
        get
        {
            try
            {
                RemoveUnsupportedModels();
                string modelPath = GetModelPath(Model);
                return IsModelFileInstalled(Model, modelPath);
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<string> EnsureInstalledAsync(
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await EnsureModelInstalledAsync(
                Model,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureBundleModelInstalledAsync(
        IProgress<LocalModelBundleProgress>? progress,
        int completedModelOffset,
        long completedByteOffset,
        int totalModelCount,
        long totalBytes,
        CancellationToken cancellationToken = default)
    {
        var modelProgress = new ImmediateProgress<TranscriptionProgress>(item =>
        {
            long modelBytes = Math.Clamp(
                item.DownloadedBytes,
                0,
                Model.ExpectedSize);
            long overallBytes = completedByteOffset + modelBytes;
            progress?.Report(new LocalModelBundleProgress(
                Model.DisplayName,
                completedModelOffset,
                totalModelCount,
                totalBytes > 0 ? overallBytes / (double)totalBytes : null,
                overallBytes,
                totalBytes,
                item.BytesPerSecond));
        });

        await EnsureModelInstalledAsync(
                Model,
                modelProgress,
                cancellationToken)
            .ConfigureAwait(false);
        long completedBytes = completedByteOffset + Model.ExpectedSize;
        progress?.Report(new LocalModelBundleProgress(
            Model.DisplayName,
            completedModelOffset + 1,
            totalModelCount,
            totalBytes > 0 ? completedBytes / (double)totalBytes : null,
            completedBytes,
            totalBytes));
    }

    private async Task<string> EnsureModelInstalledAsync(
        WhisperModelConfiguration model,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string modelPath = GetModelPath(model);
        if (IsModelFileInstalled(model, modelPath))
        {
            return modelPath;
        }

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsModelFileInstalled(model, modelPath))
            {
                return modelPath;
            }

            string modelDirectory = Path.GetDirectoryName(modelPath)
                ?? throw new InvalidOperationException("Der Whisper-Modellpfad besitzt keinen Ordner.");
            Directory.CreateDirectory(modelDirectory);

            string temporaryPath = modelPath + ".download";
            TryDelete(temporaryPath);
            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.DownloadingModel,
                Fraction: 0,
                TotalBytes: model.ExpectedSize));

            try
            {
                using Stream modelStream = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(
                        model.GgmlType,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                long totalLength = TryGetLength(modelStream) ?? model.ExpectedSize;
                long copied = 0;
                var downloadStopwatch = Stopwatch.StartNew();
                TimeSpan lastProgressReport = TimeSpan.Zero;

                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int bytesRead;
                    while ((bytesRead = await modelStream
                               .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        await output
                            .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                        copied += bytesRead;

                        TimeSpan elapsed = downloadStopwatch.Elapsed;
                        if (elapsed - lastProgressReport >= ProgressReportInterval
                            || copied >= totalLength)
                        {
                            double bytesPerSecond = elapsed.TotalSeconds > 0
                                ? copied / elapsed.TotalSeconds
                                : 0;
                            progress?.Report(new TranscriptionProgress(
                                TranscriptionStage.DownloadingModel,
                                Fraction: Math.Clamp(copied / (double)totalLength, 0, 1),
                                DownloadedBytes: copied,
                                TotalBytes: totalLength,
                                BytesPerSecond: bytesPerSecond));
                            lastProgressReport = elapsed;
                        }
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (new FileInfo(temporaryPath).Length < model.MinimumValidSize)
                {
                    throw new InvalidDataException("Das heruntergeladene Whisper-Modell ist unvollständig.");
                }

                File.Move(temporaryPath, modelPath, overwrite: true);
                return modelPath;
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private void RemoveUnsupportedModels()
    {
        string modelDirectory = Path.Combine(paths.ModelsDirectory, "Whisper");
        TryDelete(Path.Combine(modelDirectory, "ggml-small.bin"));
        TryDelete(Path.Combine(modelDirectory, "ggml-small-q5_1.bin"));
        TryDelete(Path.Combine(modelDirectory, "ggml-large-v3-turbo.bin"));
    }

    private string GetModelPath(WhisperModelConfiguration model) =>
        Path.Combine(paths.ModelsDirectory, "Whisper", model.FileName);

    private static bool IsModelFileInstalled(
        WhisperModelConfiguration model,
        string modelPath) =>
        File.Exists(modelPath)
        && new FileInfo(modelPath).Length >= model.MinimumValidSize;

    private static long? TryGetLength(Stream stream)
    {
        try
        {
            return stream.CanSeek ? stream.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // A later attempt can overwrite an abandoned temporary download.
        }
    }

    private sealed record WhisperModelConfiguration(
        string DisplayName,
        string FileName,
        GgmlType GgmlType,
        long MinimumValidSize,
        long ExpectedSize);
}

internal sealed class WhisperTranscriptionEngine(
    WhisperModelManager modelManager)
    : IWhisperTranscriptionEngine
{
    internal static readonly int ProcessorThreadCount =
        Math.Max(1, Environment.ProcessorCount);
    internal static readonly IReadOnlyList<string> GpuRuntimeOrder =
        ["Cuda", "Cuda12", "Vulkan"];
    private static readonly JsonSerializerOptions WorkerJsonOptions =
        new(JsonSerializerDefaults.Web);

    public bool IsModelInstalled => modelManager.IsInstalled;

    public string ModelDisplayName => modelManager.ModelDisplayName;

    public long ModelDownloadSizeBytes => modelManager.ModelDownloadSizeBytes;

    public string LastRuntimeDescription { get; private set; } =
        $"CPU · {ProcessorThreadCount} Threads";

    public async Task<WhisperTranscriptionResult> TranscribeAsync(
        string normalizedWavePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedWavePath);

        string modelPath = await modelManager
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        TimeSpan audioDuration;
        using (var waveReader = new WaveFileReader(normalizedWavePath))
        {
            audioDuration = waveReader.TotalTime;
        }

        progress?.Report(new TranscriptionProgress(
            TranscriptionStage.Transcribing,
            Fraction: 0));

        RawWhisperResult rawResult;
        WorkerAttempt workerAttempt = await TryTranscribeOnGpuAsync(
                modelPath,
                normalizedWavePath,
                audioDuration,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (workerAttempt.Result is not null)
        {
            LastRuntimeDescription =
                $"{workerAttempt.Result.Runtime} · GPU";
            Debug.WriteLine(
                $"Whisper runtime: {LastRuntimeDescription}; " +
                $"CPU-Threads: {ProcessorThreadCount}");
            rawResult = new RawWhisperResult(
                workerAttempt.Result.Language,
                workerAttempt.Result.Segments
                    .Select(segment => new WhisperTranscriptSegment(
                        segment.Start,
                        segment.End,
                        segment.Text))
                    .ToArray());
        }
        else
        {
            Debug.WriteLine(
                "Whisper GPU runtimes unavailable; using safe CPU fallback. "
                + workerAttempt.Error);
            rawResult = await TranscribeOnCpuAsync(
                    modelPath,
                    normalizedWavePath,
                    audioDuration,
                    progress,
                    cancellationToken,
                    isFallback: true)
                .ConfigureAwait(false);
        }

        WhisperTranscriptSegment[] cleanedSegments = rawResult.Segments
            .Select(segment => segment with
            {
                Text = TranscriptRepetitionFilter.CollapseAdjacentPhrases(
                    segment.Text)
            })
            .ToArray();
        TranscriptFilterResult<WhisperTranscriptSegment> filtered =
            TranscriptRepetitionFilter.Filter(
                cleanedSegments,
                segment => segment.Text,
                segment => segment.Start);

        progress?.Report(new TranscriptionProgress(
            TranscriptionStage.Transcribing,
            Fraction: 1,
            SegmentCount: filtered.Items.Count,
            SuppressedSegmentCount: filtered.SuppressedCount));
        return new WhisperTranscriptionResult(
            rawResult.Language,
            filtered.Items,
            filtered.SuppressedCount);
    }

    private async Task<RawWhisperResult> TranscribeOnCpuAsync(
        string modelPath,
        string normalizedWavePath,
        TimeSpan audioDuration,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken,
        bool isFallback = false)
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        WhisperFactoryOptions factoryOptions = WhisperFactoryOptions.Default;
        factoryOptions.UseGpu = false;

        using WhisperFactory factory = WhisperFactory.FromPath(
            modelPath,
            factoryOptions);
        LastRuntimeDescription = isFallback
            ? $"CPU-Fallback · {ProcessorThreadCount} Threads"
            : $"CPU · {ProcessorThreadCount} Threads";
        Debug.WriteLine(
            $"Whisper runtime: {RuntimeOptions.LoadedLibrary?.ToString() ?? "CPU"}; " +
            $"Modus: {LastRuntimeDescription}");
        var rawSegments = new List<WhisperTranscriptSegment>();
        var languages = new List<string>();
        string? lockedLanguage = null;
        IReadOnlyList<WhisperAudioChunk> chunks =
            WhisperAudioChunking.Plan(audioDuration);
        foreach (WhisperAudioChunk chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using WhisperProcessor processor = CreateProcessor(
                factory,
                ProcessorThreadCount,
                lockedLanguage);
            float[] samples = ReadChunkSamples(normalizedWavePath, chunk);
            var chunkLanguages = new List<string>();
            await foreach (SegmentData segment in processor.ProcessAsync(
                               samples,
                               cancellationToken))
            {
                string text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                TimeSpan relativeStart = Clamp(
                    segment.Start,
                    TimeSpan.Zero,
                    chunk.Duration);
                TimeSpan relativeEnd = Clamp(
                    segment.End < segment.Start ? segment.Start : segment.End,
                    relativeStart,
                    chunk.Duration);
                TimeSpan absoluteStart = chunk.Start + relativeStart;
                TimeSpan absoluteEnd = chunk.Start + relativeEnd;
                if (!WhisperAudioChunking.OwnsSegment(
                        chunk,
                        absoluteStart,
                        absoluteEnd,
                        audioDuration))
                {
                    continue;
                }

                rawSegments.Add(new WhisperTranscriptSegment(
                    absoluteStart,
                    absoluteEnd,
                    text));
                if (!string.IsNullOrWhiteSpace(segment.Language))
                {
                    string language = segment.Language.Trim();
                    languages.Add(language);
                    chunkLanguages.Add(language);
                }
            }

            lockedLanguage ??= chunkLanguages
                .GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();

            double? fraction = audioDuration > TimeSpan.Zero
                ? Math.Clamp(
                    chunk.OwnedEnd.TotalMilliseconds
                    / audioDuration.TotalMilliseconds,
                    0,
                    1)
                : null;
            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.Transcribing,
                Fraction: fraction,
                SegmentCount: rawSegments.Count));
        }

        string? detectedLanguage = languages
            .GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        return new RawWhisperResult(detectedLanguage, rawSegments);
    }

    private static WhisperProcessor CreateProcessor(
        WhisperFactory factory,
        int threadCount,
        string? language)
    {
        WhisperProcessorBuilder builder = factory
            .CreateBuilder()
            .WithThreads(threadCount)
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithEntropyThreshold(2.8f)
            .WithNoSpeechThreshold(0.6f);
        if (string.IsNullOrWhiteSpace(language))
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(language);
        }

        return builder.Build();
    }

    private static float[] ReadChunkSamples(
        string normalizedWavePath,
        WhisperAudioChunk chunk)
    {
        using var reader = new WaveFileReader(normalizedWavePath);
        WaveFormat format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm
            || format.BitsPerSample != 16
            || format.Channels != 1
            || format.SampleRate != 16_000)
        {
            throw new InvalidDataException(
                "Whisper chunking requires 16 kHz mono PCM16 audio.");
        }

        long startFrame = checked((long)Math.Round(
            chunk.Start.TotalSeconds * format.SampleRate));
        long requestedFrames = checked((long)Math.Round(
            chunk.Duration.TotalSeconds * format.SampleRate));
        long availableFrames = Math.Max(0, reader.SampleCount - startFrame);
        requestedFrames = Math.Min(requestedFrames, availableFrames);
        long bytePosition = checked(startFrame * format.BlockAlign);
        reader.Position = bytePosition;
        if (reader.Position != bytePosition)
        {
            throw new InvalidDataException(
                $"Whisper audio block {chunk.Index} could not seek to PCM frame {startFrame}.");
        }

        int requestedBytes = checked((int)(requestedFrames * format.BlockAlign));
        byte[] bytes = new byte[requestedBytes];
        int bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            int read = reader.Read(bytes, bytesRead, bytes.Length - bytesRead);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead != requestedBytes)
        {
            throw new EndOfStreamException(
                $"Whisper audio block {chunk.Index} expected {requestedBytes} PCM bytes " +
                $"but read {bytesRead}.");
        }

        int sampleCount = bytesRead / sizeof(short);
        var samples = new float[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)));
            samples[index] = sample / 32768f;
        }

        return samples;
    }

    private static TimeSpan Clamp(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static async Task<WorkerAttempt> TryTranscribeOnGpuAsync(
        string modelPath,
        string normalizedWavePath,
        TimeSpan audioDuration,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (string runtime in GpuRuntimeOrder)
        {
            WorkerAttempt attempt = await TryTranscribeWithGpuRuntimeAsync(
                    modelPath,
                    normalizedWavePath,
                    audioDuration,
                    runtime,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (attempt.Result is not null)
            {
                return attempt;
            }

            errors.Add($"{runtime}: {attempt.Error ?? "unknown error"}");
        }

        return new WorkerAttempt(null, string.Join(" | ", errors));
    }

    private static async Task<WorkerAttempt> TryTranscribeWithGpuRuntimeAsync(
        string modelPath,
        string normalizedWavePath,
        TimeSpan audioDuration,
        string runtime,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "WhisperWorker",
            "MeetingRecall.TranscriptionWorker.exe");
        if (!File.Exists(workerPath))
        {
            return new WorkerAttempt(
                null,
                $"GPU worker not found: {workerPath}");
        }

        string resultPath = Path.Combine(
            Path.GetTempPath(),
            $"MeetingRecall-whisper-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add(normalizedWavePath);
        startInfo.ArgumentList.Add(resultPath);
        startInfo.ArgumentList.Add(ProcessorThreadCount.ToString());
        startInfo.ArgumentList.Add(runtime);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new WorkerAttempt(null, "GPU worker could not be started.");
            }

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task progressTask = PumpWorkerProgressAsync(
                process.StandardOutput,
                audioDuration,
                progress,
                cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                throw;
            }

            string error = await errorTask.ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(resultPath))
            {
                return new WorkerAttempt(
                    null,
                    $"GPU worker exit code {process.ExitCode}: {error.Trim()}");
            }

            await using FileStream stream = File.OpenRead(resultPath);
            WorkerResult? result = await JsonSerializer.DeserializeAsync<WorkerResult>(
                    stream,
                    WorkerJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return result is null
                ? new WorkerAttempt(null, "GPU worker returned no result.")
                : new WorkerAttempt(result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WorkerAttempt(null, exception.Message);
        }
        finally
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".partial");
        }
    }

    private static async Task PumpWorkerProgressAsync(
        StreamReader output,
        TimeSpan audioDuration,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false)
               is { } line)
        {
            string[] values = line.Split('\t');
            if (values.Length != 2
                || !long.TryParse(values[0], out long endTicks)
                || !int.TryParse(values[1], out int segmentCount))
            {
                continue;
            }

            double? fraction = audioDuration > TimeSpan.Zero
                ? Math.Clamp(
                    endTicks / (double)audioDuration.Ticks,
                    0,
                    1)
                : null;
            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.Transcribing,
                Fraction: fraction,
                SegmentCount: segmentCount));
        }
    }

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
            File.Delete(path);
        }
        catch
        {
            // Temporary worker results are best-effort cleanup only.
        }
    }

    private sealed record RawWhisperResult(
        string? Language,
        IReadOnlyList<WhisperTranscriptSegment> Segments);

    private sealed record WorkerAttempt(
        WorkerResult? Result,
        string? Error);

    private sealed record WorkerResult(
        string Runtime,
        string? Language,
        IReadOnlyList<WorkerSegment> Segments);

    private sealed record WorkerSegment(
        TimeSpan Start,
        TimeSpan End,
        string Text);
}

internal sealed class LocalTranscriptionService(
    IConversationRepository conversationRepository,
    ISpeakerRepository speakerRepository,
    IAudioArchiveRepository audioArchiveRepository,
    IAudioTranscriptionNormalizer normalizer,
    IWhisperTranscriptionEngine whisperEngine,
    AppDataPaths paths,
    SpeakerDiarizationModelManager? speakerModelManager = null,
    ISpeakerDiarizationService? diarizationService = null,
    IMeetingSummaryRepository? summaryRepository = null) : ILocalTranscriptionService
{
    private const double MaximumMergedSegmentSeconds = 15;
    private static readonly TimeSpan MaximumMergeGap =
        TimeSpan.FromMilliseconds(800);
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);

    public bool IsModelInstalled => whisperEngine.IsModelInstalled;

    public string ModelDisplayName => whisperEngine.ModelDisplayName;

    public long ModelDownloadSizeBytes => whisperEngine.ModelDownloadSizeBytes;

    public bool IsSpeakerDiarizationModelInstalled =>
        speakerModelManager?.IsInstalled ?? true;

    public long SpeakerDiarizationDownloadSizeBytes =>
        speakerModelManager?.DownloadSizeBytes ?? 0;

    public string LastRuntimeDescription =>
        whisperEngine.LastRuntimeDescription;

    public async Task<Conversation> TranscribeAsync(
        Guid conversationId,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Eine Transkription benötigt ein Gespräch.", nameof(conversationId));
        }

        await _transcriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Conversation? conversation = null;
        string? workingDirectory = null;

        try
        {
            if (speakerModelManager is not null)
            {
                await speakerModelManager
                    .EnsureInstalledAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            conversation = await conversationRepository
                .GetByIdAsync(conversationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Das ausgewählte Gespräch wurde nicht gefunden.");

            AudioTrack track = await audioArchiveRepository
                .GetPrimaryAsync(conversationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Für dieses Gespräch wurde keine Hauptaudiospur gefunden.");

            conversation.SetProcessingState(ConversationProcessingState.Processing);
            await conversationRepository
                .UpsertAsync(conversation, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new TranscriptionProgress(TranscriptionStage.PreparingAudio));
            workingDirectory = Path.Combine(
                paths.TempDirectory,
                "Transcription",
                conversationId.ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            string sourcePath = paths.ToAbsolutePath(track.RelativePath);
            string normalizedPath = Path.Combine(workingDirectory, "whisper-input.wav");
            await normalizer
                .NormalizeAsync(sourcePath, normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            AudioTranscriptionNormalizer.ValidateNormalizedDuration(
                normalizedPath,
                track.Duration);

            WhisperTranscriptionResult result = await whisperEngine
                .TranscribeAsync(normalizedPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Segments.Count == 0)
            {
                throw new InvalidOperationException(
                    "Whisper konnte in der Audiodatei keinen gesprochenen Text erkennen.");
            }

            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.AnalyzingSpeakers,
                SegmentCount: result.Segments.Count,
                SuppressedSegmentCount: result.SuppressedRepetitionSegments));

            IReadOnlyList<Guid> speakerIds;
            if (diarizationService is not null)
            {
                SpeakerDiarizationResult diarization = await diarizationService
                    .AnalyzeAsync(
                        normalizedPath,
                        result.Segments,
                        cancellationToken)
                    .ConfigureAwait(false);
                speakerIds = diarization.SpeakerIds;
            }
            else
            {
                Guid fallbackSpeakerId = conversation.TranscriptSegments
                    .FirstOrDefault()?.SpeakerId
                    ?? conversation.SpeakerIds.FirstOrDefault();

                if (fallbackSpeakerId == Guid.Empty)
                {
                    IReadOnlyList<Speaker> existingSpeakers = await speakerRepository
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    int sequence = Speaker.FindNextAvailableUnknownSequence(
                        existingSpeakers);
                    var fallbackSpeaker = Speaker.CreateUnknown(
                        Guid.NewGuid(),
                        sequence);
                    fallbackSpeakerId = fallbackSpeaker.Id;
                    await speakerRepository
                        .UpsertAsync(fallbackSpeaker, cancellationToken)
                        .ConfigureAwait(false);
                }

                speakerIds = Enumerable
                    .Repeat(fallbackSpeakerId, result.Segments.Count)
                    .ToArray();
            }

            if (speakerIds.Count != result.Segments.Count)
            {
                throw new InvalidDataException(
                    "Die lokale Sprecheranalyse lieferte keine vollständige Segmentzuordnung.");
            }

            TranscriptSegment[] transcriptSegments = BuildTranscriptSegments(
                result.Segments,
                speakerIds,
                mergeAdjacentSpeakerSegments: diarizationService is not null);
            TranscriptFilterResult<TranscriptSegment> finalFilter =
                TranscriptRepetitionFilter.Filter(
                    transcriptSegments,
                    segment => segment.CurrentText,
                    segment => segment.StartTime);
            transcriptSegments = finalFilter.Items.ToArray();
            int totalSuppressedSegments = result.SuppressedRepetitionSegments
                + finalFilter.SuppressedCount;

            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.Saving,
                SegmentCount: transcriptSegments.Length,
                SuppressedSegmentCount: totalSuppressedSegments));

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Conversation updatedConversation = Conversation.Restore(
                conversation.Id,
                conversation.Title,
                conversation.RecordedAt,
                conversation.Source,
                ConversationProcessingState.Ready,
                conversation.Duration,
                string.IsNullOrWhiteSpace(result.Language)
                    ? conversation.DetectedLanguage
                    : result.Language,
                conversation.CreatedAtUtc,
                now,
                conversation.GroupIds,
                transcriptSegments
                    .Select(segment => segment.SpeakerId)
                    .Distinct(),
                transcriptSegments);

            await conversationRepository
                .UpsertAsync(updatedConversation, cancellationToken)
                .ConfigureAwait(false);
            await PruneObsoleteUnknownSpeakersAsync(
                    conversation.SpeakerIds,
                    updatedConversation.SpeakerIds,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (summaryRepository is not null)
            {
                await summaryRepository
                    .DeleteAsync(updatedConversation.Id, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.Completed,
                1,
                transcriptSegments.Length,
                SuppressedSegmentCount: totalSuppressedSegments));
            return updatedConversation;
        }
        catch (OperationCanceledException)
        {
            await TryPersistStateAsync(
                conversation,
                ConversationProcessingState.Pending)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryPersistStateAsync(
                conversation,
                ConversationProcessingState.Failed)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                TryDeleteDirectory(workingDirectory);
            }

            _transcriptionGate.Release();
        }
    }

    private static TranscriptSegment[] BuildTranscriptSegments(
        IReadOnlyList<WhisperTranscriptSegment> segments,
        IReadOnlyList<Guid> speakerIds,
        bool mergeAdjacentSpeakerSegments)
    {
        AssignedTranscriptPart[] ordered = segments
            .Select((segment, index) => new AssignedTranscriptPart(
                speakerIds[index],
                segment.Start,
                segment.End,
                segment.Text.Trim()))
            .OrderBy(part => part.Start)
            .ThenBy(part => part.End)
            .ToArray();
        var merged = new List<AssignedTranscriptPart>(ordered.Length);

        foreach (AssignedTranscriptPart part in ordered)
        {
            if (mergeAdjacentSpeakerSegments
                && merged.Count > 0
                && CanMerge(merged[^1], part))
            {
                AssignedTranscriptPart previous = merged[^1];
                merged[^1] = previous with
                {
                    End = Max(previous.End, part.End),
                    Text = JoinTranscriptText(previous.Text, part.Text)
                };
                continue;
            }

            merged.Add(part);
        }

        var seamCleaned = new List<AssignedTranscriptPart>(merged.Count);
        foreach (AssignedTranscriptPart part in merged)
        {
            string text = TranscriptRepetitionFilter.CollapseAdjacentPhrases(
                part.Text);
            if (seamCleaned.Count > 0)
            {
                text = TranscriptRepetitionFilter.TrimLeadingWordOverlap(
                    seamCleaned[^1].Text,
                    text);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                seamCleaned.Add(part with { Text = text });
            }
        }

        return seamCleaned
            .Select(part => new TranscriptSegment(
                Guid.NewGuid(),
                part.SpeakerId,
                part.Start,
                part.End,
                part.Text))
            .ToArray();
    }

    private static bool CanMerge(
        AssignedTranscriptPart previous,
        AssignedTranscriptPart current)
    {
        if (previous.SpeakerId != current.SpeakerId)
        {
            return false;
        }

        TimeSpan gap = current.Start - previous.End;
        TimeSpan mergedEnd = Max(previous.End, current.End);
        return gap <= MaximumMergeGap
               && (mergedEnd - previous.Start).TotalSeconds
               <= MaximumMergedSegmentSeconds;
    }

    private static string JoinTranscriptText(string left, string right)
    {
        string leftText = left.TrimEnd();
        string rightText = right.Trim();
        if (leftText.Length == 0)
        {
            return rightText;
        }

        if (rightText.Length == 0)
        {
            return leftText;
        }

        const string punctuationWithoutLeadingSpace = ".,!?;:%)]}»”";
        return punctuationWithoutLeadingSpace.Contains(rightText[0])
            ? leftText + rightText
            : leftText + " " + rightText;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;

    private sealed record AssignedTranscriptPart(
        Guid SpeakerId,
        TimeSpan Start,
        TimeSpan End,
        string Text);

    private async Task PruneObsoleteUnknownSpeakersAsync(
        IEnumerable<Guid> previousSpeakerIds,
        IEnumerable<Guid> currentSpeakerIds,
        CancellationToken cancellationToken)
    {
        Guid[] obsoleteIds = previousSpeakerIds
            .Except(currentSpeakerIds)
            .Distinct()
            .ToArray();
        if (obsoleteIds.Length == 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<Conversation> activeConversations =
                await conversationRepository
                    .ListAsync(cancellationToken)
                    .ConfigureAwait(false);
            HashSet<Guid> referencedIds = activeConversations
                .SelectMany(item => item.SpeakerIds)
                .ToHashSet();

            foreach (Guid obsoleteId in obsoleteIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (referencedIds.Contains(obsoleteId))
                {
                    continue;
                }

                Speaker? speaker = await speakerRepository
                    .GetByIdAsync(obsoleteId, cancellationToken)
                    .ConfigureAwait(false);
                if (speaker is null || speaker.IsNamed)
                {
                    continue;
                }

                await speakerRepository
                    .DeleteAsync(obsoleteId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The new transcript is already safe. A stale, unreferenced
            // unknown profile can still be removed from the profiles page.
        }
    }

    private async Task TryPersistStateAsync(
        Conversation? conversation,
        ConversationProcessingState processingState)
    {
        if (conversation is null)
        {
            return;
        }

        try
        {
            conversation.SetProcessingState(processingState);
            await conversationRepository
                .UpsertAsync(conversation, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original transcription or cancellation exception.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary files can be removed on a later application start.
        }
    }
}
