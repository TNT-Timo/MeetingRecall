using MeetingRecall.Core.Intelligence;

namespace MeetingRecall.Core.Interfaces;

public sealed record ConversationQuestionExchange(
    Guid Id,
    string Question,
    GroundedConversationAnswer? Answer);

public sealed record ConversationQuestionHistory(
    Guid ConversationId,
    string TranscriptFingerprint,
    IReadOnlyList<ConversationQuestionExchange> Exchanges);

public interface IConversationQuestionHistoryRepository
{
    Task<ConversationQuestionHistory?> GetByConversationIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ConversationQuestionHistory history,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
