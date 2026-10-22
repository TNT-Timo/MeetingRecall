using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Interfaces;

public interface IConversationGroupRepository
{
    Task<ConversationGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationGroup>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(ConversationGroup group, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
