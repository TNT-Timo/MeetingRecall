using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
using System.Text;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Desktop.Controls;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, ConversationQuestionSession>
        _conversationQuestionSessions = [];
    private readonly Dictionary<Guid, Task> _conversationQuestionLoadTasks = [];
    private readonly HashSet<Guid> _conversationQuestionLoadFailures = [];
    private ILocalConversationQuestionService? _conversationQuestionService;
    private IConversationQuestionHistoryRepository?
        _conversationQuestionHistoryRepository;
    private ConversationQuestionPanel? _conversationQuestionPanel;
    private Guid? _activeQuestionConversationId;
    private bool _conversationQuestionUiInitialized;

    public void InitializeConversationQuestions()
    {
        if (_conversationQuestionUiInitialized)
        {
            return;
        }

        _conversationQuestionUiInitialized = true;
        _conversationQuestionService = App.Services
            .GetRequiredService<ILocalConversationQuestionService>();
        _conversationQuestionHistoryRepository = App.Services
            .GetRequiredService<IConversationQuestionHistoryRepository>();
        RootGrid.Loaded += ConversationQuestionRootGrid_Loaded;
        ConversationsList.SelectionChanged +=
            ConversationsList_ConversationQuestionSelectionChanged;
        Closed += ConversationQuestionWindow_Closed;
    }

    private ILocalConversationQuestionService ConversationQuestionService =>
        _conversationQuestionService
        ?? throw new InvalidOperationException(
            "Der lokale Gesprächschat wurde nicht initialisiert.");

    private IConversationQuestionHistoryRepository
        ConversationQuestionHistoryRepository =>
        _conversationQuestionHistoryRepository
        ?? throw new InvalidOperationException(
            "Der Speicher für Gesprächschats wurde nicht initialisiert.");

    private async void ConversationQuestionRootGrid_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        InstallConversationQuestionPanel();
        await LoadSelectedConversationQuestionsAsync();
    }

    private void InstallConversationQuestionPanel()
    {
        if (_conversationQuestionPanel is not null)
        {
            return;
        }

        _conversationQuestionPanel = new ConversationQuestionPanel(Translate);
        _conversationQuestionPanel.QuestionSubmitted +=
            ConversationQuestionPanel_QuestionSubmitted;
        _conversationQuestionPanel.CancellationRequested +=
            ConversationQuestionPanel_CancellationRequested;
        _conversationQuestionPanel.ClearRequested +=
            ConversationQuestionPanel_ClearRequested;
        _conversationQuestionPanel.SourceRequested +=
            ConversationQuestionPanel_SourceRequested;
        _conversationQuestionPanel.ForceAnswerRequested +=
            ConversationQuestionPanel_ForceAnswerRequested;
        _conversationQuestionPanel.MessageDeleteRequested +=
            ConversationQuestionPanel_MessageDeleteRequested;

        var card = new Border
        {
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(16),
            Background = GetThemeBrush(
                "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetThemeBrush(
                "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = _conversationQuestionPanel
        };
        ConversationDetailContent.Children.Add(card);
    }

    private async void ConversationsList_ConversationQuestionSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        await LoadSelectedConversationQuestionsAsync();
    }

    private async Task LoadSelectedConversationQuestionsAsync()
    {
        Conversation? conversation =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation;
        if (conversation is null)
        {
            RenderSelectedConversationQuestions();
            return;
        }

        try
        {
            _conversationQuestionLoadFailures.Remove(conversation.Id);
            await EnsureConversationQuestionSessionLoadedAsync(conversation);
        }
        catch (Exception exception)
        {
            _conversationQuestionLoadFailures.Add(conversation.Id);
            await ShowMessageAsync(
                Translate(
                    "Gesprächschat konnte nicht geladen werden",
                    "Conversation chat could not be loaded"),
                exception.Message,
                Translate("Schließen", "Close"));
        }

        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversation.Id)
        {
            RenderSelectedConversationQuestions();
        }
    }

    private void RenderSelectedConversationQuestions()
    {
        Conversation? conversation =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation;
        if (conversation is null)
        {
            _conversationQuestionPanel?.ShowConversation(
                null,
                []);
            return;
        }

        string fingerprint = BuildTranscriptFingerprint(conversation);
        if (!_conversationQuestionSessions.TryGetValue(
                conversation.Id,
                out ConversationQuestionSession? session)
            || !string.Equals(
                session.TranscriptFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            _conversationQuestionPanel?.ShowConversation(
                conversation,
                [],
                inputLocked: true);
            if (!_conversationQuestionLoadFailures.Contains(conversation.Id))
            {
                _ = LoadSelectedConversationQuestionsAsync();
            }
            return;
        }

        _conversationQuestionPanel?.ShowConversation(
            conversation,
            session.Exchanges,
            isBusy: false,
            inputLocked: _isBusy);

        if (TryGetProcessingJobState(
                conversation.Id,
                out ProcessingJobViewState queueState,
                ProcessingJobKind.Question))
        {
            _conversationQuestionPanel?.ShowQueueState(
                queueState.Position,
                queueState.IsActive,
                queueState.MatchingJobs,
                inputLocked: _isBusy);
        }
    }

    private async void ConversationQuestionPanel_QuestionSubmitted(
        string question)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        if (conversation.TranscriptSegments.Count == 0)
        {
            return;
        }

        if (!ConversationQuestionService.IsModelInstalled
            && !await ConfirmConversationQuestionModelDownloadAsync())
        {
            return;
        }

        ConversationQuestionSession session = GetOrCreateQuestionSession(
            conversation);
        Guid exchangeId = Guid.NewGuid();
        session.Exchanges.Add(new ConversationQuestionExchange(
            exchangeId,
            question,
            null));
        RenderSelectedConversationQuestions();

        await RunConversationQuestionAsync(
            conversation,
            session,
            exchangeId,
            question,
            forceAnswer: false,
            restoreAnswer: null);
    }

    private async void ConversationQuestionPanel_ForceAnswerRequested(
        int exchangeIndex)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        ConversationQuestionSession session = GetOrCreateQuestionSession(
            conversation);
        if (exchangeIndex < 0
            || exchangeIndex >= session.Exchanges.Count
            || session.Exchanges[exchangeIndex].Answer is not
                { IsAnswerable: false } rejectedAnswer)
        {
            return;
        }

        if (!ConversationQuestionService.IsModelInstalled
            && !await ConfirmConversationQuestionModelDownloadAsync())
        {
            return;
        }

        ConversationQuestionExchange exchange =
            session.Exchanges[exchangeIndex];
        session.Exchanges[exchangeIndex] = exchange with
        {
            Answer = null
        };
        RenderSelectedConversationQuestions();

        await RunConversationQuestionAsync(
            conversation,
            session,
            exchange.Id,
            exchange.Question,
            forceAnswer: true,
            restoreAnswer: rejectedAnswer);
    }

    private async Task RunConversationQuestionAsync(
        Conversation conversation,
        ConversationQuestionSession session,
        Guid exchangeId,
        string question,
        bool forceAnswer,
        GroundedConversationAnswer? restoreAnswer)
    {
        ProcessingJobHandle handle = EnqueueProcessingJob(
            ProcessingJobKind.Question,
            conversation.Id,
            conversation.Title,
            forceAnswer
                ? Translate(
                    "Erzwungene Chat-Antwort",
                    "Forced chat answer")
                : Translate(
                    "Chat-Antwort",
                    "Chat answer"),
            cancellationToken => RunConversationQuestionJobAsync(
                conversation,
                session,
                exchangeId,
                question,
                forceAnswer,
                restoreAnswer,
                cancellationToken),
            allowDuplicate: true,
            canceledBeforeStart: () =>
            {
                RestoreOrRemovePendingQuestion(
                    session,
                    exchangeId,
                    restoreAnswer);
                RenderSelectedConversationQuestions();
            });

        await handle.Completion;
    }

    private async Task<ProcessingJobResult> RunConversationQuestionJobAsync(
        Conversation conversation,
        ConversationQuestionSession session,
        Guid exchangeId,
        string question,
        bool forceAnswer,
        GroundedConversationAnswer? restoreAnswer,
        CancellationToken cancellationToken)
    {
        int pendingIndex = session.Exchanges.FindIndex(exchange =>
            exchange.Id == exchangeId);
        if (pendingIndex < 0)
        {
            return ProcessingJobResult.Canceled;
        }

        ConversationQuestionTurn[] history =
            BuildConversationQuestionHistory(
                session,
                pendingIndex);
        _activeQuestionConversationId = conversation.Id;
        var progress = new Progress<ConversationQuestionProgress>(item =>
            UpdateConversationQuestionProgress(conversation.Id, item));
        RenderSelectedConversationQuestions();

        try
        {
            GroundedConversationAnswer answer = await ConversationQuestionService
                .AnswerAsync(
                    conversation.Id,
                    question,
                    history,
                    progress,
                    cancellationToken,
                    forceAnswer);
            int currentIndex = session.Exchanges.FindIndex(exchange =>
                exchange.Id == exchangeId);
            if (currentIndex >= 0)
            {
                session.Exchanges[currentIndex] =
                    session.Exchanges[currentIndex] with
                    {
                        Answer = answer
                    };
                await SaveConversationQuestionSessionAsync(
                    conversation.Id,
                    session);
            }
            return ProcessingJobResult.Completed;
        }
        catch (OperationCanceledException)
        {
            RestoreOrRemovePendingQuestion(
                session,
                exchangeId,
                restoreAnswer);
            return ProcessingJobResult.Canceled;
        }
        catch (Exception exception)
        {
            RestoreOrRemovePendingQuestion(
                session,
                exchangeId,
                restoreAnswer);
            await ShowMessageAsync(
                Translate(
                    "Frage konnte nicht beantwortet werden",
                    "Question could not be answered"),
                exception.Message,
                Translate("Schließen", "Close"));
            return ProcessingJobResult.Failed;
        }
        finally
        {
            _activeQuestionConversationId = null;
            ClearProcessingStatus();
            RenderSelectedConversationQuestions();
        }
    }

    private static ConversationQuestionTurn[] BuildConversationQuestionHistory(
        ConversationQuestionSession session,
        int excludedIndex = -1) =>
        session.Exchanges
            .Where((exchange, index) =>
                index != excludedIndex
                && exchange.Answer is
                {
                    IsAnswerable: true,
                    IsBestEffort: false
                })
            .TakeLast(4)
            .Select(exchange => new ConversationQuestionTurn(
                exchange.Question,
                exchange.Answer!.Text))
            .ToArray();

    private static void RestoreOrRemovePendingQuestion(
        ConversationQuestionSession session,
        Guid exchangeId,
        GroundedConversationAnswer? restoreAnswer)
    {
        int index = session.Exchanges.FindIndex(exchange =>
            exchange.Id == exchangeId);
        if (index < 0)
        {
            return;
        }

        if (restoreAnswer is null)
        {
            session.Exchanges.RemoveAt(index);
        }
        else
        {
            session.Exchanges[index] =
                session.Exchanges[index] with
                {
                    Answer = restoreAnswer
                };
        }
    }

    private void UpdateConversationQuestionProgress(
        Guid conversationId,
        ConversationQuestionProgress progress)
    {
        if (_activeQuestionConversationId != conversationId)
        {
            return;
        }

        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversationId)
        {
            _conversationQuestionPanel?.ShowProgress(
                progress,
                allowInputWhileProcessing: true);
        }

        string status = progress.Stage switch
        {
            ConversationQuestionStage.DownloadingModel =>
                FormatConversationQuestionDownloadStatus(progress),
            ConversationQuestionStage.SearchingTranscript => Translate(
                "Passende Transkriptstellen werden gesucht …",
                "Finding relevant transcript passages …"),
            ConversationQuestionStage.LoadingModel => Translate(
                "Sprachmodell wird lokal geladen …",
                "Loading language model locally …"),
            ConversationQuestionStage.FormulatingAnswer => Translate(
                "Antwort wird ausschließlich aus den Quellen formuliert …",
                "Writing an answer using only the sources …"),
            ConversationQuestionStage.VerifyingAnswer => Translate(
                "Antwortaussagen werden gegen das Transkript geprüft …",
                "Checking answer claims against the transcript …"),
            _ => string.Empty
        };
        SetProcessingStatus(status);
    }

    private string FormatConversationQuestionDownloadStatus(
        ConversationQuestionProgress progress)
    {
        double percentage = Math.Clamp(
            (progress.Fraction ?? 0) * 100,
            0,
            100);
        string speed = progress.BytesPerSecond is > 0
            ? $" · {progress.BytesPerSecond.Value / 1_000_000:0.0} MB/s"
              + $" · {progress.BytesPerSecond.Value * 8 / 1_000_000:0.0} Mbit/s"
            : string.Empty;
        return Translate("Sprachmodell", "Language model")
            + $" · {percentage:0.0} %"
            + speed;
    }

    private void ConversationQuestionPanel_CancellationRequested(
        object? sender,
        EventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationListItem selectedItem)
        {
            CancelNextProcessingJob(
                selectedItem.Conversation.Id,
                ProcessingJobKind.Question);
        }
    }

    private async void ConversationQuestionPanel_ClearRequested(
        object? sender,
        EventArgs e)
    {
        if (ConversationsList.SelectedItem
                is not ConversationListItem selectedItem
            || HasProcessingJobForConversation(
                selectedItem.Conversation.Id))
        {
            return;
        }

        Guid conversationId = selectedItem.Conversation.Id;
        if (!_conversationQuestionSessions.TryGetValue(
                conversationId,
                out ConversationQuestionSession? session))
        {
            return;
        }

        if (session.Exchanges.Count == 0
            || !await ConfirmConversationQuestionClearAsync(
                selectedItem.Conversation.Title))
        {
            return;
        }

        ConversationQuestionExchange[] previous = session.Exchanges.ToArray();
        session.Exchanges.Clear();
        RenderSelectedConversationQuestions();
        try
        {
            await ConversationQuestionHistoryRepository.DeleteAsync(
                conversationId);
        }
        catch (Exception exception)
        {
            session.Exchanges.AddRange(previous);
            RenderSelectedConversationQuestions();
            await ShowMessageAsync(
                Translate(
                    "Chat konnte nicht gelöscht werden",
                    "Chat could not be deleted"),
                exception.Message,
                Translate("Schließen", "Close"));
        }
    }

    private async Task<bool> ConfirmConversationQuestionClearAsync(
        string conversationTitle)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "Gesamten Chat löschen?",
                "Delete the entire chat?"),
            Content = Translate(
                $"Alle Fragen und Antworten im Chat zu „{conversationTitle}“ werden dauerhaft gelöscht. Das Gespräch, Audio und Transkript bleiben erhalten. Diese Aktion kann nicht rückgängig gemacht werden.",
                $"All questions and answers in the chat for “{conversationTitle}” will be permanently deleted. The conversation, audio, and transcript remain unchanged. This action cannot be undone."),
            PrimaryButtonText = Translate("Chat löschen", "Delete chat"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void ConversationQuestionPanel_MessageDeleteRequested(
        Guid exchangeId,
        ConversationQuestionMessageKind messageKind)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem
            || HasProcessingJobForConversation(
                selectedItem.Conversation.Id))
        {
            return;
        }

        ConversationQuestionSession session = GetOrCreateQuestionSession(
            selectedItem.Conversation);
        int index = session.Exchanges.FindIndex(exchange =>
            exchange.Id == exchangeId);
        if (index < 0)
        {
            return;
        }

        ConversationQuestionExchange previous = session.Exchanges[index];
        if (messageKind == ConversationQuestionMessageKind.Question)
        {
            session.Exchanges.RemoveAt(index);
        }
        else if (previous.Answer is not null)
        {
            session.Exchanges[index] = previous with { Answer = null };
        }
        else
        {
            return;
        }

        RenderSelectedConversationQuestions();
        try
        {
            await SaveConversationQuestionSessionAsync(
                selectedItem.Conversation.Id,
                session);
        }
        catch (Exception exception)
        {
            if (messageKind == ConversationQuestionMessageKind.Question)
            {
                session.Exchanges.Insert(index, previous);
            }
            else
            {
                session.Exchanges[index] = previous;
            }

            RenderSelectedConversationQuestions();
            await ShowMessageAsync(
                Translate(
                    "Nachricht konnte nicht gelöscht werden",
                    "Message could not be deleted"),
                exception.Message,
                Translate("Schließen", "Close"));
        }
    }

    private void ConversationQuestionPanel_SourceRequested(
        TimeSpan position)
    {
        TranscriptPanel_TimestampRequested(position);
        _transcriptPanel?.HopTo(position);
    }

    private async Task<bool> ConfirmConversationQuestionModelDownloadAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "Lokales Sprachmodell herunterladen?",
                "Download local language model?"),
            Content = Translate(
                $"Für Fragen zum Gespräch benötigt MeetingRecall einmalig „{ConversationQuestionService.ModelDisplayName}“ (ca. {FormatFileSize(ConversationQuestionService.ModelDownloadSizeBytes)}). Das Modell beantwortet Fragen vollständig lokal; Audio, Transkript und Chat werden nicht hochgeladen.",
                $"Questions about a conversation require a one-time download of “{ConversationQuestionService.ModelDisplayName}” (about {FormatFileSize(ConversationQuestionService.ModelDownloadSizeBytes)}). Questions are answered entirely locally; audio, transcript, and chat are never uploaded."),
            PrimaryButtonText = Translate("Herunterladen", "Download"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private ConversationQuestionSession GetOrCreateQuestionSession(
        Conversation conversation)
    {
        string fingerprint = BuildTranscriptFingerprint(conversation);
        if (!_conversationQuestionSessions.TryGetValue(
                conversation.Id,
                out ConversationQuestionSession? session)
            || !string.Equals(
                session.TranscriptFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            session = new ConversationQuestionSession(fingerprint);
            _conversationQuestionSessions[conversation.Id] = session;
        }

        return session;
    }

    private async Task EnsureConversationQuestionSessionLoadedAsync(
        Conversation conversation)
    {
        string fingerprint = BuildTranscriptFingerprint(conversation);
        if (_conversationQuestionSessions.TryGetValue(
                conversation.Id,
                out ConversationQuestionSession? session)
            && string.Equals(
                session.TranscriptFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!_conversationQuestionLoadTasks.TryGetValue(
                conversation.Id,
                out Task? loadTask))
        {
            loadTask = LoadConversationQuestionSessionCoreAsync(
                conversation.Id,
                fingerprint);
            _conversationQuestionLoadTasks[conversation.Id] = loadTask;
        }

        try
        {
            await loadTask;
        }
        finally
        {
            if (_conversationQuestionLoadTasks.TryGetValue(
                    conversation.Id,
                    out Task? currentTask)
                && ReferenceEquals(currentTask, loadTask))
            {
                _conversationQuestionLoadTasks.Remove(conversation.Id);
            }
        }
    }

    private async Task LoadConversationQuestionSessionCoreAsync(
        Guid conversationId,
        string fingerprint)
    {
        ConversationQuestionHistory? history =
            await ConversationQuestionHistoryRepository
                .GetByConversationIdAsync(conversationId);
        var session = new ConversationQuestionSession(fingerprint);
        if (history is not null
            && string.Equals(
                history.TranscriptFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            session.Exchanges.AddRange(history.Exchanges);
        }
        else if (history is not null)
        {
            await ConversationQuestionHistoryRepository.DeleteAsync(
                conversationId);
        }

        _conversationQuestionSessions[conversationId] = session;
    }

    private Task SaveConversationQuestionSessionAsync(
        Guid conversationId,
        ConversationQuestionSession session) =>
        ConversationQuestionHistoryRepository.SaveAsync(
            new ConversationQuestionHistory(
                conversationId,
                session.TranscriptFingerprint,
                session.Exchanges
                    .Where(exchange => exchange.Answer is not null)
                    .ToArray()));

    private static string BuildTranscriptFingerprint(
        Conversation conversation)
    {
        var source = new StringBuilder();
        foreach (TranscriptSegment segment in conversation.TranscriptSegments)
        {
            source.Append(segment.Id.ToString("N"));
            source.Append(segment.SpeakerId.ToString("N"));
            source.Append(segment.CurrentText.Length);
            source.Append(':');
            source.Append(segment.CurrentText);
            source.Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private void ConversationQuestionWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        RootGrid.Loaded -= ConversationQuestionRootGrid_Loaded;
        ConversationsList.SelectionChanged -=
            ConversationsList_ConversationQuestionSelectionChanged;
        if (_conversationQuestionPanel is not null)
        {
            _conversationQuestionPanel.QuestionSubmitted -=
                ConversationQuestionPanel_QuestionSubmitted;
            _conversationQuestionPanel.CancellationRequested -=
                ConversationQuestionPanel_CancellationRequested;
            _conversationQuestionPanel.ClearRequested -=
                ConversationQuestionPanel_ClearRequested;
            _conversationQuestionPanel.SourceRequested -=
                ConversationQuestionPanel_SourceRequested;
            _conversationQuestionPanel.ForceAnswerRequested -=
                ConversationQuestionPanel_ForceAnswerRequested;
            _conversationQuestionPanel.MessageDeleteRequested -=
                ConversationQuestionPanel_MessageDeleteRequested;
        }
    }

    private sealed class ConversationQuestionSession(
        string transcriptFingerprint)
    {
        public string TranscriptFingerprint { get; } = transcriptFingerprint;

        public List<ConversationQuestionExchange> Exchanges { get; } = [];
    }
}
