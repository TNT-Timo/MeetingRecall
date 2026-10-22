using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecall.Infrastructure.Transcription;

internal interface IAudioTranscriptionNormalizer
{
    Task NormalizeAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

internal sealed class AudioTranscriptionNormalizer : IAudioTranscriptionNormalizer
{
    private const int WhisperSampleRate = 16_000;

    public Task NormalizeAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Die Audiodatei für die Transkription wurde nicht gefunden.", sourcePath);
        }

        return Task.Run(
            () => Normalize(sourcePath, destinationPath, cancellationToken),
            cancellationToken);
    }

    private static void Normalize(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Der Zielpfad besitzt keinen Ordner."));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using WaveStream reader = CreateReader(sourcePath);
            ISampleProvider provider = reader.ToSampleProvider();
            provider = EnsureMono(provider);

            if (provider.WaveFormat.SampleRate != WhisperSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, WhisperSampleRate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var outputStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            WaveFileWriter.WriteWavFileToStream(outputStream, provider.ToWaveProvider16());
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private static WaveStream CreateReader(string sourcePath)
    {
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".wav" => new WaveFileReader(sourcePath),
            _ => new MediaFoundationReader(sourcePath)
        };
    }

    internal static TimeSpan ValidateNormalizedDuration(
        string normalizedWavePath,
        TimeSpan expectedDuration)
    {
        using var reader = new WaveFileReader(normalizedWavePath);
        TimeSpan actualDuration = reader.TotalTime;
        if (expectedDuration < TimeSpan.FromMinutes(1))
        {
            return actualDuration;
        }

        TimeSpan tolerance = TimeSpan.FromSeconds(Math.Max(
            5,
            expectedDuration.TotalSeconds * 0.02));
        if (actualDuration + tolerance < expectedDuration)
        {
            throw new InvalidDataException(
                $"Die vorbereitete Audiospur ist unvollständig: erwartet wurden ungefähr " +
                $"{expectedDuration:hh\\:mm\\:ss}, dekodiert wurden jedoch nur " +
                $"{actualDuration:hh\\:mm\\:ss}. Bitte importiere die Originaldatei erneut.");
        }

        return actualDuration;
    }

    private static ISampleProvider EnsureMono(ISampleProvider source)
    {
        return source.WaveFormat.Channels == 1
            ? source
            : new MonoDownmixSampleProvider(source);
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
            // Preserve the original normalization error.
        }
    }

    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public MonoDownmixSampleProvider(ISampleProvider source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.WaveFormat.Channels < 2)
            {
                throw new ArgumentException("Die Quelle muss mindestens zwei Kanäle besitzen.", nameof(source));
            }

            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int sourceChannels = _source.WaveFormat.Channels;
            int requiredSourceSamples = count * sourceChannels;
            if (_sourceBuffer.Length < requiredSourceSamples)
            {
                _sourceBuffer = new float[requiredSourceSamples];
            }

            int sourceSamplesRead = _source.Read(_sourceBuffer, 0, requiredSourceSamples);
            int framesRead = sourceSamplesRead / sourceChannels;

            for (int frame = 0; frame < framesRead; frame++)
            {
                int sourceIndex = frame * sourceChannels;
                float sum = 0;
                for (int channel = 0; channel < sourceChannels; channel++)
                {
                    sum += _sourceBuffer[sourceIndex + channel];
                }

                buffer[offset + frame] = sum / sourceChannels;
            }

            return framesRead;
        }
    }
}
