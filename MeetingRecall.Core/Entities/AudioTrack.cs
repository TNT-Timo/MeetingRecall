using MeetingRecall.Core.Enums;

namespace MeetingRecall.Core.Entities;

public sealed class AudioTrack
{
    private const int MaximumOriginalFileNameLength = 260;
    private const int MaximumMediaTypeLength = 120;
    private const int Sha256Length = 64;

    public AudioTrack(
        Guid id,
        Guid conversationId,
        AudioTrackKind kind,
        string relativePath,
        string originalFileName,
        string mediaType,
        long fileSizeBytes,
        TimeSpan duration,
        string? sourceSha256 = null)
        : this(
            id,
            conversationId,
            kind,
            relativePath,
            originalFileName,
            mediaType,
            fileSizeBytes,
            duration,
            sourceSha256,
            DateTimeOffset.UtcNow)
    {
    }

    private AudioTrack(
        Guid id,
        Guid conversationId,
        AudioTrackKind kind,
        string relativePath,
        string originalFileName,
        string mediaType,
        long fileSizeBytes,
        TimeSpan duration,
        string? sourceSha256,
        DateTimeOffset createdAtUtc)
    {
        ValidateIdentifier(id, nameof(id));
        ValidateIdentifier(conversationId, nameof(conversationId));

        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "An audio file size cannot be negative.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "An audio duration cannot be negative.");
        }

        Id = id;
        ConversationId = conversationId;
        Kind = kind;
        RelativePath = ValidateRelativePath(relativePath);
        OriginalFileName = ValidateText(originalFileName, MaximumOriginalFileNameLength, nameof(originalFileName));
        MediaType = ValidateText(mediaType, MaximumMediaTypeLength, nameof(mediaType));
        FileSizeBytes = fileSizeBytes;
        Duration = duration;
        SourceSha256 = NormalizeSha256(sourceSha256);
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid ConversationId { get; }

    public AudioTrackKind Kind { get; }

    public string RelativePath { get; }

    public string OriginalFileName { get; }

    public string MediaType { get; }

    public long FileSizeBytes { get; }

    public TimeSpan Duration { get; }

    public string? SourceSha256 { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    internal static AudioTrack Restore(
        Guid id,
        Guid conversationId,
        AudioTrackKind kind,
        string relativePath,
        string originalFileName,
        string mediaType,
        long fileSizeBytes,
        TimeSpan duration,
        string? sourceSha256,
        DateTimeOffset createdAtUtc)
    {
        return new AudioTrack(
            id,
            conversationId,
            kind,
            relativePath,
            originalFileName,
            mediaType,
            fileSizeBytes,
            duration,
            sourceSha256,
            createdAtUtc);
    }

    private static string ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string normalized = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            throw new ArgumentException("An audio path must be relative to the MeetingRecall data directory.", nameof(relativePath));
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("An audio path cannot escape the MeetingRecall data directory.", nameof(relativePath));
        }

        return string.Join('/', segments);
    }

    private static string ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentException($"The value may not exceed {maximumLength} characters.", parameterName);
        }

        return trimmed;
    }

    private static string? NormalizeSha256(string? sourceSha256)
    {
        if (string.IsNullOrWhiteSpace(sourceSha256))
        {
            return null;
        }

        string normalized = sourceSha256.Trim().ToLowerInvariant();
        if (normalized.Length != Sha256Length || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 fingerprint must contain exactly 64 hexadecimal characters.", nameof(sourceSha256));
        }

        return normalized;
    }

    private static void ValidateIdentifier(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
