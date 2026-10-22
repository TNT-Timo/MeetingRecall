namespace MeetingRecall.Core.Transcription;

public sealed record WhisperAudioChunk(
    int Index,
    TimeSpan Start,
    TimeSpan Duration,
    TimeSpan OwnedStart,
    TimeSpan OwnedEnd)
{
    public TimeSpan End => Start + Duration;
}

public static class WhisperAudioChunking
{
    public static readonly TimeSpan DefaultChunkDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultOverlap = TimeSpan.FromSeconds(4);
    public static IReadOnlyList<WhisperAudioChunk> Plan(
        TimeSpan audioDuration,
        TimeSpan? chunkDuration = null,
        TimeSpan? overlap = null)
    {
        if (audioDuration <= TimeSpan.Zero)
        {
            return [];
        }

        TimeSpan requestedChunkDuration = chunkDuration ?? DefaultChunkDuration;
        TimeSpan requestedOverlap = overlap ?? DefaultOverlap;
        if (requestedChunkDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration));
        }

        if (requestedOverlap < TimeSpan.Zero
            || requestedOverlap >= requestedChunkDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap));
        }

        TimeSpan step = requestedChunkDuration - requestedOverlap;
        TimeSpan halfOverlap = TimeSpan.FromTicks(requestedOverlap.Ticks / 2);
        var chunks = new List<WhisperAudioChunk>();
        for (TimeSpan start = TimeSpan.Zero; start < audioDuration; start += step)
        {
            TimeSpan end = Min(start + requestedChunkDuration, audioDuration);
            TimeSpan ownedStart = chunks.Count == 0
                ? TimeSpan.Zero
                : start + halfOverlap;
            TimeSpan ownedEnd = end >= audioDuration
                ? audioDuration
                : end - halfOverlap;
            chunks.Add(new WhisperAudioChunk(
                chunks.Count,
                start,
                end - start,
                ownedStart,
                ownedEnd));
            if (end >= audioDuration)
            {
                break;
            }
        }

        return chunks;
    }

    public static bool OwnsSegment(
        WhisperAudioChunk chunk,
        TimeSpan absoluteStart,
        TimeSpan absoluteEnd,
        TimeSpan audioDuration)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        TimeSpan safeEnd = absoluteEnd < absoluteStart
            ? absoluteStart
            : absoluteEnd;
        TimeSpan midpoint = absoluteStart
            + TimeSpan.FromTicks((safeEnd - absoluteStart).Ticks / 2);
        bool isLast = chunk.OwnedEnd >= audioDuration;
        return midpoint >= chunk.OwnedStart
               && (isLast
                   ? midpoint <= chunk.OwnedEnd
                   : midpoint < chunk.OwnedEnd);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;
}
