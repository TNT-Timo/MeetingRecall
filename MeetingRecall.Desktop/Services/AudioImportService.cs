using System.Security.Cryptography;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecallDataPaths = MeetingRecall.Infrastructure.Storage.AppDataPaths;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace MeetingRecall.Desktop.Services;

public sealed class AudioImportService(
    MeetingRecallDataPaths paths,
    IAudioArchiveRepository audioArchiveRepository,
    IConversationRepository conversationRepository)
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v"
    };

    public static IReadOnlyCollection<string> SupportedExtensions =>
        AudioExtensions.Concat(VideoExtensions).OrderBy(extension => extension).ToArray();

    public static bool IsVideoFile(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return VideoExtensions.Contains(file.FileType);
    }

    public async Task<AudioImportResult> ImportAsync(
        StorageFile sourceFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);

        string extension = sourceFile.FileType;
        bool isVideo = VideoExtensions.Contains(extension);
        if (!isVideo && !AudioExtensions.Contains(extension))
        {
            throw new NotSupportedException($"The file type '{extension}' is not supported.");
        }

        string sourceSha256 = await ComputeSourceHashAsync(sourceFile, cancellationToken);
        AudioImportResult? existingImport = await FindExistingImportAsync(sourceSha256, cancellationToken);
        if (existingImport is not null)
        {
            return existingImport;
        }

        Guid conversationId = Guid.NewGuid();
        string meetingDirectory = paths.GetMeetingDirectory(conversationId);
        string audioDirectory = paths.GetMeetingAudioDirectory(conversationId);
        Directory.CreateDirectory(audioDirectory);

        try
        {
            StorageFolder destinationFolder = await StorageFolder.GetFolderFromPathAsync(audioDirectory);
            StorageFile storedAudio = isVideo
                ? await ExtractAudioAsync(sourceFile, destinationFolder)
                : await CopyAudioAsync(sourceFile, destinationFolder);

            TimeSpan duration = await ReadDurationAsync(storedAudio, isVideo ? sourceFile : null);
            BasicProperties properties = await storedAudio.GetBasicPropertiesAsync();
            string title = Path.GetFileNameWithoutExtension(sourceFile.Name).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Importiertes Gespräch";
            }

            var conversation = new Conversation(
                conversationId,
                title,
                sourceFile.DateCreated == default ? DateTimeOffset.Now : sourceFile.DateCreated,
                isVideo ? ConversationSource.ImportedVideoAudio : ConversationSource.ImportedAudio);
            conversation.SetDuration(duration);

            var track = new AudioTrack(
                Guid.NewGuid(),
                conversationId,
                AudioTrackKind.Primary,
                paths.ToRelativePath(storedAudio.Path),
                sourceFile.Name,
                string.IsNullOrWhiteSpace(storedAudio.ContentType) ? "application/octet-stream" : storedAudio.ContentType,
                checked((long)properties.Size),
                duration,
                sourceSha256);

            await audioArchiveRepository.ImportAsync(conversation, track, cancellationToken);
            return new AudioImportResult(conversation, track, WasAlreadyImported: false);
        }
        catch
        {
            TryDeleteDirectory(meetingDirectory);
            throw;
        }
    }

    private async Task<AudioImportResult?> FindExistingImportAsync(
        string sourceSha256,
        CancellationToken cancellationToken)
    {
        AudioTrack? matchedTrack = await audioArchiveRepository.FindPrimaryBySourceHashAsync(
            sourceSha256,
            cancellationToken);

        if (matchedTrack is null)
        {
            IReadOnlyList<AudioTrack> legacyTracks =
                await audioArchiveRepository.ListPrimaryWithoutSourceHashAsync(cancellationToken);

            foreach (AudioTrack legacyTrack in legacyTracks)
            {
                string archivedFilePath = paths.ToAbsolutePath(legacyTrack.RelativePath);
                if (!File.Exists(archivedFilePath))
                {
                    continue;
                }

                string archivedHash;
                await using (var archivedStream = new FileStream(
                                 archivedFilePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 bufferSize: 128 * 1024,
                                 useAsync: true))
                {
                    archivedHash = await ComputeSha256Async(archivedStream, cancellationToken);
                }

                if (!string.Equals(archivedHash, sourceSha256, StringComparison.Ordinal))
                {
                    continue;
                }

                await audioArchiveRepository.TrySetSourceHashAsync(
                    legacyTrack.Id,
                    sourceSha256,
                    cancellationToken);

                matchedTrack = await audioArchiveRepository.FindPrimaryBySourceHashAsync(
                                   sourceSha256,
                                   cancellationToken)
                               ?? legacyTrack;
                break;
            }
        }

        if (matchedTrack is null)
        {
            return null;
        }

        Conversation? conversation = await conversationRepository.GetByIdAsync(
            matchedTrack.ConversationId,
            cancellationToken);

        return conversation is null
            ? null
            : new AudioImportResult(conversation, matchedTrack, WasAlreadyImported: true);
    }

    private static async Task<string> ComputeSourceHashAsync(
        StorageFile sourceFile,
        CancellationToken cancellationToken)
    {
        using Stream stream = await sourceFile.OpenStreamForReadAsync();
        return await ComputeSha256Async(stream, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<StorageFile> CopyAudioAsync(
        StorageFile sourceFile,
        StorageFolder destinationFolder)
    {
        string extension = sourceFile.FileType.ToLowerInvariant();
        return await sourceFile.CopyAsync(
            destinationFolder,
            $"primary{extension}",
            NameCollisionOption.ReplaceExisting);
    }

    private static async Task<StorageFile> ExtractAudioAsync(
        StorageFile sourceFile,
        StorageFolder destinationFolder)
    {
        StorageFile destinationFile = await destinationFolder.CreateFileAsync(
            "primary.mp3",
            CreationCollisionOption.ReplaceExisting);

        var transcoder = new MediaTranscoder();
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
        PrepareTranscodeResult preparation = await transcoder.PrepareFileTranscodeAsync(
            sourceFile,
            destinationFile,
            profile);

        if (!preparation.CanTranscode)
        {
            await destinationFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
            throw new InvalidOperationException(
                $"Das Audio konnte nicht aus der Videodatei extrahiert werden ({preparation.FailureReason}).");
        }

        await preparation.TranscodeAsync();
        return destinationFile;
    }

    private static async Task<TimeSpan> ReadDurationAsync(
        StorageFile audioFile,
        StorageFile? originalVideo)
    {
        MusicProperties musicProperties = await audioFile.Properties.GetMusicPropertiesAsync();
        if (musicProperties.Duration > TimeSpan.Zero)
        {
            return musicProperties.Duration;
        }

        if (originalVideo is not null)
        {
            VideoProperties videoProperties = await originalVideo.Properties.GetVideoPropertiesAsync();
            return videoProperties.Duration;
        }

        return TimeSpan.Zero;
    }

    private static void TryDeleteDirectory(string directory)
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
            // The original import error is more useful than a cleanup error.
        }
    }
}
