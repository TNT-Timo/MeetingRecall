using System.Diagnostics;
using System.Security.Cryptography;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Infrastructure.Storage;
using NAudio.Wave;
using SherpaOnnx;

namespace MeetingRecall.Infrastructure.Transcription;

internal static class SpeakerEmbeddingModels
{
    public const string TitaNetLarge = "nvidia-titanet-large-v1";
    public const string LegacyAcoustic = "meetingrecall-acoustic-v1";
    public const string Unversioned = "unversioned";
    public const int TitaNetLargeVectorSize = 192;
}

internal sealed record NeuralSpeakerTurn(
    TimeSpan Start,
    TimeSpan End,
    int SpeakerIndex);

internal sealed record NeuralSpeakerDiarizationResult(
    IReadOnlyList<NeuralSpeakerTurn> Turns,
    IReadOnlyDictionary<int, double[]> Embeddings,
    string EmbeddingModelId = SpeakerEmbeddingModels.TitaNetLarge);

internal interface INeuralSpeakerDiarizationEngine
{
    bool IsAvailable { get; }

    Task<NeuralSpeakerDiarizationResult> AnalyzeAsync(
        string normalizedWavePath,
        CancellationToken cancellationToken = default);
}

internal sealed class SpeakerDiarizationModelManager : IDisposable
{
    private const string ModelDirectoryName = "SpeakerDiarization";
    private const string ObsoleteEmbeddingModelFileName =
        "3dspeaker-eres2net-base-16k.onnx";
    private static readonly TimeSpan ProgressInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly SpeakerModelFile SegmentationModel = new(
        "pyannote segmentation 3.0 INT8",
        "pyannote-segmentation-3.0.int8.onnx",
        new Uri(
            "https://huggingface.co/csukuangfj/"
            + "sherpa-onnx-pyannote-segmentation-3-0/resolve/"
            + "340b52f1f5cd12d45a30fa284691417eaad2ff92/"
            + "model.int8.onnx?download=true"),
        MinimumValidSize: 1_400_000,
        ApproximateSize: 1_540_000,
        Sha256: "10a438c2e0d90ed5f5da545cec2244d887315f6dbbbf1d3d564d00745b01952e");
    private static readonly SpeakerModelFile EmbeddingModel = new(
        "NVIDIA NeMo TitaNet-Large",
        "nemo-en-titanet-large.onnx",
        new Uri(
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/"
            + "speaker-recongition-models/nemo_en_titanet_large.onnx"),
        MinimumValidSize: 100_000_000,
        ApproximateSize: 101_405_493,
        Sha256: "d51abcf31717ef28162f26acb9d44dd4127c3d44c9b8624f699f3425daca8e77");

    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private bool _disposed;

    public SpeakerDiarizationModelManager(AppDataPaths paths)
    {
        _paths = paths;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MeetingRecall/1.0 local-speaker-model-downloader");
    }

    public long DownloadSizeBytes =>
        SegmentationModel.ApproximateSize + EmbeddingModel.ApproximateSize;

    public bool IsInstalled
    {
        get
        {
            try
            {
                return IsFileInstalled(SegmentationModel)
                       && IsFileInstalled(EmbeddingModel);
            }
            catch
            {
                return false;
            }
        }
    }

