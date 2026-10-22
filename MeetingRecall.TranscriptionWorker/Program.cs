using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeetingRecall.Core.Transcription;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace MeetingRecall.TranscriptionWorker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        SetErrorMode(ErrorMode.FailCriticalErrors | ErrorMode.NoGpFaultErrorBox);

        if (args.Length != 5
            || !int.TryParse(args[3], out int threadCount)
            || threadCount < 1
            || !TryParseGpuRuntime(args[4], out RuntimeLibrary runtime))
        {
            Console.Error.WriteLine(
                "Usage: MeetingRecall.TranscriptionWorker <model> <wave> <result> <threads> <Cuda|Cuda12|Vulkan>");
            return 2;
        }

        string modelPath = Path.GetFullPath(args[0]);
        string wavePath = Path.GetFullPath(args[1]);
        string resultPath = Path.GetFullPath(args[2]);

        try
        {
            using IDisposable whisperLogger = LogProvider.AddLogger(
                (level, message) =>
                    Console.Error.Write($"[{level}] {message}"));
            ConfigureGpuRuntimeSearchPath(runtime);
            RuntimeOptions.RuntimeLibraryOrder = [runtime];

            WhisperFactoryOptions factoryOptions = WhisperFactoryOptions.Default;
            factoryOptions.UseGpu = true;
            factoryOptions.GpuDevice = 0;

            using WhisperFactory factory = WhisperFactory.FromPath(
                modelPath,
                factoryOptions);
            if (RuntimeOptions.LoadedLibrary != runtime)
            {
                Console.Error.WriteLine(
                    $"Whisper did not load the requested {runtime} GPU runtime.");
                return 10;
            }

            var segments = new List<WorkerSegment>();
            var languages = new List<string>();
            TimeSpan audioDuration;
            using (var waveReader = new WaveFileReader(wavePath))
            {
                audioDuration = waveReader.TotalTime;
            }

            string? lockedLanguage = null;
            IReadOnlyList<WhisperAudioChunk> chunks =
                WhisperAudioChunking.Plan(audioDuration);
            foreach (WhisperAudioChunk chunk in chunks)
            {
                using WhisperProcessor processor = CreateProcessor(
                    factory,
                    threadCount,
                    lockedLanguage);
                float[] samples = ReadChunkSamples(wavePath, chunk);
                var chunkLanguages = new List<string>();
                await foreach (SegmentData segment in processor.ProcessAsync(samples))
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

                    segments.Add(new WorkerSegment(
                        absoluteStart,
                        absoluteEnd,
                        text));
                    Console.Out.WriteLine(
                        $"{absoluteEnd.Ticks}\t{segments.Count}");
                    if (!string.IsNullOrWhiteSpace(segment.Language))
                    {
                        string segmentLanguage = segment.Language.Trim();
                        languages.Add(segmentLanguage);
                        chunkLanguages.Add(segmentLanguage);
                    }
                }

                lockedLanguage ??= chunkLanguages
                    .GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();

                Console.Out.WriteLine(
                    $"{chunk.OwnedEnd.Ticks}\t{segments.Count}");
            }

            string? language = languages
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();
            var result = new WorkerResult(
                RuntimeOptions.LoadedLibrary.ToString()!,
                language,
                segments);
            string temporaryResultPath = resultPath + ".partial";
            await using (var resultStream = new FileStream(
                             temporaryResultPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    resultStream,
                    result,
                    JsonOptions);
                await resultStream.FlushAsync();
            }

            File.Move(temporaryResultPath, resultPath, overwrite: true);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 11;
        }
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
        string wavePath,
        WhisperAudioChunk chunk)
    {
        using var reader = new WaveFileReader(wavePath);
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

    private static bool TryParseGpuRuntime(
        string value,
        out RuntimeLibrary runtime)
    {
        if (Enum.TryParse(value, ignoreCase: true, out runtime)
            && runtime is RuntimeLibrary.Cuda
                or RuntimeLibrary.Cuda12
                or RuntimeLibrary.Vulkan)
        {
            return true;
        }

        runtime = default;
        return false;
    }

    private static void ConfigureGpuRuntimeSearchPath(RuntimeLibrary runtime)
    {
        if (runtime is not (RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12))
        {
            return;
        }

        string runtimeFileName = runtime == RuntimeLibrary.Cuda
            ? "cudart64_13.dll"
            : "cudart64_12.dll";
        string? runtimeDirectory = EnumerateCudaBinaryDirectories()
            .FirstOrDefault(directory =>
                File.Exists(Path.Combine(directory, runtimeFileName)));
        if (runtimeDirectory is null)
        {
            Console.Error.WriteLine(
                $"CUDA runtime directory containing {runtimeFileName} was not found.");
            return;
        }

        if (!SetDllDirectory(runtimeDirectory))
        {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine(
                $"Could not register CUDA runtime directory '{runtimeDirectory}' (Win32 {error}).");
            return;
        }

        Console.Error.WriteLine(
            $"Using CUDA runtime directory: {runtimeDirectory}");
    }

    private static IEnumerable<string> EnumerateCudaBinaryDirectories()
    {
        var roots = new List<string>();
        AddEnvironmentRoot("CUDA_PATH", roots);
        foreach (System.Collections.DictionaryEntry variable in
                 Environment.GetEnvironmentVariables())
        {
            string name = variable.Key?.ToString() ?? string.Empty;
            if (name.StartsWith("CUDA_PATH_V", StringComparison.OrdinalIgnoreCase))
            {
                AddRoot(variable.Value?.ToString(), roots);
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
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddRoot(directory, roots);
                }
            }
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "bin", "x64");
            yield return Path.Combine(root, "bin");
        }
    }

    private static void AddEnvironmentRoot(string name, ICollection<string> roots)
    {
        AddRoot(Environment.GetEnvironmentVariable(name), roots);
    }

    private static void AddRoot(string? value, ICollection<string> roots)
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

    private sealed record WorkerResult(
        string Runtime,
        string? Language,
        IReadOnlyList<WorkerSegment> Segments);

    private sealed record WorkerSegment(
        TimeSpan Start,
        TimeSpan End,
        string Text);
}
