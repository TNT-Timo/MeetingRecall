using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Interfaces;

public interface ISpeakerRepository
{
    Task<Speaker?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Speaker>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(Speaker speaker, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);
}
