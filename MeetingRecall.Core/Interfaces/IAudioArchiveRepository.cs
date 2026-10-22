using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Interfaces;

public interface IAudioArchiveRepository
{
    Task ImportAsync(
        Conversation conversation,
        AudioTrack primaryTrack,
        CancellationToken cancellationToken = default);

    Task ImportAsync(
        Conversation conversation,
        IReadOnlyCollection<AudioTrack> tracks,
        CancellationToken cancellationToken = default);

    Task<AudioTrack?> GetPrimaryAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<AudioTrack?> FindPrimaryBySourceHashAsync(
        string sourceSha256,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudioTrack>> ListPrimaryWithoutSourceHashAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TrySetSourceHashAsync(
        Guid trackId,
        string sourceSha256,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudioTrack>> ListAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
