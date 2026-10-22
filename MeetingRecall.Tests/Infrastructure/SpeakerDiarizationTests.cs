using Microsoft.Extensions.DependencyInjection;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Transcription;
using MeetingRecall.Tests.Helpers;
using NAudio.Wave;

namespace MeetingRecall.Tests.Infrastructure;

[TestClass]
public sealed class SpeakerDiarizationTests
{
    [TestMethod]
    public async Task AnalyzeAsync_NeuralTurnsAlignByTemporalOverlap()
    {
        var neural = new FakeNeuralSpeakerDiarizationEngine(
            new NeuralSpeakerDiarizationResult(
                [
                    new NeuralSpeakerTurn(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2.05),
                        3),
                    new NeuralSpeakerTurn(
                        TimeSpan.FromSeconds(1.95),
                        TimeSpan.FromSeconds(4.05),
                        8),
                    new NeuralSpeakerTurn(
                        TimeSpan.FromSeconds(3.95),
                        TimeSpan.FromSeconds(6),
                        3)
                ],
                new Dictionary<int, double[]>
                {
                    [3] = UnitEmbedding(0),
                    [8] = UnitEmbedding(1)
                }));
        using var context = new PersistenceTestContext(services =>
            services.AddSingleton<INeuralSpeakerDiarizationEngine>(neural));
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "neural.wav");
        WriteVoiceTimeline(wavePath, (180, 6));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            wavePath,
            CreateSegments((0, 2), (2, 4), (4, 6)));

        Assert.AreEqual(result.SpeakerIds[0], result.SpeakerIds[2]);
        Assert.AreNotEqual(result.SpeakerIds[0], result.SpeakerIds[1]);
        Assert.AreEqual(2, result.SpeakerIds.Distinct().Count());
    }

    [TestMethod]
    public async Task AnalyzeAsync_FastSpeakerChangeDoesNotStickToPreviousSpeaker()
    {
        var neural = new FakeNeuralSpeakerDiarizationEngine(
            new NeuralSpeakerDiarizationResult(
                [
                    new NeuralSpeakerTurn(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2.05),
                        3),
                    new NeuralSpeakerTurn(
                        TimeSpan.FromSeconds(1.94),
                        TimeSpan.FromSeconds(4),
                        8)
                ],
                new Dictionary<int, double[]>
                {
                    [3] = UnitEmbedding(0),
                    [8] = UnitEmbedding(1)
                }));
        using var context = new PersistenceTestContext(services =>
            services.AddSingleton<INeuralSpeakerDiarizationEngine>(neural));
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "fast-switch.wav");
        WriteVoiceTimeline(wavePath, (180, 4));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            wavePath,
            CreateSegments((0, 1.9), (1.9, 2.1), (2.1, 4)));

        Assert.AreNotEqual(result.SpeakerIds[0], result.SpeakerIds[1]);
        Assert.AreEqual(result.SpeakerIds[1], result.SpeakerIds[2]);
    }

    [TestMethod]
    public async Task AnalyzeAsync_MergesFragmentedNeuralClustersForSameVoice()
    {
        var neural = new FakeNeuralSpeakerDiarizationEngine(
            new NeuralSpeakerDiarizationResult(
                [
                    new NeuralSpeakerTurn(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2),
                        3),
                    new NeuralSpeakerTurn(
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(4),
                        9),
                    new NeuralSpeakerTurn(
                        TimeSpan.FromSeconds(4),
                        TimeSpan.FromSeconds(6),
                        3)
                ],
                new Dictionary<int, double[]>
                {
                    [3] = UnitEmbedding(0),
                    [9] = PerturbedEmbedding(0, 1, 0.08)
                }));
        using var context = new PersistenceTestContext(services =>
            services.AddSingleton<INeuralSpeakerDiarizationEngine>(neural));
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "fragmented.wav");
        WriteVoiceTimeline(wavePath, (180, 6));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            wavePath,
            CreateSegments((0, 2), (2, 4), (4, 6)));

        Assert.AreEqual(1, result.SpeakerIds.Distinct().Count());
    }

    [TestMethod]
    public void CollectExclusiveSpeakerSamples_LongMeetingUsesWholeTimeline()
    {
        const int sampleRate = 16_000;
        const int durationSeconds = 90;
        var source = new float[sampleRate * durationSeconds];
        for (int index = 0; index < source.Length; index++)
        {
            source[index] = index / (float)(source.Length - 1);
        }

        float[] selected = SherpaOnnxSpeakerDiarizationEngine
            .CollectExclusiveSpeakerSamples(
                source,
                [new NeuralSpeakerTurn(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(durationSeconds),
                    0)],
                speakerIndex: 0,
                maximumSamples: sampleRate * 9 * 5);

        Assert.AreEqual(sampleRate * 9 * 5, selected.Length);
        Assert.IsTrue(selected[0] < 0.01F);
        Assert.IsTrue(selected[^1] > 0.99F);
    }

    [TestMethod]
    public async Task AnalyzeAsync_NeuralEmbeddingRecognizesNamedVoiceLater()
    {
        var neural = new FakeNeuralSpeakerDiarizationEngine(
            new NeuralSpeakerDiarizationResult(
                [
                    new NeuralSpeakerTurn(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(6),
                        4)
                ],
                new Dictionary<int, double[]>
                {
                    [4] = UnitEmbedding(2)
                }));
        using var context = new PersistenceTestContext(services =>
            services.AddSingleton<INeuralSpeakerDiarizationEngine>(neural));
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "enrollment.wav");
        WriteVoiceTimeline(wavePath, (190, 6));
        WhisperTranscriptSegment[] segments =
            CreateSegments((0, 2), (2, 4), (4, 6));

        SpeakerDiarizationResult enrollment = await service.AnalyzeAsync(
            wavePath,
            segments);
        Guid enrolledId = enrollment.SpeakerIds.Distinct().Single();
        Speaker enrolled = (await speakers.GetByIdAsync(enrolledId))!;
        enrolled.AssignName("Alibaba");
        await speakers.UpsertAsync(enrolled);

        SpeakerDiarizationResult recognized = await service.AnalyzeAsync(
            wavePath,
            segments);

        Assert.IsTrue(recognized.SpeakerIds.All(id => id == enrolledId));
    }

    [TestMethod]
    public async Task AnalyzeAsync_TitaNetPathDoesNotUseLegacyVoiceprintFallback()
    {
        double[] embedding = UnitEmbedding(2);
        var neural = new FakeNeuralSpeakerDiarizationEngine(
            new NeuralSpeakerDiarizationResult(
                [
                    new NeuralSpeakerTurn(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(6),
                        4)
                ],
                new Dictionary<int, double[]> { [4] = embedding }));
        using var context = new PersistenceTestContext(services =>
            services.AddSingleton<INeuralSpeakerDiarizationEngine>(neural));
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        var voiceprints = context.GetRequiredService<SpeakerVoiceprintStore>();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "model-change.wav");
        WriteVoiceTimeline(wavePath, (190, 6));
        WhisperTranscriptSegment[] segments =
            CreateSegments((0, 2), (2, 4), (4, 6));
        double[] legacyVector;
        using (var extractor = new SpeakerFeatureExtractor(wavePath))
        {
            legacyVector = segments
                .Select(segment => extractor.Extract(
                    segment.Start,
                    segment.End)!)
                .Aggregate(
                    new double[SpeakerFeatureExtractor.FeatureCount],
                    (sum, vector) => sum
                        .Zip(vector, (left, right) => left + right)
                        .ToArray())
                .Select(value => value / segments.Length)
                .ToArray();
        }

        Speaker oldProfile = Speaker.CreateUnknown(Guid.NewGuid(), 1);
        oldProfile.AssignName("Altes Profil");
        await speakers.UpsertAsync(oldProfile);
        await voiceprints.UpsertAsync(new StoredVoiceprint(
            oldProfile.Id,
            3,
            SpeakerEmbeddingModels.Unversioned,
            legacyVector,
            DateTimeOffset.UtcNow));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            wavePath,
            segments);

        Guid currentSpeakerId = result.SpeakerIds.Distinct().Single();
        Assert.AreNotEqual(oldProfile.Id, currentSpeakerId);
        StoredVoiceprint currentVoiceprint = (await voiceprints.ListAsync())
            .Single(candidate => candidate.SpeakerId == currentSpeakerId);
        Assert.AreEqual(
            SpeakerEmbeddingModels.TitaNetLarge,
            currentVoiceprint.EmbeddingModelId);
    }

    [TestMethod]
    public async Task AnalyzeAsync_RecognizesNamedVoiceInFutureConversation()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(context.RootDirectory, "voice.wav");
        WriteVoiceTimeline(wavePath, (180, 6));

        WhisperTranscriptSegment[] segments = CreateSegments(
            (0, 2),
            (2, 4),
            (4, 6));

        SpeakerDiarizationResult first = await service.AnalyzeAsync(
            wavePath,
            segments);
        Guid firstSpeakerId = first.SpeakerIds.Distinct().Single();
        Speaker firstSpeaker =
            (await speakers.GetByIdAsync(firstSpeakerId))!;
        firstSpeaker.AssignName("Timo");
        await speakers.UpsertAsync(firstSpeaker);

        SpeakerDiarizationResult second = await service.AnalyzeAsync(
            wavePath,
            segments);

        Assert.IsTrue(second.SpeakerIds.All(id => id == firstSpeaker.Id));
    }

    [TestMethod]
    public async Task AnalyzeAsync_DoesNotAssignAbsentNamedSpeaker()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string enrolledPath = Path.Combine(
            context.RootDirectory,
            "vicky.wav");
        WriteVoiceTimeline(enrolledPath, (150, 6));
        WhisperTranscriptSegment[] segments = CreateSegments(
            (0, 2),
            (2, 4),
            (4, 6));

        SpeakerDiarizationResult enrolled = await service.AnalyzeAsync(
            enrolledPath,
            segments);
        Guid vickyId = enrolled.SpeakerIds.Distinct().Single();
        Speaker vicky = (await speakers.GetByIdAsync(vickyId))!;
        vicky.AssignName("Vicky");
        await speakers.UpsertAsync(vicky);

        string differentPath = Path.Combine(
            context.RootDirectory,
            "different-voice.wav");
        WriteVoiceTimeline(differentPath, (330, 6));

        SpeakerDiarizationResult different = await service.AnalyzeAsync(
            differentPath,
            segments);

        Assert.IsFalse(different.SpeakerIds.Contains(vickyId));
        Assert.AreEqual(1, different.SpeakerIds.Distinct().Count());
    }

    [TestMethod]
    public async Task AnalyzeAsync_DoesNotCreatePersonForShortAlteredVoice()
    {
        using var context = new PersistenceTestContext();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        string wavePath = Path.Combine(
            context.RootDirectory,
            "short-altered-voice.wav");
        WriteVoiceTimeline(
            wavePath,
            (180, 2),
            (330, 0.75),
            (180, 2));

        WhisperTranscriptSegment[] segments = CreateSegments(
            (0, 2),
            (2, 2.75),
            (2.75, 4.75));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            wavePath,
            segments);

        Assert.AreEqual(1, result.SpeakerIds.Distinct().Count());
    }

    [TestMethod]
    public async Task AnalyzeAsync_PreservesThreeAlternatingSpeakers()
    {
        using var context = new PersistenceTestContext();
        ISpeakerRepository speakers =
            context.GetRequiredService<ISpeakerRepository>();
        var service = context.GetRequiredService<ISpeakerDiarizationService>();
        WhisperTranscriptSegment[] enrollmentSegments = CreateSegments(
            (0, 2),
            (2, 4),
            (4, 6));

        string alibabaPath = Path.Combine(
            context.RootDirectory,
            "alibaba.wav");
        WriteVoiceTimeline(alibabaPath, (140, 6));
        SpeakerDiarizationResult alibabaEnrollment =
            await service.AnalyzeAsync(
                alibabaPath,
                enrollmentSegments);
        Guid alibabaId = alibabaEnrollment.SpeakerIds.Distinct().Single();
        Speaker alibaba = (await speakers.GetByIdAsync(alibabaId))!;
        alibaba.AssignName("Alibaba");
        await speakers.UpsertAsync(alibaba);

        string sanaPath = Path.Combine(
            context.RootDirectory,
            "sana.wav");
        WriteVoiceTimeline(sanaPath, (400, 6));
        SpeakerDiarizationResult sanaEnrollment =
            await service.AnalyzeAsync(
                sanaPath,
                enrollmentSegments);
        Guid sanaId = sanaEnrollment.SpeakerIds.Distinct().Single();
        Speaker sana = (await speakers.GetByIdAsync(sanaId))!;
        sana.AssignName("Sana");
        await speakers.UpsertAsync(sana);

        string conversationPath = Path.Combine(
            context.RootDirectory,
            "three-speakers.wav");
        WriteVoiceTimeline(
            conversationPath,
            (140, 3),
            (240, 3),
            (400, 3),
            (140, 3),
            (240, 3),
            (400, 3));
        WhisperTranscriptSegment[] conversationSegments = CreateSegments(
            (0, 3),
            (3, 6),
            (6, 9),
            (9, 12),
            (12, 15),
            (15, 18));

        SpeakerDiarizationResult result = await service.AnalyzeAsync(
            conversationPath,
            conversationSegments);

        Assert.AreEqual(3, result.SpeakerIds.Distinct().Count());
        Assert.AreEqual(alibabaId, result.SpeakerIds[0]);
        Assert.AreEqual(alibabaId, result.SpeakerIds[3]);
        Assert.AreEqual(sanaId, result.SpeakerIds[2]);
        Assert.AreEqual(sanaId, result.SpeakerIds[5]);
        Assert.AreEqual(result.SpeakerIds[1], result.SpeakerIds[4]);
        Assert.AreNotEqual(alibabaId, result.SpeakerIds[1]);
        Assert.AreNotEqual(sanaId, result.SpeakerIds[1]);
    }

    private static WhisperTranscriptSegment[] CreateSegments(
        params (double Start, double End)[] ranges)
    {
        return ranges
            .Select((range, index) => new WhisperTranscriptSegment(
                TimeSpan.FromSeconds(range.Start),
                TimeSpan.FromSeconds(range.End),
                $"Test {index + 1}"))
            .ToArray();
    }

    private static double[] UnitEmbedding(int activeIndex)
    {
        var embedding = new double[12];
        embedding[activeIndex] = 1;
        return embedding;
    }

    private static double[] PerturbedEmbedding(
        int activeIndex,
        int secondaryIndex,
        double secondaryValue)
    {
        double[] embedding = UnitEmbedding(activeIndex);
        embedding[secondaryIndex] = secondaryValue;
        return embedding;
    }

    private static void WriteVoiceTimeline(
        string path,
        params (double Frequency, double Seconds)[] parts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = new WaveFormat(16_000, 16, 1);

        using var writer = new WaveFileWriter(path, format);
        double timelineSeconds = 0;
        foreach ((double frequency, double seconds) in parts)
        {
            int sampleCount = (int)Math.Round(16_000 * seconds);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                double time = timelineSeconds + (sampleIndex / 16_000d);
                double envelope =
                    0.55 + (0.15 * Math.Sin(2 * Math.PI * 3 * time));
                double sample =
                    (Math.Sin(2 * Math.PI * frequency * time) * 0.65)
                    + (Math.Sin(2 * Math.PI * frequency * 2 * time) * 0.2);
                short pcm = (short)Math.Clamp(
                    sample * envelope * short.MaxValue,
                    short.MinValue,
                    short.MaxValue);
                writer.WriteByte((byte)(pcm & 0xff));
                writer.WriteByte((byte)((pcm >> 8) & 0xff));
            }

            timelineSeconds += seconds;
        }
    }

    private sealed class FakeNeuralSpeakerDiarizationEngine(
        NeuralSpeakerDiarizationResult result)
        : INeuralSpeakerDiarizationEngine
    {
        public bool IsAvailable => true;

        public Task<NeuralSpeakerDiarizationResult> AnalyzeAsync(
            string normalizedWavePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