    public (string SegmentationPath, string EmbeddingPath) GetInstalledPaths()
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException(
                "Die lokalen Modelle für die Sprechererkennung sind nicht vollständig installiert.");
        }

        return (GetPath(SegmentationModel), GetPath(EmbeddingModel));
    }

    public async Task EnsureInstalledAsync(
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInstalled)
        {
            RemoveObsoleteEmbeddingModel();
            return;
        }

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
            {
                RemoveObsoleteEmbeddingModel();
                return;
            }

            Directory.CreateDirectory(GetModelDirectory());
            long completedBytes = IsFileInstalled(SegmentationModel)
                ? SegmentationModel.ApproximateSize
                : 0;
            if (!IsFileInstalled(SegmentationModel))
            {
                await DownloadAsync(
                        SegmentationModel,
                        completedBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                completedBytes += SegmentationModel.ApproximateSize;
            }

            if (!IsFileInstalled(EmbeddingModel))
            {
                await DownloadAsync(
                        EmbeddingModel,
                        completedBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            RemoveObsoleteEmbeddingModel();

            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.DownloadingSpeakerModel,
                Fraction: 1,
                DownloadedBytes: DownloadSizeBytes,
                TotalBytes: DownloadSizeBytes));
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task DownloadAsync(
        SpeakerModelFile model,
        long completedBytes,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string destinationPath = GetPath(model);
        string partialPath = destinationPath + ".download";
        TryDelete(partialPath);

        progress?.Report(new TranscriptionProgress(
            TranscriptionStage.DownloadingSpeakerModel,
            Fraction: completedBytes / (double)DownloadSizeBytes,
            DownloadedBytes: completedBytes,
            TotalBytes: DownloadSizeBytes));

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(
                    model.Uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long downloaded = 0;
            string actualHash;
            await using (Stream input = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (IncrementalHash hash =
                   IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var stopwatch = Stopwatch.StartNew();
                TimeSpan lastReport = TimeSpan.Zero;
                var buffer = new byte[1024 * 1024];
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

                    if (stopwatch.Elapsed - lastReport >= ProgressInterval)
                    {
                        long overallBytes = completedBytes + downloaded;
                        progress?.Report(new TranscriptionProgress(
                            TranscriptionStage.DownloadingSpeakerModel,
                            Fraction: Math.Clamp(
                                overallBytes / (double)DownloadSizeBytes,
                                0,
                                1),
                            DownloadedBytes: overallBytes,
                            TotalBytes: DownloadSizeBytes,
                            BytesPerSecond: stopwatch.Elapsed.TotalSeconds > 0
                                ? downloaded / stopwatch.Elapsed.TotalSeconds
                                : null));
                        lastReport = stopwatch.Elapsed;
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                actualHash = Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant();
            }

            if (downloaded < model.MinimumValidSize)
            {
                throw new InvalidDataException(
                    $"Das heruntergeladene Modell „{model.DisplayName}“ ist unvollständig.");
            }

            if (!string.Equals(
                    actualHash,
                    model.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Die Prüfsumme des Modells „{model.DisplayName}“ ist ungültig.");
            }

            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private bool IsFileInstalled(SpeakerModelFile model)
    {
        string path = GetPath(model);
        return File.Exists(path)
               && new FileInfo(path).Length >= model.MinimumValidSize;
    }

    private string GetModelDirectory() =>
        Path.Combine(_paths.ModelsDirectory, ModelDirectoryName);

    private string GetPath(SpeakerModelFile model) =>
        Path.Combine(GetModelDirectory(), model.FileName);

    private void RemoveObsoleteEmbeddingModel()
    {
        string obsoletePath = Path.Combine(
            GetModelDirectory(),
            ObsoleteEmbeddingModelFileName);
        TryDelete(obsoletePath);
        TryDelete(obsoletePath + ".download");
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
            // A later download can clean up an abandoned temporary file.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _downloadGate.Dispose();
    }

    private sealed record SpeakerModelFile(
        string DisplayName,
        string FileName,
        Uri Uri,
        long MinimumValidSize,
        long ApproximateSize,
        string Sha256);
}

internal sealed class SherpaOnnxSpeakerDiarizationEngine(
    SpeakerDiarizationModelManager modelManager)
    : INeuralSpeakerDiarizationEngine
{
    private const int RequiredSampleRate = 16_000;
    private const int EmbeddingWindowSeconds = 9;
    private const int MaximumEmbeddingWindows = 5;
    private const int MinimumEmbeddingMilliseconds = 1_500;
    private const int MinimumExclusiveFragmentMilliseconds = 250;
    private const double MinimumEmbeddingRms = 0.0005;
    private const double MinimumWindowConsensusSimilarity = 0.55;
    private static readonly TimeSpan EmbeddingBoundaryTrim =
        TimeSpan.FromMilliseconds(150);

    public bool IsAvailable => modelManager.IsInstalled;

    public Task<NeuralSpeakerDiarizationResult> AnalyzeAsync(
        string normalizedWavePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedWavePath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => AnalyzeCore(normalizedWavePath, cancellationToken),
            cancellationToken);
    }

    private NeuralSpeakerDiarizationResult AnalyzeCore(
        string normalizedWavePath,
        CancellationToken cancellationToken)
    {
        (string segmentationPath, string embeddingPath) =
            modelManager.GetInstalledPaths();
        float[] samples = ReadNormalizedSamples(normalizedWavePath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            NeuralSpeakerDiarizationResult result = AnalyzeWithProvider(
                segmentationPath,
                embeddingPath,
                samples,
                provider: "cuda",
                cancellationToken);
            Debug.WriteLine("Speaker runtime: CUDA GPU.");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception gpuException)
        {
            Debug.WriteLine(
                "Speaker CUDA runtime unavailable; using CPU fallback. "
                + gpuException);
            return AnalyzeWithProvider(
                segmentationPath,
                embeddingPath,
                samples,
                provider: "cpu",
                cancellationToken);
        }
    }

    private static NeuralSpeakerDiarizationResult AnalyzeWithProvider(
        string segmentationPath,
        string embeddingPath,
        float[] samples,
        string provider,
        CancellationToken cancellationToken)
    {
        int numThreads = string.Equals(
            provider,
            "cpu",
            StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, Environment.ProcessorCount - 1)
            : 2;
        var diarizationConfig = new OfflineSpeakerDiarizationConfig();
        diarizationConfig.Segmentation.Pyannote.Model = segmentationPath;
        diarizationConfig.Segmentation.NumThreads = numThreads;
        diarizationConfig.Segmentation.Debug = 0;
        diarizationConfig.Segmentation.Provider = provider;
        diarizationConfig.Embedding.Model = embeddingPath;
        diarizationConfig.Embedding.NumThreads = numThreads;
        diarizationConfig.Embedding.Debug = 0;
        diarizationConfig.Embedding.Provider = provider;
        diarizationConfig.Clustering.Threshold = 0.78F;
        diarizationConfig.MinDurationOn = 0.3F;
        diarizationConfig.MinDurationOff = 0.5F;

        using var diarizer = new OfflineSpeakerDiarization(diarizationConfig);
        var rawTurns = diarizer.Process(samples);
        var turns = rawTurns
            .Where(turn => turn.End > turn.Start)
            .Select(turn => new NeuralSpeakerTurn(
                TimeSpan.FromSeconds(turn.Start),
                TimeSpan.FromSeconds(turn.End),
                turn.Speaker))
            .OrderBy(turn => turn.Start)
            .ThenBy(turn => turn.End)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var embeddingConfig = new SpeakerEmbeddingExtractorConfig
        {
            Model = embeddingPath,
            NumThreads = numThreads,
            Debug = 0,
            Provider = provider
        };
        using var extractor = new SpeakerEmbeddingExtractor(embeddingConfig);
        var embeddings = new Dictionary<int, double[]>();

        foreach (IGrouping<int, NeuralSpeakerTurn> speakerTurns in
                 turns.GroupBy(turn => turn.SpeakerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            double[]? embedding = ComputeRobustSpeakerEmbedding(
                extractor,
                samples,
                turns,
                speakerTurns.Key,
                cancellationToken);
            if (embedding is null)
            {
                continue;
            }

            embeddings[speakerTurns.Key] = embedding;
        }

        return new NeuralSpeakerDiarizationResult(
            turns,
            embeddings,
            SpeakerEmbeddingModels.TitaNetLarge);
    }

    private static double[]? ComputeRobustSpeakerEmbedding(
        SpeakerEmbeddingExtractor extractor,
        IReadOnlyList<float> source,
        IReadOnlyList<NeuralSpeakerTurn> turns,
        int speakerIndex,
        CancellationToken cancellationToken)
    {
        int maximumSamples = RequiredSampleRate
                             * EmbeddingWindowSeconds
                             * MaximumEmbeddingWindows;
        float[] voiceSamples = CollectExclusiveSpeakerSamples(
            source,
            turns,
            speakerIndex,
            maximumSamples);
        int minimumSamples = RequiredSampleRate
                             * MinimumEmbeddingMilliseconds
                             / 1_000;
        if (voiceSamples.Length < minimumSamples)
        {
            return null;
        }

        int windowSize = RequiredSampleRate * EmbeddingWindowSeconds;
        var candidates = new List<EmbeddingCandidate>(
            MaximumEmbeddingWindows);
        for (int offset = 0;
             offset + minimumSamples <= voiceSamples.Length
             && candidates.Count < MaximumEmbeddingWindows;
             offset += windowSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(windowSize, voiceSamples.Length - offset);
            float[] window = voiceSamples[offset..(offset + count)];
            if (!HasUsableEnergy(window))
            {
                continue;
            }

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(RequiredSampleRate, window);
            stream.InputFinished();
            float[] rawEmbedding = extractor.Compute(stream);
            if (rawEmbedding.Length != SpeakerEmbeddingModels.TitaNetLargeVectorSize)
            {
                throw new InvalidDataException(
                    "TitaNet-Large hat eine unerwartete Embedding-Größe geliefert.");
            }

            double[]? normalized = NormalizeEmbedding(rawEmbedding);
            if (normalized is not null)
            {
                candidates.Add(new EmbeddingCandidate(
                    normalized,
                    count));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        EmbeddingCandidate medoid = candidates
            .OrderByDescending(candidate => WeightedAverageSimilarity(
                candidate,
                candidates))
            .First();
        EmbeddingCandidate[] consensus = candidates
            .Where(candidate =>
                CosineSimilarity(medoid.Embedding, candidate.Embedding)
                >= MinimumWindowConsensusSimilarity)
            .ToArray();
        if (candidates.Count > 1 && consensus.Length < 2)
        {
            return null;
        }

        return NormalizeEmbedding(consensus);
    }

    private static float[] ReadNormalizedSamples(string wavePath)
    {
        using var reader = new WaveFileReader(wavePath);
        if (reader.WaveFormat.SampleRate != RequiredSampleRate
            || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                "Die neuronale Sprecheranalyse benötigt 16-kHz-Mono-Audio.");
        }

        long sampleCount = reader.Length / reader.WaveFormat.BlockAlign;
        if (sampleCount <= 0 || sampleCount > int.MaxValue)
        {
            throw new InvalidDataException(
                "Die Audiodatei ist für die Sprecheranalyse ungültig oder zu lang.");
        }

        ISampleProvider provider = reader.ToSampleProvider();
        var samples = new float[(int)sampleCount];
        int offset = 0;
        while (offset < samples.Length)
        {
            int read = provider.Read(samples, offset, samples.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        for (int index = 0; index < offset; index++)
        {
            if (!float.IsFinite(samples[index]))
            {
                throw new InvalidDataException(
                    "Die Audiodatei enthält ungültige Samples für die Sprecheranalyse.");
            }
        }

        return offset == samples.Length ? samples : samples[..offset];
    }

    internal static float[] CollectExclusiveSpeakerSamples(
        IReadOnlyList<float> source,
        IReadOnlyList<NeuralSpeakerTurn> turns,
        int speakerIndex,
        int maximumSamples)
    {
        List<SampleRange> ownRanges = MergeRanges(turns
            .Where(turn => turn.SpeakerIndex == speakerIndex)
            .Select(turn => ToSampleRange(turn, trimBoundaries: true))
            .Where(range => range.Length > 0));
        List<SampleRange> otherRanges = MergeRanges(turns
            .Where(turn => turn.SpeakerIndex != speakerIndex)
            .Select(turn => ToSampleRange(turn, trimBoundaries: false))
            .Where(range => range.Length > 0));

        var exclusiveRanges = new List<SampleRange>();
        foreach (SampleRange ownRange in ownRanges)
        {
            List<SampleRange> available = [ownRange];
            foreach (SampleRange otherRange in otherRanges)
            {
                available = available
                    .SelectMany(range => Subtract(range, otherRange))
                    .ToList();
                if (available.Count == 0)
                {
                    break;
                }
            }

            exclusiveRanges.AddRange(available);
        }

        int minimumFragmentSamples = RequiredSampleRate
                                     * MinimumExclusiveFragmentMilliseconds
                                     / 1_000;
        SampleRange[] usableRanges = exclusiveRanges
            .Where(candidate => candidate.Length >= minimumFragmentSamples)
            .OrderBy(candidate => candidate.Start)
            .Select(range => new SampleRange(
                Math.Clamp(range.Start, 0, source.Count),
                Math.Clamp(range.End, 0, source.Count)))
            .Where(range => range.Length >= minimumFragmentSamples)
            .ToArray();
        long totalSamples = usableRanges.Sum(range => (long)range.Length);
        if (totalSamples <= 0)
        {
            return [];
        }

        var result = new List<float>((int)Math.Min(
            maximumSamples,
            totalSamples));
        if (totalSamples <= maximumSamples)
        {
            AppendVirtualSamples(
                source,
                usableRanges,
                virtualStart: 0,
                count: (int)totalSamples,
                destination: result);
            return result.ToArray();
        }

        // A long meeting should not build a permanent voiceprint from only its
        // first minutes. Sample equal windows across the full exclusive-speech
        // timeline so changes in microphone distance, energy and speaking style
        // are represented without increasing TitaNet's inference count.
        int windowSize = RequiredSampleRate * EmbeddingWindowSeconds;
        int windowCount = Math.Min(
            MaximumEmbeddingWindows,
            Math.Max(1, maximumSamples / windowSize));
        long maximumStart = Math.Max(0, totalSamples - windowSize);
        for (int windowIndex = 0;
             windowIndex < windowCount && result.Count < maximumSamples;
             windowIndex++)
        {
            long virtualStart = windowCount == 1
                ? maximumStart / 2
                : maximumStart * windowIndex / (windowCount - 1);
            int count = Math.Min(
                windowSize,
                maximumSamples - result.Count);
            AppendVirtualSamples(
                source,
                usableRanges,
                virtualStart,
                count,
                result);
        }

        return result.ToArray();
    }

    private static void AppendVirtualSamples(
        IReadOnlyList<float> source,
        IReadOnlyList<SampleRange> ranges,
        long virtualStart,
        int count,
        ICollection<float> destination)
    {
        long virtualEnd = virtualStart + count;
        long cursor = 0;
        foreach (SampleRange range in ranges)
        {
            long rangeStart = cursor;
            long rangeEnd = cursor + range.Length;
            cursor = rangeEnd;
            if (rangeEnd <= virtualStart)
            {
                continue;
            }

            if (rangeStart >= virtualEnd)
            {
                break;
            }

            int sourceOffset = checked((int)Math.Max(
                0,
                virtualStart - rangeStart));
            int available = range.Length - sourceOffset;
            int requested = checked((int)Math.Min(
                available,
                virtualEnd - Math.Max(virtualStart, rangeStart)));
            for (int index = 0; index < requested; index++)
            {
                destination.Add(source[range.Start + sourceOffset + index]);
            }
        }
    }

    private static SampleRange ToSampleRange(
        NeuralSpeakerTurn turn,
        bool trimBoundaries)
    {
        TimeSpan start = turn.Start;
        TimeSpan end = turn.End;
        if (trimBoundaries
            && end - start > EmbeddingBoundaryTrim * 2
            + TimeSpan.FromMilliseconds(300))
        {
            start += EmbeddingBoundaryTrim;
            end -= EmbeddingBoundaryTrim;
        }

        return new SampleRange(
            Math.Max(0, (int)Math.Floor(
                start.TotalSeconds * RequiredSampleRate)),
            Math.Max(0, (int)Math.Ceiling(
                end.TotalSeconds * RequiredSampleRate)));
    }

    private static List<SampleRange> MergeRanges(
        IEnumerable<SampleRange> ranges)
    {
        var merged = new List<SampleRange>();
        foreach (SampleRange range in ranges
                     .Where(candidate => candidate.Length > 0)
                     .OrderBy(candidate => candidate.Start)
                     .ThenBy(candidate => candidate.End))
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            SampleRange previous = merged[^1];
            merged[^1] = previous with
            {
                End = Math.Max(previous.End, range.End)
            };
        }

        return merged;
    }

    private static IEnumerable<SampleRange> Subtract(
        SampleRange source,
        SampleRange blocker)
    {
        if (blocker.End <= source.Start || blocker.Start >= source.End)
        {
            yield return source;
            yield break;
        }

        if (blocker.Start > source.Start)
        {
            yield return new SampleRange(
                source.Start,
                Math.Min(source.End, blocker.Start));
        }

        if (blocker.End < source.End)
        {
            yield return new SampleRange(
                Math.Max(source.Start, blocker.End),
                source.End);
        }
    }

    private static bool HasUsableEnergy(IReadOnlyList<float> samples)
    {
        double squareSum = 0;
        for (int index = 0; index < samples.Count; index++)
        {
            float sample = samples[index];
            if (!float.IsFinite(sample))
            {
                return false;
            }

            squareSum += sample * sample;
        }

        return Math.Sqrt(squareSum / Math.Max(1, samples.Count))
               >= MinimumEmbeddingRms;
    }

    private static double[]? NormalizeEmbedding(
        IReadOnlyList<float> embedding)
    {
        var values = new double[embedding.Count];
        for (int index = 0; index < embedding.Count; index++)
        {
            if (!float.IsFinite(embedding[index]))
            {
                return null;
            }

            values[index] = embedding[index];
        }

        return NormalizeEmbedding([values]);
    }

    private static double[]? NormalizeEmbedding(
        IReadOnlyList<double[]> embeddings)
    {
        if (embeddings.Count == 0)
        {
            return null;
        }

        int length = embeddings[0].Length;
        var average = new double[length];
        foreach (double[] embedding in embeddings)
        {
            if (embedding.Length != length)
            {
                return null;
            }

            for (int index = 0; index < length; index++)
            {
                if (!double.IsFinite(embedding[index]))
                {
                    return null;
                }

                average[index] += embedding[index];
            }
        }

        double squareSum = average.Sum(value => value * value);
        double norm = Math.Sqrt(squareSum);
        if (!double.IsFinite(norm) || norm <= 0.0000001)
        {
            return null;
        }

        for (int index = 0; index < average.Length; index++)
        {
            average[index] /= norm;
        }

        return average;
    }

    private static double[]? NormalizeEmbedding(
        IReadOnlyList<EmbeddingCandidate> embeddings)
    {
        if (embeddings.Count == 0)
        {
            return null;
        }

        int length = embeddings[0].Embedding.Length;
        var average = new double[length];
        double totalWeight = 0;
        foreach (EmbeddingCandidate candidate in embeddings)
        {
            if (candidate.Embedding.Length != length)
            {
                return null;
            }

            double weight = Math.Max(1, candidate.SampleCount);
            totalWeight += weight;
            for (int index = 0; index < length; index++)
            {
                double value = candidate.Embedding[index];
                if (!double.IsFinite(value))
                {
                    return null;
                }

                average[index] += value * weight;
            }
        }

        if (totalWeight <= 0)
        {
            return null;
        }

        for (int index = 0; index < average.Length; index++)
        {
            average[index] /= totalWeight;
        }

        double norm = Math.Sqrt(average.Sum(value => value * value));
        if (!double.IsFinite(norm) || norm <= 0.0000001)
        {
            return null;
        }

        for (int index = 0; index < average.Length; index++)
        {
            average[index] /= norm;
        }

        return average;
    }

    private static double WeightedAverageSimilarity(
        EmbeddingCandidate candidate,
        IReadOnlyList<EmbeddingCandidate> candidates)
    {
        double weightedSimilarity = 0;
        double totalWeight = 0;
        foreach (EmbeddingCandidate other in candidates)
        {
            double weight = Math.Max(1, other.SampleCount);
            weightedSimilarity += CosineSimilarity(
                candidate.Embedding,
                other.Embedding) * weight;
            totalWeight += weight;
        }

        return totalWeight > 0
            ? weightedSimilarity / totalWeight
            : -1;
    }

    private static double CosineSimilarity(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return -1;
        }

        double dot = 0;
        for (int index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
        }

        return Math.Clamp(dot, -1, 1);
    }

    private readonly record struct SampleRange(int Start, int End)
    {
        public int Length => Math.Max(0, End - Start);
    }

    private sealed record EmbeddingCandidate(
        double[] Embedding,
        int SampleCount);
}
