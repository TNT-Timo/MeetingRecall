using MeetingRecall.Core.Entities;

namespace MeetingRecall.Core.Interfaces;

public interface IMeetingSummaryRepository
{
    Task<MeetingSummary?> GetByConversationIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        MeetingSummary summary,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
