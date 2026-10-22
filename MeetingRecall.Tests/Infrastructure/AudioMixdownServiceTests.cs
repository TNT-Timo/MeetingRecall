using System.Text;
using MeetingRecall.Infrastructure.Audio;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class AudioMixdownServiceTests
{
    [TestMethod]
    public async Task CreateAsync_NormalizesAndMixesDifferentWaveFormats()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.MixdownTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        try
        {
            string microphonePath = Path.Combine(rootDirectory, "microphone.wav");
            string desktopPath = Path.Combine(rootDirectory, "desktop.wav");
            string primaryPath = Path.Combine(rootDirectory, "primary.wav");
            WritePcm16Wave(microphonePath, sampleRate: 16_000, channels: 1, TimeSpan.FromMilliseconds(300), 220);
            WritePcm16Wave(desktopPath, sampleRate: 44_100, channels: 2, TimeSpan.FromMilliseconds(250), 440);

            var service = new AudioMixdownService();
            TimeSpan duration = await service.CreateAsync(
                new[]
                {
                    new AudioMixInput(microphonePath, TimeSpan.Zero),
                    new AudioMixInput(desktopPath, TimeSpan.FromMilliseconds(40))
                },
                primaryPath);

            Assert.IsTrue(File.Exists(primaryPath));
            Assert.IsTrue(new FileInfo(primaryPath).Length > 44);
            Assert.IsTrue(duration >= TimeSpan.FromMilliseconds(280));
            Assert.IsTrue(duration < TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private static void WritePcm16Wave(
        string filePath,
        int sampleRate,
        short channels,
        TimeSpan duration,
        double frequency)
    {
        int frameCount = checked((int)(sampleRate * duration.TotalSeconds));
        int bytesPerSample = sizeof(short);
        int blockAlign = channels * bytesPerSample;
        int dataLength = frameCount * blockAlign;

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (int frame = 0; frame < frameCount; frame++)
        {
            double phase = 2 * Math.PI * frequency * frame / sampleRate;
            short sample = (short)(Math.Sin(phase) * short.MaxValue * 0.2);
            for (int channel = 0; channel < channels; channel++)
            {
                writer.Write(sample);
            }
        }
    }
}
