using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Interfaces;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
