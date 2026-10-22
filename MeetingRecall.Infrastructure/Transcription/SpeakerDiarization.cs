using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Persistence;
using MeetingRecall.Infrastructure.Repositories;
using NAudio.Wave;

namespace MeetingRecall.Infrastructure.Transcription;

internal interface ISpeakerDiarizationService
{
    Task<SpeakerDiarizationResult> AnalyzeAsync(
        string normalizedWavePath,
        IReadOnlyList<WhisperTranscriptSegment> segments,
        CancellationToken cancellationToken = default);
}

internal sealed record SpeakerDiarizationResult(IReadOnlyList<Guid> SpeakerIds);

internal sealed record StoredVoiceprint(
    Guid SpeakerId,
    int SampleCount,
    string EmbeddingModelId,
    double[] FeatureVector,
    DateTimeOffset UpdatedAtUtc);

internal sealed class SpeakerVoiceprintStore(
    IDatabaseInitializer databaseInitializer,
    ISqliteConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<StoredVoiceprint>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        databaseInitializer.Initialize();
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(
            connection,
            """
            SELECT
                speaker_id,
                sample_count,
                embedding_model_id,
                feature_vector_json,
                updated_at_utc
            FROM speaker_voiceprints;
            """);
        using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<StoredVoiceprint>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            double[] vector = JsonSerializer.Deserialize<double[]>(reader.GetString(3)) ?? [];
            if (vector.Length >= SpeakerFeatureExtractor.FeatureCount
                && vector.All(double.IsFinite))
            {
                results.Add(new StoredVoiceprint(
                    SqliteValues.ToGuid(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    vector,
                    SqliteValues.ToDateTimeOffset(reader.GetString(4))));
            }
        }

        return results;
    }

    public async Task UpsertAsync(
        StoredVoiceprint voiceprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            voiceprint.EmbeddingModelId);
        if (voiceprint.FeatureVector.Length < SpeakerFeatureExtractor.FeatureCount
            || voiceprint.FeatureVector.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException(
                "A speaker voiceprint must contain a valid feature vector.",
                nameof(voiceprint));
        }

        databaseInitializer.Initialize();
        using SqliteConnection connection = connectionFactory.OpenConnection();
        using SqliteCommand command = SqliteCommandFactory.Create(
            connection,
            """
            INSERT INTO speaker_voiceprints (
                speaker_id,
                sample_count,
                embedding_model_id,
                feature_vector_json,
                updated_at_utc)
            VALUES (
                @speakerId,
                @sampleCount,
                @embeddingModelId,
                @featureVectorJson,
                @updatedAtUtc)
            ON CONFLICT(speaker_id) DO UPDATE SET
                sample_count = excluded.sample_count,
                embedding_model_id = excluded.embedding_model_id,
                feature_vector_json = excluded.feature_vector_json,
                updated_at_utc = excluded.updated_at_utc;
            """);
        command.Parameters.AddWithValue(
            "@speakerId",
            SqliteValues.FromGuid(voiceprint.SpeakerId));
        command.Parameters.AddWithValue("@sampleCount", voiceprint.SampleCount);
        command.Parameters.AddWithValue(
            "@embeddingModelId",
            voiceprint.EmbeddingModelId);
        command.Parameters.AddWithValue(
            "@featureVectorJson",
            JsonSerializer.Serialize(voiceprint.FeatureVector));
        command.Parameters.AddWithValue(
            "@updatedAtUtc",
            SqliteValues.FromDateTimeOffset(voiceprint.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SpeakerDiarizationService(
    ISpeakerRepository speakerRepository,
    SpeakerVoiceprintStore voiceprintStore,
    INeuralSpeakerDiarizationEngine? neuralEngine = null)
    : ISpeakerDiarizationService
{
    // Matching a known person is intentionally much stricter than grouping
    // voices inside one conversation. A false "unknown" is recoverable; a
    // confidently displayed wrong name is not.
    private const double KnownSpeakerThreshold = 0.125;
    private const double KnownSpeakerMargin = 0.025;
    private const double ConversationClusterThreshold = 0.105;
    private const double ConversationAnchorThreshold = 0.135;
    private const double ClusterMergeThreshold = 0.075;
    private const double ShortClusterMergeThreshold = 0.11;
    private const double LowQualityAssignmentThreshold = 0.14;
    private const double MinimumReliableSegmentSeconds = 1.25;
    private const double MinimumStableClusterSeconds = 4;
    private const double MinimumKnownMatchSeconds = 5;
    private const int MinimumKnownMatchSamples = 2;
    // NeMo's TitaNet verification helper uses a cosine-similarity threshold
    // of 0.70. This is the equivalent maximum cosine distance.
    private const double NeuralKnownSpeakerThreshold = 0.30;
    private const double NeuralKnownSpeakerMargin = 0.07;
    private const double NeuralClusterMergeThreshold = 0.22;
    private const double NeuralShortClusterMergeThreshold = 0.29;
    private const double MaximumShortNeuralClusterSeconds = 4;
    private const double NeuralOverlapTieSeconds = 0.15;

    public async Task<SpeakerDiarizationResult> AnalyzeAsync(
        string normalizedWavePath,
        IReadOnlyList<WhisperTranscriptSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedWavePath);
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return new SpeakerDiarizationResult([]);
        }

        if (neuralEngine?.IsAvailable == true)
        {
            NeuralSpeakerDiarizationResult neural = await neuralEngine
                .AnalyzeAsync(normalizedWavePath, cancellationToken)
                .ConfigureAwait(false);
            if (neural.Turns.Count == 0)
            {
                throw new InvalidDataException(
                    "Die neuronale Sprecheranalyse hat keine Sprechabschnitte erkannt.");
            }

            return await AnalyzeNeuralAsync(
                    segments,
                    neural,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await AnalyzeLegacyAsync(
                normalizedWavePath,
                segments,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SpeakerDiarizationResult> AnalyzeLegacyAsync(
        string normalizedWavePath,
        IReadOnlyList<WhisperTranscriptSegment> segments,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<Speaker> speakers = await speakerRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        var allocatedSpeakers = speakers.ToList();
        IReadOnlyList<StoredVoiceprint> storedVoiceprints = await voiceprintStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var speakersById = speakers.ToDictionary(speaker => speaker.Id);
        StoredVoiceprint[] knownVoiceprints = storedVoiceprints
            .Where(voiceprint =>
                voiceprint.SampleCount >= MinimumKnownMatchSamples
                && IsLegacyAcousticVoiceprint(voiceprint)
                && speakersById.TryGetValue(voiceprint.SpeakerId, out Speaker? speaker)
                && speaker.IsNamed)
            .ToArray();

        var observations = new List<SegmentObservation>(segments.Count);
        using (var extractor = new SpeakerFeatureExtractor(normalizedWavePath))
        {
            for (int index = 0; index < segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WhisperTranscriptSegment segment = segments[index];
                double[]? vector = extractor.Extract(segment.Start, segment.End);
                observations.Add(new SegmentObservation(index, segment, vector));
            }
        }

        var clusters = BuildConversationClusters(observations);
        if (clusters.Count == 0)
        {
            Guid fallbackId = await CreateUnknownSpeakerAsync(
                    allocatedSpeakers,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SpeakerDiarizationResult(
                Enumerable.Repeat(fallbackId, segments.Count).ToArray());
        }

        MergeCloseClusters(clusters);
        ConsolidateShortClusters(clusters);
        AssignLowQualitySegments(observations, clusters);

        var speakerIdsByCluster = new Dictionary<ConversationSpeakerCluster, Guid>();

        foreach (ConversationSpeakerCluster cluster in clusters
                     .OrderBy(candidate => candidate.FirstSegmentIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();

            StoredVoiceprint? knownMatch =
                cluster.ReliableSampleCount >= MinimumKnownMatchSamples
                && cluster.TotalDurationSeconds >= MinimumKnownMatchSeconds
                    ? FindConfidentKnownMatch(knownVoiceprints, cluster.Centroid)
                    : null;

            if (knownMatch is not null)
            {
                // Never blend automatically matched audio back into a named
                // profile. One bad match must not poison later conversations.
                speakerIdsByCluster[cluster] = knownMatch.SpeakerId;
                continue;
            }

            Guid speakerId = await CreateUnknownSpeakerAsync(
                    allocatedSpeakers,
                    cancellationToken)
                .ConfigureAwait(false);
            speakerIdsByCluster[cluster] = speakerId;

            // Unknown profiles keep a clean conversation-level centroid. If
            // the user later names the profile, this becomes its enrollment
            // sample for future conversations.
            await voiceprintStore
                .UpsertAsync(
                    new StoredVoiceprint(
                        speakerId,
                        cluster.ReliableSampleCount,
                        SpeakerEmbeddingModels.LegacyAcoustic,
                        cluster.Centroid,
                        DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Guid[] assignments = observations
            .OrderBy(observation => observation.Index)
            .Select(observation =>
                speakerIdsByCluster[
                    observation.Cluster
                    ?? throw new InvalidDataException(
                        "A transcript segment has no speaker cluster.")])
            .ToArray();

        return new SpeakerDiarizationResult(assignments);
    }

    private async Task<SpeakerDiarizationResult> AnalyzeNeuralAsync(
        IReadOnlyList<WhisperTranscriptSegment> segments,
        NeuralSpeakerDiarizationResult neural,
        CancellationToken cancellationToken)
    {
        if (neural.Turns.Count == 0)
        {
            throw new InvalidDataException(
                "Die neuronale Sprecheranalyse hat keine Sprechabschnitte erkannt.");
        }

        neural = ConsolidateNeuralClusters(neural);

        int[] clusterAssignments = AssignSegmentsToNeuralTurns(
            segments,
            neural.Turns);

        IReadOnlyList<Speaker> speakers = await speakerRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        var allocatedSpeakers = speakers.ToList();
        IReadOnlyList<StoredVoiceprint> storedVoiceprints = await voiceprintStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        var speakersById = speakers.ToDictionary(speaker => speaker.Id);
        StoredVoiceprint[] knownVoiceprints = storedVoiceprints
            .Where(voiceprint =>
                voiceprint.SampleCount >= MinimumKnownMatchSamples
                && speakersById.TryGetValue(
                    voiceprint.SpeakerId,
                    out Speaker? speaker)
                && speaker.IsNamed)
            .ToArray();

        var usedKnownSpeakerIds = new HashSet<Guid>();
        var speakerIdsByCluster = new Dictionary<int, Guid>();
        int[] orderedClusters = clusterAssignments
            .Distinct()
            .OrderBy(cluster => Array.IndexOf(clusterAssignments, cluster))
            .ToArray();

        foreach (int cluster in orderedClusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sampleCount = clusterAssignments.Count(value => value == cluster);
            double durationSeconds = Enumerable.Range(0, segments.Count)
                .Where(index => clusterAssignments[index] == cluster)
                .Sum(index => Math.Max(
                    0,
                    (segments[index].End - segments[index].Start)
                    .TotalSeconds));

            StoredVoiceprint? knownMatch = null;
            if (sampleCount >= MinimumKnownMatchSamples
                && durationSeconds >= MinimumKnownMatchSeconds)
            {
                if (neural.Embeddings.TryGetValue(
                        cluster,
                        out double[]? embedding))
                {
                    knownMatch = FindConfidentNeuralMatch(
                        knownVoiceprints.Where(candidate =>
                            !usedKnownSpeakerIds.Contains(candidate.SpeakerId)
                            && IsVoiceprintForModel(
                                candidate,
                                neural.EmbeddingModelId)),
                        embedding);
                }
            }

            if (knownMatch is not null)
            {
                speakerIdsByCluster[cluster] = knownMatch.SpeakerId;
                usedKnownSpeakerIds.Add(knownMatch.SpeakerId);
                continue;
            }

            Guid speakerId = await CreateUnknownSpeakerAsync(
                    allocatedSpeakers,
                    cancellationToken)
                .ConfigureAwait(false);
            speakerIdsByCluster[cluster] = speakerId;

            if (neural.Embeddings.TryGetValue(
                    cluster,
                    out double[]? neuralEmbedding))
            {
                await voiceprintStore
                    .UpsertAsync(
                        new StoredVoiceprint(
                            speakerId,
                            sampleCount,
                            neural.EmbeddingModelId,
                            neuralEmbedding,
                            DateTimeOffset.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        Guid[] speakerIds = clusterAssignments
            .Select(cluster => speakerIdsByCluster[cluster])
            .ToArray();
        return new SpeakerDiarizationResult(speakerIds);
    }

    private static int[] AssignSegmentsToNeuralTurns(
        IReadOnlyList<WhisperTranscriptSegment> segments,
        IReadOnlyList<NeuralSpeakerTurn> turns)
    {
        if (turns.Count == 0)
        {
            throw new ArgumentException(
                "At least one neural speaker turn is required.",
                nameof(turns));
        }

        var assignments = new int[segments.Count];
        for (int index = 0; index < segments.Count; index++)
        {
            WhisperTranscriptSegment segment = segments[index];
            Dictionary<int, double> overlapBySpeaker = turns
                .Select(turn => new
                {
                    turn.SpeakerIndex,
                    Seconds = Math.Max(
                        0,
                        (Min(segment.End, turn.End)
                         - Max(segment.Start, turn.Start)).TotalSeconds)
                })
                .Where(candidate => candidate.Seconds > 0)
                .GroupBy(candidate => candidate.SpeakerIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(candidate => candidate.Seconds));

            int speaker;
            if (overlapBySpeaker.Count > 0)
            {
                KeyValuePair<int, double>[] ranked = overlapBySpeaker
                    .OrderByDescending(candidate => candidate.Value)
                    .ThenBy(candidate => candidate.Key)
                    .ToArray();
                speaker = ranked[0].Key;
                if (ranked.Length > 1
                    && ranked[0].Value - ranked[1].Value
                    <= NeuralOverlapTieSeconds)
                {
                    TimeSpan midpoint = SegmentMidpoint(segment);
                    NeuralSpeakerTurn? currentTurn = turns
                        .Where(turn =>
                            midpoint >= turn.Start
                            && midpoint < turn.End
                            && overlapBySpeaker.ContainsKey(
                                turn.SpeakerIndex))
                        .OrderByDescending(turn => turn.Start)
                        .ThenBy(turn => turn.End)
                        .FirstOrDefault();
                    if (currentTurn is not null)
                    {
                        speaker = currentTurn.SpeakerIndex;
                    }
                }
            }
            else
            {
                TimeSpan midpoint = SegmentMidpoint(segment);
                speaker = turns
                    .OrderBy(turn => DistanceToTurn(midpoint, turn))
                    .ThenBy(turn => turn.Start)
                    .First()
                    .SpeakerIndex;
            }

            assignments[index] = speaker;
        }

        return assignments;
    }

    private static TimeSpan SegmentMidpoint(
        WhisperTranscriptSegment segment) =>
        segment.Start + TimeSpan.FromTicks(Math.Max(
            0,
            (segment.End - segment.Start).Ticks / 2));

    private static NeuralSpeakerDiarizationResult ConsolidateNeuralClusters(
        NeuralSpeakerDiarizationResult neural)
    {
        Dictionary<int, double> durations = neural.Turns
            .GroupBy(turn => turn.SpeakerIndex)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(turn => Math.Max(
                    0,
                    (turn.End - turn.Start).TotalSeconds)));
        int[] speakerIndexes = neural.Turns
            .Select(turn => turn.SpeakerIndex)
            .Distinct()
            .OrderByDescending(index => durations.GetValueOrDefault(index))
            .ToArray();

        var aliases = new Dictionary<int, int>();
        var representatives = new List<NeuralClusterRepresentative>();

        foreach (int speakerIndex in speakerIndexes)
        {
            if (!neural.Embeddings.TryGetValue(
                    speakerIndex,
                    out double[]? embedding))
            {
                aliases[speakerIndex] = speakerIndex;
                continue;
            }

            var closestMatch = representatives
                .Where(candidate =>
                    candidate.Centroid.Length == embedding.Length)
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Distance = CosineDistance(
                        candidate.Centroid,
                        embedding)
                })
                .OrderBy(candidate => candidate.Distance)
                .FirstOrDefault();
            double mergeThreshold =
                durations.GetValueOrDefault(speakerIndex)
                <= MaximumShortNeuralClusterSeconds
                    ? NeuralShortClusterMergeThreshold
                    : NeuralClusterMergeThreshold;
            NeuralClusterRepresentative? closest =
                closestMatch is not null
                && closestMatch.Distance <= mergeThreshold
                    ? closestMatch.Candidate
                    : null;

            if (closest is null)
            {
                closest = new NeuralClusterRepresentative(
                    speakerIndex,
                    embedding,
                    durations.GetValueOrDefault(speakerIndex));
                representatives.Add(closest);
            }
            else
            {
                closest.Add(
                    embedding,
                    durations.GetValueOrDefault(speakerIndex));
            }

            aliases[speakerIndex] = closest.SpeakerIndex;
        }

        NeuralSpeakerTurn[] remappedTurns = neural.Turns
            .Select(turn => turn with
            {
                SpeakerIndex = aliases.GetValueOrDefault(
                    turn.SpeakerIndex,
                    turn.SpeakerIndex)
            })
            .OrderBy(turn => turn.Start)
            .ThenBy(turn => turn.End)
            .ToArray();
        var normalizedIndexes = new Dictionary<int, int>();
        foreach (int speakerIndex in remappedTurns
                     .Select(turn => turn.SpeakerIndex)
                     .Distinct())
        {
            normalizedIndexes[speakerIndex] = normalizedIndexes.Count;
        }

        NeuralSpeakerTurn[] normalizedTurns = remappedTurns
            .Select(turn => turn with
            {
                SpeakerIndex = normalizedIndexes[turn.SpeakerIndex]
            })
            .ToArray();
        IReadOnlyList<NeuralSpeakerTurn> combinedTurns =
            CombineAdjacentTurns(normalizedTurns);
        Dictionary<int, double[]> embeddings = representatives
            .Where(representative =>
                normalizedIndexes.ContainsKey(representative.SpeakerIndex))
            .ToDictionary(
                representative =>
                    normalizedIndexes[representative.SpeakerIndex],
                representative => representative.Centroid);

        return new NeuralSpeakerDiarizationResult(
            combinedTurns,
            embeddings,
            neural.EmbeddingModelId);
    }

    private static IReadOnlyList<NeuralSpeakerTurn> CombineAdjacentTurns(
        IReadOnlyList<NeuralSpeakerTurn> turns)
    {
        var combined = new List<NeuralSpeakerTurn>(turns.Count);
        foreach (NeuralSpeakerTurn turn in turns)
        {
            if (combined.Count > 0
                && combined[^1].SpeakerIndex == turn.SpeakerIndex
                && turn.Start <= combined[^1].End
                    + TimeSpan.FromMilliseconds(150))
            {
                NeuralSpeakerTurn previous = combined[^1];
                combined[^1] = previous with
                {
                    End = Max(previous.End, turn.End)
                };
                continue;
            }

            combined.Add(turn);
        }

        return combined;
    }

    private static StoredVoiceprint? FindConfidentNeuralMatch(
        IEnumerable<StoredVoiceprint> candidates,
        IReadOnlyList<double> embedding)
    {
        var ordered = candidates
            .Where(candidate =>
                candidate.FeatureVector.Length == embedding.Count)
            .Select(candidate => new
            {
                Voiceprint = candidate,
                Distance = CosineDistance(
                    candidate.FeatureVector,
                    embedding)
            })
            .OrderBy(candidate => candidate.Distance)
            .Take(2)
            .ToArray();

        if (ordered.Length == 0
            || ordered[0].Distance > NeuralKnownSpeakerThreshold)
        {
            return null;
        }

        if (ordered.Length > 1
            && ordered[1].Distance - ordered[0].Distance
            < NeuralKnownSpeakerMargin)
        {
            return null;
        }

        return ordered[0].Voiceprint;
    }

    private static bool IsVoiceprintForModel(
        StoredVoiceprint voiceprint,
        string embeddingModelId) =>
        string.Equals(
            voiceprint.EmbeddingModelId,
            embeddingModelId,
            StringComparison.Ordinal);

    private static bool IsLegacyAcousticVoiceprint(
        StoredVoiceprint voiceprint) =>
        voiceprint.FeatureVector.Length == SpeakerFeatureExtractor.FeatureCount
        && (string.Equals(
                voiceprint.EmbeddingModelId,
                SpeakerEmbeddingModels.LegacyAcoustic,
                StringComparison.Ordinal)
            || string.Equals(
                voiceprint.EmbeddingModelId,
                SpeakerEmbeddingModels.Unversioned,
                StringComparison.Ordinal));

    private static double CosineDistance(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return double.MaxValue;
        }

        double dot = 0;
        double leftSquareSum = 0;
        double rightSquareSum = 0;
        for (int index = 0; index < left.Count; index++)
        {
            if (!double.IsFinite(left[index])
                || !double.IsFinite(right[index]))
            {
                return double.MaxValue;
            }

            dot += left[index] * right[index];
            leftSquareSum += left[index] * left[index];
            rightSquareSum += right[index] * right[index];
        }

        double denominator = Math.Sqrt(leftSquareSum * rightSquareSum);
        if (denominator <= 0.0000001)
        {
            return double.MaxValue;
        }

        return 1 - Math.Clamp(dot / denominator, -1, 1);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;

    private static double DistanceToTurn(
        TimeSpan position,
        NeuralSpeakerTurn turn)
    {
        if (position < turn.Start)
        {
            return (turn.Start - position).TotalSeconds;
        }

        return position > turn.End
            ? (position - turn.End).TotalSeconds
            : 0;
    }

    private static List<ConversationSpeakerCluster> BuildConversationClusters(
        IReadOnlyList<SegmentObservation> observations)
    {
        var clusters = new List<ConversationSpeakerCluster>();

        foreach (SegmentObservation observation in observations.Where(
                     candidate => candidate.IsReliable))
        {
            ConversationSpeakerCluster? best = clusters
                .Where(cluster => cluster.CanAccept(observation.Vector!))
                .OrderBy(cluster => SpeakerFeatureExtractor.Distance(
                    cluster.Centroid,
                    observation.Vector!))
                .FirstOrDefault();
            if (best is null)
            {
                best = new ConversationSpeakerCluster();
                clusters.Add(best);
            }

            best.Add(observation);
        }

        return clusters;
    }

    private static void MergeCloseClusters(
        ICollection<ConversationSpeakerCluster> clusters)
    {
        bool merged;
        do
        {
            merged = false;
            ConversationSpeakerCluster? leftMatch = null;
            ConversationSpeakerCluster? rightMatch = null;
            double bestDistance = double.MaxValue;
            ConversationSpeakerCluster[] snapshot = clusters.ToArray();

            for (int leftIndex = 0; leftIndex < snapshot.Length; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1;
                     rightIndex < snapshot.Length;
                     rightIndex++)
                {
                    double distance = SpeakerFeatureExtractor.Distance(
                        snapshot[leftIndex].Centroid,
                        snapshot[rightIndex].Centroid);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        leftMatch = snapshot[leftIndex];
                        rightMatch = snapshot[rightIndex];
                    }
                }
            }

            if (leftMatch is not null
                && rightMatch is not null
                && bestDistance <= ClusterMergeThreshold
                && leftMatch.CanMerge(rightMatch))
            {
                ConversationSpeakerCluster target =
                    leftMatch.ReliableSampleCount >= rightMatch.ReliableSampleCount
                        ? leftMatch
                        : rightMatch;
                ConversationSpeakerCluster source =
                    ReferenceEquals(target, leftMatch) ? rightMatch : leftMatch;
                target.Absorb(source);
                clusters.Remove(source);
                merged = true;
            }
        }
        while (merged);
    }

    private static void ConsolidateShortClusters(
        ICollection<ConversationSpeakerCluster> clusters)
    {
        if (clusters.Count <= 1)
        {
            return;
        }

        List<ConversationSpeakerCluster> stable = clusters
            .Where(cluster =>
                cluster.ReliableSampleCount >= 2
                || cluster.TotalDurationSeconds >= MinimumStableClusterSeconds)
            .ToList();

        if (stable.Count == 0)
        {
            stable.Add(clusters
                .OrderByDescending(cluster => cluster.TotalDurationSeconds)
                .First());
        }

        foreach (ConversationSpeakerCluster shortCluster in clusters
                     .Where(cluster => !stable.Contains(cluster))
                     .OrderBy(cluster => cluster.TotalDurationSeconds)
                     .ToArray())
        {
            ConversationSpeakerCluster? closest = FindClosestCluster(
                stable,
                shortCluster.Centroid,
                out double distance);
            if (closest is null || distance > ShortClusterMergeThreshold)
            {
                continue;
            }

            closest.Absorb(shortCluster);
            clusters.Remove(shortCluster);
        }
    }

    private static void AssignLowQualitySegments(
        IReadOnlyList<SegmentObservation> observations,
        IReadOnlyList<ConversationSpeakerCluster> clusters)
    {
        ConversationSpeakerCluster largest = clusters
            .OrderByDescending(cluster => cluster.TotalDurationSeconds)
            .First();

        for (int index = 0; index < observations.Count; index++)
        {
            SegmentObservation observation = observations[index];
            if (observation.Cluster is not null)
            {
                continue;
            }

            ConversationSpeakerCluster? previous = index > 0
                ? observations[index - 1].Cluster
                : null;
            ConversationSpeakerCluster? next = index + 1 < observations.Count
                ? observations[index + 1].Cluster
                : null;

            if (previous is not null && ReferenceEquals(previous, next))
            {
                previous.AddUnreliable(observation);
                continue;
            }

            // Very short passages, whispers, laughter and boundary fragments
            // must not manufacture a permanent new person.
            if (observation.DurationSeconds < MinimumReliableSegmentSeconds)
            {
                (previous ?? next ?? largest).AddUnreliable(observation);
                continue;
            }

            if (observation.Vector is not null)
            {
                ConversationSpeakerCluster? closest = FindClosestCluster(
                    clusters,
                    observation.Vector,
                    out double distance);
                if (closest is not null
                    && distance <= LowQualityAssignmentThreshold)
                {
                    closest.AddUnreliable(observation);
                    continue;
                }
            }

            (previous ?? next ?? largest).AddUnreliable(observation);
        }
    }

    private async Task<Guid> CreateUnknownSpeakerAsync(
        ICollection<Speaker> allocatedSpeakers,
        CancellationToken cancellationToken)
    {
        int sequenceNumber = Speaker.FindNextAvailableUnknownSequence(
            allocatedSpeakers);
        Speaker speaker = Speaker.CreateUnknown(Guid.NewGuid(), sequenceNumber);
        await speakerRepository
            .UpsertAsync(speaker, cancellationToken)
            .ConfigureAwait(false);
        allocatedSpeakers.Add(speaker);
        return speaker.Id;
    }

    private static StoredVoiceprint? FindConfidentKnownMatch(
        IEnumerable<StoredVoiceprint> candidates,
        double[] vector)
    {
        var ordered = candidates
            .Select(candidate => new
            {
                Voiceprint = candidate,
                Distance = SpeakerFeatureExtractor.Distance(
                    candidate.FeatureVector,
                    vector)
            })
            .OrderBy(candidate => candidate.Distance)
            .Take(2)
            .ToArray();

        if (ordered.Length == 0
            || ordered[0].Distance > KnownSpeakerThreshold)
        {
            return null;
        }

        if (ordered.Length > 1
            && ordered[1].Distance - ordered[0].Distance < KnownSpeakerMargin)
        {
            return null;
        }

        return ordered[0].Voiceprint;
    }

    private static ConversationSpeakerCluster? FindClosestCluster(
        IEnumerable<ConversationSpeakerCluster> clusters,
        IReadOnlyList<double> vector,
        out double distance)
    {
        ConversationSpeakerCluster? best = null;
        distance = double.MaxValue;

        foreach (ConversationSpeakerCluster cluster in clusters)
        {
            double candidateDistance = SpeakerFeatureExtractor.Distance(
                cluster.Centroid,
                vector);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                best = cluster;
            }
        }

        return best;
    }

    private sealed class NeuralClusterRepresentative
    {
        private readonly double[] _weightedSum;
        private double _totalWeight;

        public NeuralClusterRepresentative(
            int speakerIndex,
            IReadOnlyList<double> embedding,
            double durationSeconds)
        {
            SpeakerIndex = speakerIndex;
            Centroid = new double[embedding.Count];
            _weightedSum = new double[embedding.Count];
            Add(embedding, durationSeconds);
        }

        public int SpeakerIndex { get; }

        public double[] Centroid { get; }

        public void Add(
            IReadOnlyList<double> embedding,
            double durationSeconds)
        {
            double weight = Math.Max(0.25, durationSeconds);
            _totalWeight += weight;
            for (int index = 0; index < Centroid.Length; index++)
            {
                _weightedSum[index] += embedding[index] * weight;
                Centroid[index] = _weightedSum[index] / _totalWeight;
            }

            NormalizeInPlace(Centroid);
        }

        private static void NormalizeInPlace(double[] embedding)
        {
            double norm = Math.Sqrt(embedding.Sum(value => value * value));
            if (!double.IsFinite(norm) || norm <= 0.0000001)
            {
                return;
            }

            for (int index = 0; index < embedding.Length; index++)
            {
                embedding[index] /= norm;
            }
        }
    }

    private sealed class SegmentObservation(
        int index,
        WhisperTranscriptSegment segment,
        double[]? vector)
    {
        public int Index { get; } = index;

        public double[]? Vector { get; } = vector;

        public double DurationSeconds { get; } = Math.Max(
            0,
            (segment.End - segment.Start).TotalSeconds);

        public bool IsReliable =>
            Vector is not null
            && DurationSeconds >= MinimumReliableSegmentSeconds;

        public ConversationSpeakerCluster? Cluster { get; set; }
    }

    private sealed class ConversationSpeakerCluster
    {
        private readonly double[] _weightedSum =
            new double[SpeakerFeatureExtractor.FeatureCount];
        private double[]? _anchor;

        public double[] Centroid { get; } =
            new double[SpeakerFeatureExtractor.FeatureCount];

        public int ReliableSampleCount { get; private set; }

        public double TotalDurationSeconds { get; private set; }

        public int FirstSegmentIndex { get; private set; } = int.MaxValue;

        public bool CanAccept(IReadOnlyList<double> vector)
        {
            if (_anchor is null)
            {
                return true;
            }

            return SpeakerFeatureExtractor.Distance(Centroid, vector)
                    <= ConversationClusterThreshold
                && SpeakerFeatureExtractor.Distance(_anchor, vector)
                    <= ConversationAnchorThreshold;
        }

        public bool CanMerge(ConversationSpeakerCluster other)
        {
            if (_anchor is null || other._anchor is null)
            {
                return false;
            }

            return SpeakerFeatureExtractor.Distance(_anchor, other.Centroid)
                    <= ConversationClusterThreshold
                && SpeakerFeatureExtractor.Distance(other._anchor, Centroid)
                    <= ConversationClusterThreshold;
        }

        public void Add(SegmentObservation observation)
        {
            if (observation.Vector is null)
            {
                throw new ArgumentException(
                    "A reliable cluster sample requires a feature vector.",
                    nameof(observation));
            }

            _anchor ??= observation.Vector.ToArray();
            double weight = Math.Clamp(observation.DurationSeconds, 0.5, 8);
            Track(observation, weight);
            ReliableSampleCount++;
            TotalDurationSeconds += observation.DurationSeconds;
            FirstSegmentIndex = Math.Min(FirstSegmentIndex, observation.Index);
            for (int index = 0; index < Centroid.Length; index++)
            {
                _weightedSum[index] += observation.Vector[index] * weight;
                Centroid[index] = _weightedSum[index] / _totalWeight;
            }

            observation.Cluster = this;
        }

        public void AddUnreliable(SegmentObservation observation)
        {
            Track(observation, 0);
            TotalDurationSeconds += observation.DurationSeconds;
            FirstSegmentIndex = Math.Min(FirstSegmentIndex, observation.Index);
            observation.Cluster = this;
        }

        public void Absorb(ConversationSpeakerCluster source)
        {
            foreach (SegmentObservation observation in source.Observations)
            {
                if (observation.IsReliable)
                {
                    Add(observation);
                }
                else
                {
                    AddUnreliable(observation);
                }
            }
        }

        private List<SegmentObservation> Observations { get; } = [];

        private double _totalWeight;

        private double GetTotalWeight() => _totalWeight;

        private void Track(SegmentObservation observation, double weight)
        {
            _totalWeight += weight;
            Observations.Add(observation);
        }
    }
}

internal sealed class SpeakerFeatureExtractor : IDisposable
{
    public const int FeatureCount = 10;
    private const int SampleRate = 16_000;
    private const int MaximumSecondsPerSegment = 8;
    private readonly WaveFileReader _reader;

    public SpeakerFeatureExtractor(string wavePath)
    {
        _reader = new WaveFileReader(wavePath);
        if (_reader.WaveFormat.SampleRate != SampleRate
            || _reader.WaveFormat.Channels != 1
            || _reader.WaveFormat.BitsPerSample != 16)
        {
            throw new InvalidDataException(
                "Speaker analysis requires normalized 16 kHz mono PCM16 audio.");
        }
    }

    public double[]? Extract(TimeSpan start, TimeSpan end)
    {
        TimeSpan safeStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        TimeSpan duration = end > safeStart
            ? end - safeStart
            : TimeSpan.FromMilliseconds(400);
        duration = duration > TimeSpan.FromSeconds(MaximumSecondsPerSegment)
            ? TimeSpan.FromSeconds(MaximumSecondsPerSegment)
            : duration;

        int blockAlign = _reader.WaveFormat.BlockAlign;
        long startByte = (long)(safeStart.TotalSeconds * _reader.WaveFormat.AverageBytesPerSecond);
        startByte -= startByte % blockAlign;
        startByte = Math.Clamp(startByte, 0, _reader.Length);
        _reader.Position = startByte;

        int byteCount = (int)Math.Min(
            duration.TotalSeconds * _reader.WaveFormat.AverageBytesPerSecond,
            _reader.Length - startByte);
        byteCount -= byteCount % blockAlign;
        if (byteCount < SampleRate / 5 * blockAlign)
        {
            return null;
        }

        byte[] bytes = new byte[byteCount];
        int read = _reader.Read(bytes, 0, bytes.Length);
        int sampleCount = read / 2;
        if (sampleCount < SampleRate / 5)
        {
            return null;
        }

        var samples = new double[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(index * 2, 2)) / 32768d;
        }

        return CalculateFeatures(samples);
    }

    public static double Distance(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        if (left.Count != FeatureCount || right.Count != FeatureCount)
        {
            return double.MaxValue;
        }

        double[] weights = [0.25, 0.2, 0.65, 0.2, 2.1, 1.1, 1.25, 0.65, 0.65, 0.65];
        double weightedSquareSum = 0;
        double weightSum = 0;

        for (int index = 0; index < FeatureCount; index++)
        {
            double difference = left[index] - right[index];
            weightedSquareSum += difference * difference * weights[index];
            weightSum += weights[index];
        }

        return Math.Sqrt(weightedSquareSum / weightSum);
    }

    private static double[] CalculateFeatures(double[] samples)
    {
        double squareSum = 0;
        double absoluteSum = 0;
        double maximumAbsolute = 0;
        int zeroCrossings = 0;

        for (int index = 0; index < samples.Length; index++)
        {
            double sample = samples[index];
            double absolute = Math.Abs(sample);
            squareSum += sample * sample;
            absoluteSum += absolute;
            maximumAbsolute = Math.Max(maximumAbsolute, absolute);

            if (index > 0 && Math.Sign(samples[index - 1]) != Math.Sign(sample))
            {
                zeroCrossings++;
            }
        }

        double rms = Math.Sqrt(squareSum / samples.Length);
        double meanAbsolute = absoluteSum / samples.Length;
        double zeroCrossingRate = zeroCrossings / (double)Math.Max(1, samples.Length - 1);
        double crest = maximumAbsolute / Math.Max(rms, 0.00001);
        (double pitch, double pitchSpread, double pitchStrength) = EstimatePitch(samples);

        return
        [
            Math.Clamp((Math.Log10(rms + 0.00001) + 5) / 5, 0, 1),
            Math.Clamp(meanAbsolute / Math.Max(rms, 0.00001), 0, 1),
            Math.Clamp(zeroCrossingRate * 4, 0, 1),
            Math.Clamp(crest / 8, 0, 1),
            Math.Clamp(Math.Log(Math.Max(pitch, 50) / 50) / Math.Log(8), 0, 1),
            Math.Clamp(pitchSpread / 150, 0, 1),
            Math.Clamp(pitchStrength, 0, 1),
            NormalizeAutocorrelation(samples, 20),
            NormalizeAutocorrelation(samples, 40),
            NormalizeAutocorrelation(samples, 80)
        ];
    }

    private static (double Pitch, double Spread, double Strength) EstimatePitch(double[] samples)
    {
        const int frameLength = 640;
        const int frameStep = 1600;
        var pitches = new List<double>();
        var strengths = new List<double>();

        for (int start = 0;
             start + frameLength <= samples.Length && pitches.Count < 32;
             start += frameStep)
        {
            double mean = 0;
            for (int index = 0; index < frameLength; index++)
            {
                mean += samples[start + index];
            }

            mean /= frameLength;
            int bestLag = 0;
            double bestCorrelation = 0;

            for (int lag = 40; lag <= 320; lag++)
            {
                double correlation = 0;
                double leftEnergy = 0;
                double rightEnergy = 0;
                for (int index = lag; index < frameLength; index++)
                {
                    double left = samples[start + index] - mean;
                    double right = samples[start + index - lag] - mean;
                    correlation += left * right;
                    leftEnergy += left * left;
                    rightEnergy += right * right;
                }

                double normalized = correlation
                    / Math.Sqrt(Math.Max(leftEnergy * rightEnergy, 0.0000000001));
                if (normalized > bestCorrelation)
                {
                    bestCorrelation = normalized;
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && bestCorrelation >= 0.25)
            {
                pitches.Add(SampleRate / (double)bestLag);
                strengths.Add(bestCorrelation);
            }
        }

        if (pitches.Count == 0)
        {
            return (120, 150, 0);
        }

        pitches.Sort();
        double median = pitches[pitches.Count / 2];
        double variance = pitches.Sum(pitch => Math.Pow(pitch - median, 2)) / pitches.Count;
        return (median, Math.Sqrt(variance), strengths.Average());
    }

    private static double NormalizeAutocorrelation(double[] samples, int lag)
    {
        double correlation = 0;
        double leftEnergy = 0;
        double rightEnergy = 0;
        int step = Math.Max(1, samples.Length / 24_000);

        for (int index = lag; index < samples.Length; index += step)
        {
            double left = samples[index];
            double right = samples[index - lag];
            correlation += left * right;
            leftEnergy += left * left;
            rightEnergy += right * right;
        }

        double normalized = correlation
            / Math.Sqrt(Math.Max(leftEnergy * rightEnergy, 0.0000000001));
        return Math.Clamp((normalized + 1) / 2, 0, 1);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
