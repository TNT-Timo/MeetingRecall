using MeetingRecall.Core.Interfaces;

namespace MeetingRecall.Infrastructure.Storage;

public sealed class ConversationDeletionService(
    AppDataPaths paths,
    IConversationRepository conversationRepository)
{
    private const int DeleteAttempts = 4;

    public async Task<bool> DeleteAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The conversation identifier cannot be empty.",
                nameof(conversationId));
        }

        string meetingDirectory = paths.GetMeetingDirectory(conversationId);
        string? stagedDirectory = StageMeetingDirectory(
            conversationId,
            meetingDirectory);

        bool deleted;
        try
        {
            deleted = await conversationRepository
                .DeleteAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            RestoreStagedDirectory(stagedDirectory, meetingDirectory);
            throw;
        }

        if (stagedDirectory is not null)
        {
            await DeleteDirectoryCompletelyAsync(
                    stagedDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            DeleteEmptyPendingDirectory();
        }

        return deleted;
    }

    private string? StageMeetingDirectory(
        Guid conversationId,
        string meetingDirectory)
    {
        if (!Directory.Exists(meetingDirectory))
        {
            return null;
        }

        string pendingDirectory = Path.Combine(
            paths.TempDirectory,
            "PendingConversationDeletion");
        Directory.CreateDirectory(pendingDirectory);
        string stagedDirectory = Path.Combine(
            pendingDirectory,
            $"{conversationId:N}-{Guid.NewGuid():N}");
        Directory.Move(meetingDirectory, stagedDirectory);
        return stagedDirectory;
    }

    private static void RestoreStagedDirectory(
        string? stagedDirectory,
        string meetingDirectory)
    {
        if (stagedDirectory is null || !Directory.Exists(stagedDirectory))
        {
            return;
        }

        if (Directory.Exists(meetingDirectory))
        {
            throw new IOException(
                "The conversation files could not be restored because the original directory exists again.");
        }

        Directory.Move(stagedDirectory, meetingDirectory);
    }

    private static async Task DeleteDirectoryCompletelyAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                NormalizeFileAttributes(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < DeleteAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < DeleteAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void NormalizeFileAttributes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private void DeleteEmptyPendingDirectory()
    {
        string pendingDirectory = Path.Combine(
            paths.TempDirectory,
            "PendingConversationDeletion");
        if (Directory.Exists(pendingDirectory)
            && !Directory.EnumerateFileSystemEntries(pendingDirectory).Any())
        {
            Directory.Delete(pendingDirectory);
        }
    }
}
