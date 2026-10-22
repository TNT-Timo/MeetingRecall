using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;

namespace MeetingRecall.Infrastructure.Intelligence;

// Keep the public service and model-routing markers stable. This change is Q&A-only.
internal sealed class GroundedConversationQuestionService(
    IConversationRepository conversationRepository,
    ISpeakerRepository speakerRepository,
    ILocalLanguageModelService languageModel) : ILocalConversationQuestionService
{
    internal const string GroundedQaProfileMarker = "MEETINGRECALL GROUNDED QA";
    internal const string GroundedQaVerificationMarker = "MEETINGRECALL GROUNDED QA VERIFICATION";
    private readonly ContextualQuestionPipeline _pipeline = new(languageModel);

    public bool IsModelInstalled => languageModel.IsModelInstalled;
    public string ModelDisplayName => languageModel.ModelDisplayName;
    public long ModelDownloadSizeBytes => languageModel.ModelDownloadSizeBytes;

    public async Task<GroundedConversationAnswer> AnswerAsync(
        Guid conversationId, string question,
        IReadOnlyList<ConversationQuestionTurn>? recentTurns = null,
        IProgress<ConversationQuestionProgress>? progress = null,
        CancellationToken cancellationToken = default, bool forceAnswer = false)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("Eine Frage benötigt ein Gespräch.", nameof(conversationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (question.Trim().Length > 1_000)
            throw new ArgumentException("Eine Frage darf höchstens 1000 Zeichen lang sein.", nameof(question));
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(ConversationQuestionStage.SearchingTranscript, 0));
        var conversation = await conversationRepository.GetByIdAsync(conversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Das Gespräch '{conversationId}' wurde nicht gefunden.");
        var speakers = await speakerRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        return await _pipeline.AnswerAsync(conversation,
            speakers.ToDictionary(s => s.Id, s => s.DisplayName),
            question.Trim(), recentTurns ?? [], progress, cancellationToken, forceAnswer)
            .ConfigureAwait(false);
    }
}
