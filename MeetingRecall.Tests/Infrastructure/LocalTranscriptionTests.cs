using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Infrastructure.Transcription;
using NAudio.Wave;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class LocalTranscriptionTests
{
    [TestMethod]
    public void CpuFallback_UsesEveryAvailableLogicalProcessor()
    {
        Assert.AreEqual(
            Math.Max(1, Environment.ProcessorCount),
            WhisperTranscriptionEngine.ProcessorThreadCount);
    }

    [TestMethod]
    public void Transcription_PrioritizesEveryGpuRuntimeBeforeCpuFallback()
    {
        CollectionAssert.AreEqual(
            new[] { "Cuda", "Cuda12", "Vulkan" },
            WhisperTranscriptionEngine.GpuRuntimeOrder.ToArray());
    }

    [TestMethod]
    public async Task AudioNormalizer_CreatesWhisperCompatibleWaveFile()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(root, "source.wav");
            string destinationPath = Path.Combine(root, "normalized.wav");
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

            using (var writer = new WaveFileWriter(sourcePath, format))
            {
                for (int frame = 0; frame < 44_100; frame++)
                {
                    float sample = (float)Math.Sin(2 * Math.PI * 440 * frame / 44_100);
                    writer.WriteSample(sample);
                    writer.WriteSample(sample * 0.5f);
                }
            }

            var normalizer = new AudioTranscriptionNormalizer();
            await normalizer.NormalizeAsync(sourcePath, destinationPath);

            using var reader = new WaveFileReader(destinationPath);
            Assert.AreEqual(16_000, reader.WaveFormat.SampleRate);
            Assert.AreEqual(1, reader.WaveFormat.Channels);
            Assert.AreEqual(16, reader.WaveFormat.BitsPerSample);
            Assert.IsTrue(reader.TotalTime > TimeSpan.FromMilliseconds(900));
            Assert.IsTrue(reader.TotalTime < TimeSpan.FromMilliseconds(1100));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void AudioNormalizer_RejectsSeverelyTruncatedNormalizedAudio()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string normalizedPath = Path.Combine(root, "truncated.wav");
            using (var writer = new WaveFileWriter(
                       normalizedPath,
                       new WaveFormat(16_000, 16, 1)))
            {
                writer.Write(new byte[16_000 * 2 * 10]);
            }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                AudioTranscriptionNormalizer.ValidateNormalizedDuration(
                    normalizedPath,
                    TimeSpan.FromHours(2)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task TranscribeAsync_PersistsLanguageSegmentsAndUnknownSpeaker()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureCreated();
            var conversation = new Conversation(
                Guid.NewGuid(),
                "Lokale Testaufnahme",
                DateTimeOffset.Now,
                ConversationSource.NativeRecording);
            conversation.SetDuration(TimeSpan.FromSeconds(12));
            Speaker obsoleteSpeaker = Speaker.CreateUnknown(
                Guid.NewGuid(),
                42);
            conversation.AddSpeaker(obsoleteSpeaker.Id);

            AudioTrack track = CreatePrimaryTrack(paths, conversation.Id);
            var conversationRepository = new FakeConversationRepository(conversation);
            var speakerRepository = new FakeSpeakerRepository();
            speakerRepository.Speakers.Add(obsoleteSpeaker);
            Speaker replacementSpeaker = Speaker.CreateUnknown(
                Guid.NewGuid(),
                1);
            speakerRepository.Speakers.Add(replacementSpeaker);
            var audioRepository = new FakeAudioArchiveRepository(track);
            var engine = new FakeWhisperEngine(new WhisperTranscriptionResult(
                "de",
                [
                    new WhisperTranscriptSegment(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(4),
                        "Guten Morgen zusammen."),
                    new WhisperTranscriptSegment(
                        TimeSpan.FromSeconds(4),
                        TimeSpan.FromSeconds(9),
                        "Wir besprechen heute den Projektstatus.")
                ]));
            var progress = new InlineProgress<TranscriptionProgress>();
            var service = new LocalTranscriptionService(
                conversationRepository,
                speakerRepository,
                audioRepository,
                new FakeNormalizer(),
                engine,
                paths,
                diarizationService: new FixedSpeakerDiarizationService(
                    replacementSpeaker.Id));

            Conversation result = await service.TranscribeAsync(
                conversation.Id,
                progress);

            Assert.AreEqual(ConversationProcessingState.Ready, result.ProcessingState);
            Assert.AreEqual("de", result.DetectedLanguage);
            Assert.AreEqual(1, result.TranscriptSegments.Count);
            Assert.AreEqual(
                "Guten Morgen zusammen. Wir besprechen heute den Projektstatus.",
                result.TranscriptSegments[0].CurrentText);
            Assert.AreEqual(1, speakerRepository.Speakers.Count);
            Assert.AreEqual(
                speakerRepository.Speakers[0].Id,
                result.TranscriptSegments[0].SpeakerId);
            Assert.IsFalse(result.SpeakerIds.Contains(obsoleteSpeaker.Id));
            Assert.IsFalse(speakerRepository.Speakers.Any(
                speaker => speaker.Id == obsoleteSpeaker.Id));
            Assert.IsTrue(progress.Values.Any(value => value.Stage == TranscriptionStage.Completed));
            Assert.AreSame(result, conversationRepository.Current);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task TranscribeAsync_MarksConversationFailedWhenEngineThrows()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureCreated();
            var conversation = new Conversation(
                Guid.NewGuid(),
                "Fehlertest",
                DateTimeOffset.Now,
                ConversationSource.ImportedAudio);
            AudioTrack track = CreatePrimaryTrack(paths, conversation.Id);
            var conversationRepository = new FakeConversationRepository(conversation);
            var service = new LocalTranscriptionService(
                conversationRepository,
                new FakeSpeakerRepository(),
                new FakeAudioArchiveRepository(track),
                new FakeNormalizer(),
                new ThrowingWhisperEngine(),
                paths);

            InvalidOperationException? captured = null;
            try
            {
                await service.TranscribeAsync(conversation.Id);
            }
            catch (InvalidOperationException exception)
            {
                captured = exception;
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual(
                ConversationProcessingState.Failed,
                conversationRepository.Current.ProcessingState);
            Assert.AreEqual(0, conversationRepository.Current.TranscriptSegments.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static AudioTrack CreatePrimaryTrack(AppDataPaths paths, Guid conversationId)
    {
        string relativePath = $"Meetings/{conversationId:N}/Audio/primary.wav";
        string absolutePath = paths.ToAbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(absolutePath, [0, 1, 2, 3]);

        return new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            AudioTrackKind.Primary,
            relativePath,
            "primary.wav",
            "audio/wav",
            4,
            TimeSpan.FromSeconds(12));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
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
            // Test cleanup is best effort on Windows media handles.
        }
    }

    private sealed class FakeConversationRepository(Conversation conversation)
        : IConversationRepository
    {
        public Conversation Current { get; private set; } = conversation;

        public Task<Conversation?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Conversation?>(Current.Id == id ? Current : null);
        }

        public Task<IReadOnlyList<Conversation>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Conversation>>([Current]);
        }

        public Task UpsertAsync(
            Conversation conversation,
            CancellationToken cancellationToken = default)
        {
            Current = conversation;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class FixedSpeakerDiarizationService(Guid speakerId)
        : ISpeakerDiarizationService
    {
        public Task<SpeakerDiarizationResult> AnalyzeAsync(
            string normalizedWavePath,
            IReadOnlyList<WhisperTranscriptSegment> segments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SpeakerDiarizationResult(
                Enumerable.Repeat(speakerId, segments.Count).ToArray()));
        }
    }

    private sealed class FakeSpeakerRepository : ISpeakerRepository
    {
        public List<Speaker> Speakers { get; } = [];

        public Task<Speaker?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Speakers.FirstOrDefault(speaker => speaker.Id == id));
        }

        public Task<IReadOnlyList<Speaker>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Speaker>>(Speakers);
        }

        public Task UpsertAsync(
            Speaker speaker,
            CancellationToken cancellationToken = default)
        {
            Speakers.RemoveAll(existing => existing.Id == speaker.Id);
            Speakers.Add(speaker);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Speakers.RemoveAll(speaker => speaker.Id == id) > 0);
        }

        public Task<int> DeleteAllAsync(
            CancellationToken cancellationToken = default)
        {
            int count = Speakers.Count;
            Speakers.Clear();
            return Task.FromResult(count);
        }
    }

    private sealed class FakeAudioArchiveRepository(AudioTrack track)
        : IAudioArchiveRepository
    {
        public Task ImportAsync(
            Conversation conversation,
            AudioTrack primaryTrack,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ImportAsync(
            Conversation conversation,
            IReadOnlyCollection<AudioTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AudioTrack?> GetPrimaryAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AudioTrack?>(
                track.ConversationId == conversationId ? track : null);
        }

        public Task<AudioTrack?> FindPrimaryBySourceHashAsync(
            string sourceSha256,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AudioTrack?>(null);
        }

        public Task<IReadOnlyList<AudioTrack>> ListPrimaryWithoutSourceHashAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AudioTrack>>([]);
        }

        public Task<bool> TrySetSourceHashAsync(
            Guid trackId,
            string sourceSha256,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<AudioTrack>> ListAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AudioTrack>>(
                track.ConversationId == conversationId ? [track] : []);
        }
    }

    private sealed class FakeNormalizer : IAudioTranscriptionNormalizer
    {
        public Task NormalizeAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var writer = new WaveFileWriter(
                destinationPath,
                new WaveFormat(16_000, 16, 1));
            writer.Write(new byte[16_000 * 2 * 12]);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWhisperEngine(WhisperTranscriptionResult result)
        : IWhisperTranscriptionEngine
    {
        public bool IsModelInstalled => true;

        public string ModelDisplayName => "Testmodell";

        public Task<WhisperTranscriptionResult> TranscribeAsync(
            string normalizedWavePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new TranscriptionProgress(
                TranscriptionStage.Transcribing,
                SegmentCount: result.Segments.Count));
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingWhisperEngine : IWhisperTranscriptionEngine
    {
        public bool IsModelInstalled => true;

        public string ModelDisplayName => "Testmodell";

        public Task<WhisperTranscriptionResult> TranscribeAsync(
            string normalizedWavePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Whisper-Testfehler");
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }
}
