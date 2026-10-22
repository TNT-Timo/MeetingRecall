using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecall.Infrastructure.Audio;

internal sealed class AudioMixdownService
{
    private const int OutputSampleRate = 48_000;
    private const int OutputChannels = 2;

    public Task<TimeSpan> CreateAsync(
        IReadOnlyCollection<AudioMixInput> inputs,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return Task.Run(
            () => Create(inputs, outputPath, cancellationToken),
            cancellationToken);
    }

    private static TimeSpan Create(
        IReadOnlyCollection<AudioMixInput> inputs,
        string outputPath,
        CancellationToken cancellationToken)
    {
        AudioMixInput[] existingInputs = inputs
            .Where(input => File.Exists(input.FilePath) && new FileInfo(input.FilePath).Length > 44)
            .ToArray();

        if (existingInputs.Length == 0)
        {
            throw new InvalidOperationException("Die Aufnahme enthält keine verwertbaren Audiodaten.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The output path has no directory."));

        var readers = new List<AudioFileReader>();
        try
        {
            float sourceVolume = existingInputs.Length > 1 ? 0.65f : 1f;
            var providers = new List<ISampleProvider>();

            foreach (AudioMixInput input in existingInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reader = new AudioFileReader(input.FilePath);
                readers.Add(reader);

                ISampleProvider provider = EnsureStereo(reader);
                if (provider.WaveFormat.SampleRate != OutputSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
                }

                provider = new VolumeSampleProvider(provider)
                {
                    Volume = sourceVolume * Math.Clamp(input.Volume, 0f, 1f)
                };

                if (input.Delay > TimeSpan.Zero)
                {
                    provider = new OffsetSampleProvider(provider)
                    {
                        DelayBy = input.Delay
                    };
                }

                providers.Add(provider);
            }

            var mixer = new MixingSampleProvider(providers)
            {
                ReadFully = false
            };
            var waveProvider = new SampleToWaveProvider16(mixer);

            using (var writer = new WaveFileWriter(outputPath, waveProvider.WaveFormat))
            {
                byte[] buffer = new byte[waveProvider.WaveFormat.AverageBytesPerSecond];
                int bytesRead;
                while ((bytesRead = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(buffer, 0, bytesRead);
                }
            }

            using var outputReader = new WaveFileReader(outputPath);
            return outputReader.TotalTime;
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            foreach (AudioFileReader reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static ISampleProvider EnsureStereo(ISampleProvider provider)
    {
        return provider.WaveFormat.Channels == OutputChannels
            ? provider
            : new StereoChannelSelectorSampleProvider(provider);
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
            // Preserve the original mixdown error.
        }
    }

    private sealed class StereoChannelSelectorSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public StereoChannelSelectorSampleProvider(ISampleProvider source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.WaveFormat.Channels <= 0)
            {
                throw new ArgumentException("The source must expose at least one channel.", nameof(source));
            }

            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, OutputChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int requestedFrames = count / OutputChannels;
            if (requestedFrames == 0)
            {
                return 0;
            }

            int sourceChannels = _source.WaveFormat.Channels;
            int requiredSourceSamples = requestedFrames * sourceChannels;
            if (_sourceBuffer.Length < requiredSourceSamples)
            {
                _sourceBuffer = new float[requiredSourceSamples];
            }

            int sourceSamplesRead = _source.Read(_sourceBuffer, 0, requiredSourceSamples);
            int framesRead = sourceSamplesRead / sourceChannels;
            int rightChannel = sourceChannels > 1 ? 1 : 0;

            for (int frame = 0; frame < framesRead; frame++)
            {
                int sourceIndex = frame * sourceChannels;
                int outputIndex = offset + (frame * OutputChannels);
                buffer[outputIndex] = _sourceBuffer[sourceIndex];
                buffer[outputIndex + 1] = _sourceBuffer[sourceIndex + rightChannel];
            }

            return framesRead * OutputChannels;
        }
    }
}

internal sealed record AudioMixInput(
    string FilePath,
    TimeSpan Delay,
    float Volume = 1f);
