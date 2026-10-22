using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Settings;
using MeetingRecall.Desktop.Controls;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private ILocalMeetingIntelligenceService? _meetingIntelligenceService;
    private IMeetingSummaryRepository? _meetingSummaryRepository;
    private MeetingSummaryPanel? _meetingSummaryPanel;
    private bool _summaryUiInitialized;
    private readonly Stopwatch _summaryProgressEtaStopwatch = new();
    private double _summaryProgressEtaBaselineFraction;
    private double? _summaryProgressEtaLastFraction;
    private double? _summaryProgressEtaSmoothedSeconds;
    private bool _summaryProgressEtaWasDownloading;

    public void InitializeMeetingIntelligence()
    {
        if (_summaryUiInitialized)
        {
            return;
        }

        _summaryUiInitialized = true;
        _meetingIntelligenceService =
            App.Services.GetRequiredService<ILocalMeetingIntelligenceService>();
        _meetingSummaryRepository =
            App.Services.GetRequiredService<IMeetingSummaryRepository>();
        RootGrid.Loaded += SummaryRootGrid_Loaded;
        ConversationsList.SelectionChanged +=
            ConversationsList_SummarySelectionChanged;
        Closed += SummaryWindow_Closed;
    }

    private ILocalMeetingIntelligenceService MeetingIntelligenceService =>
        _meetingIntelligenceService
        ?? throw new InvalidOperationException(
            "Die lokale Zusammenfassung wurde nicht initialisiert.");

    private IMeetingSummaryRepository MeetingSummaryRepository =>
        _meetingSummaryRepository
        ?? throw new InvalidOperationException(
            "Die Zusammenfassungspersistenz wurde nicht initialisiert.");

    private void SummaryRootGrid_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        InstallSummaryPanel();
        RenderSelectedSummary();
    }

    private void InstallSummaryPanel()
    {
        if (_meetingSummaryPanel is not null)
        {
            return;
        }

        _meetingSummaryPanel = new MeetingSummaryPanel(Translate);
        _meetingSummaryPanel.GenerationRequested +=
            MeetingSummaryPanel_GenerationRequested;
        _meetingSummaryPanel.ThreeVersionGenerationRequested +=
            MeetingSummaryPanel_ThreeVersionGenerationRequested;
        _meetingSummaryPanel.OpenThreeVersionPickerRequested +=
            MeetingSummaryPanel_OpenThreeVersionPickerRequested;
        _meetingSummaryPanel.CancellationRequested +=
            MeetingSummaryPanel_CancellationRequested;
        _meetingSummaryPanel.ApplySuggestedTitleRequested +=
            MeetingSummaryPanel_ApplySuggestedTitleRequested;
        _meetingSummaryPanel.GenerateAnotherTitleRequested +=
            MeetingSummaryPanel_GenerateAnotherTitleRequested;
        _meetingSummaryPanel.CustomTitleRequested +=
            MeetingSummaryPanel_CustomTitleRequested;

        var card = new Border
        {
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(16),
            Background = GetThemeBrush(
                "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetThemeBrush(
                "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = _meetingSummaryPanel
        };
        ConversationDetailContent.Children.Add(card);
    }

    private void ConversationsList_SummarySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RenderSelectedSummary();
    }

    private async void RenderSelectedSummary()
    {
        ConversationListItem? selectedItem =
            ConversationsList.SelectedItem as ConversationListItem;
        Conversation? conversation = selectedItem?.Conversation;
        Guid? selectedId = conversation?.Id;
        MeetingSummary? summary = conversation is null
            ? null
            : await MeetingSummaryRepository
                .GetByConversationIdAsync(conversation.Id);

        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id != selectedId)
        {
            return;
        }

        _meetingSummaryPanel?.ShowConversation(
            conversation,
            summary);

        if (conversation is not null
            && TryGetProcessingJobState(
                conversation.Id,
                out ProcessingJobViewState queueState,
                ProcessingJobKind.Summary,
                ProcessingJobKind.ThreeVersionSummary,
                ProcessingJobKind.TitleSuggestion))
        {
            if (queueState.Kind == ProcessingJobKind.TitleSuggestion)
            {
                _meetingSummaryPanel?.SetTitleGenerationBusy(true);
            }
            else
            {
                string operation = queueState.Kind
                    == ProcessingJobKind.ThreeVersionSummary
                        ? Translate(
                            "Drei Zusammenfassungen",
                            "Three summaries")
                        : Translate(
                            "Zusammenfassung",
                            "Summary");
                _meetingSummaryPanel?.ShowQueued(
                    operation,
                    queueState.Position,
                    queueState.IsActive);
            }
            return;
        }

        _meetingSummaryPanel?.SetTitleGenerationBusy(false);
        TryShowPendingThreeVersionDrafts(conversation);
    }

    private async void MeetingSummaryPanel_GenerationRequested(
        object? sender,
        EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        await QueueSummaryGenerationAsync(selectedItem.Conversation);
    }

    private async Task<bool> QueueSummaryGenerationAsync(
        Guid conversationId,
        bool confirmModelDownload = true)
    {
        Conversation? conversation = await _conversationRepository
            .GetByIdAsync(conversationId);
        return conversation is not null
            && await QueueSummaryGenerationAsync(
                conversation,
                confirmModelDownload);
    }

    private async Task<bool> QueueSummaryGenerationAsync(
        Conversation conversation,
        bool confirmModelDownload = true)
    {
        if (conversation.TranscriptSegments.Count == 0)
        {
            return false;
        }

        if (confirmModelDownload
            && !MeetingIntelligenceService.IsModelInstalled
            && !await ConfirmLanguageModelDownloadAsync())
        {
            return false;
        }

        DiscardPendingThreeVersionDrafts(conversation.Id);
        ProcessingJobHandle handle = EnqueueProcessingJob(
            ProcessingJobKind.Summary,
            conversation.Id,
            conversation.Title,
            Translate("Zusammenfassung", "Summary"),
            cancellationToken => RunSummaryGenerationJobAsync(
                conversation,
                cancellationToken));

        return await handle.Completion == ProcessingJobResult.Completed;
    }

    private async Task<ProcessingJobResult> RunSummaryGenerationJobAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        ResetMeetingIntelligenceEta();
        var progress = new Progress<MeetingIntelligenceProgress>(item =>
            UpdateMeetingIntelligenceProgress(
                conversation.Id,
                item));

        try
        {
            MeetingSummary summary = await MeetingIntelligenceService
                .GenerateSummaryAsync(
                    conversation.Id,
                    progress,
                    cancellationToken);
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                _meetingSummaryPanel?.ShowConversation(
                    conversation,
                    summary);
            }
            return ProcessingJobResult.Completed;
        }
        catch (OperationCanceledException)
        {
            return ProcessingJobResult.Canceled;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                Translate(
                    "Zusammenfassung fehlgeschlagen",
                    "Summary failed"),
                exception.Message,
                Translate("Schließen", "Close"));
            return ProcessingJobResult.Failed;
        }
        finally
        {
            ResetMeetingIntelligenceEta(stop: true);
            ClearProcessingStatus();
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                RenderSelectedSummary();
            }
        }
    }

    private async void MeetingSummaryPanel_GenerateAnotherTitleRequested(
        object? sender,
        EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        if (!MeetingIntelligenceService.IsModelInstalled
            && !await ConfirmLanguageModelDownloadAsync())
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        ProcessingJobHandle handle = EnqueueProcessingJob(
            ProcessingJobKind.TitleSuggestion,
            conversation.Id,
            conversation.Title,
            Translate("Titelvorschlag", "Title suggestion"),
            cancellationToken => RunTitleGenerationJobAsync(
                conversation,
                cancellationToken));

        await handle.Completion;
    }

    private async Task<ProcessingJobResult> RunTitleGenerationJobAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        ResetMeetingIntelligenceEta();
        var progress = new Progress<MeetingIntelligenceProgress>(item =>
            UpdateMeetingIntelligenceProgress(
                conversation.Id,
                item,
                titleOnly: true));

        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversation.Id)
        {
            _meetingSummaryPanel?.SetTitleGenerationBusy(true);
        }

        try
        {
            MeetingSummary summary = await MeetingIntelligenceService
                .GenerateAnotherTitleAsync(
                    conversation.Id,
                    progress,
                    cancellationToken);
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                _meetingSummaryPanel?.ShowConversation(
                    conversation,
                    summary);
            }
            return ProcessingJobResult.Completed;
        }
        catch (OperationCanceledException)
        {
            return ProcessingJobResult.Canceled;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                Translate(
                    "Titelvorschlag fehlgeschlagen",
                    "Title suggestion failed"),
                exception.Message,
                Translate("Schließen", "Close"));
            return ProcessingJobResult.Failed;
        }
        finally
        {
            ResetMeetingIntelligenceEta(stop: true);
            ClearProcessingStatus();
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                RenderSelectedSummary();
            }
        }
    }

    private async void MeetingSummaryPanel_CustomTitleRequested(
        object? sender,
        EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        var titleBox = new TextBox
        {
            Header = Translate("Eigener Titel", "Custom title"),
            Text = selectedItem.Conversation.Title,
            MaxLength = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate("Eigenen Titel festlegen", "Set custom title"),
            Content = titleBox,
            PrimaryButtonText = Translate("Titel speichern", "Save title"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(titleBox.Text)
        };
        titleBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(titleBox.Text);
        };
        dialog.Opened += (_, _) =>
        {
            titleBox.Focus(FocusState.Programmatic);
            titleBox.SelectAll();
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ApplyConversationTitleAsync(titleBox.Text);
        }
    }

    private void MeetingSummaryPanel_CancellationRequested(
        object? sender,
        EventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationListItem selectedItem)
        {
            CancelNextProcessingJob(
                selectedItem.Conversation.Id,
                ProcessingJobKind.Summary,
                ProcessingJobKind.ThreeVersionSummary,
                ProcessingJobKind.TitleSuggestion);
        }
    }

    private async void MeetingSummaryPanel_ApplySuggestedTitleRequested(
        string suggestedTitle)
    {
        await ApplyConversationTitleAsync(suggestedTitle);
    }

    private async Task ApplyConversationTitleAsync(string title)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        try
        {
            if (!selectedItem.Conversation.Rename(title))
            {
                return;
            }

            SetBusy(true, Translate(
                "Titel wird gespeichert …",
                "Saving title …"));
            await _conversationRepository
                .UpsertAsync(selectedItem.Conversation);
            await LoadConversationsAsync(selectedItem.Conversation.Id);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                Translate(
                    "Titel konnte nicht geändert werden",
                    "Title could not be changed"),
                exception.Message,
                Translate("Schließen", "Close"));
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void UpdateMeetingIntelligenceProgress(
        Guid conversationId,
        MeetingIntelligenceProgress progress,
        bool titleOnly = false)
    {
        string? estimatedRemaining = FormatMeetingIntelligenceEta(progress);
        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversationId)
        {
            if (titleOnly)
            {
                _meetingSummaryPanel?.SetTitleGenerationBusy(true);
            }
            else
            {
                _meetingSummaryPanel?.ShowProgress(
                    progress,
                    estimatedRemaining);
            }
        }

        string status = progress.Stage switch
        {
            MeetingIntelligenceStage.DownloadingModel =>
                FormatLanguageModelDownloadStatus(progress),
            MeetingIntelligenceStage.LoadingModel =>
                Translate(
                    "Sprachmodell wird lokal geladen …",
                    "Loading language model locally …"),
            MeetingIntelligenceStage.AnalyzingTranscript =>
                Translate(
                    "Transkript wird analysiert …",
                    "Analyzing transcript …"),
            MeetingIntelligenceStage.SummarizingSections =>
                Translate(
                    "Abschnitte werden inhaltlich verstanden …",
                    "Understanding transcript sections …"),
            MeetingIntelligenceStage.BuildingSummary =>
                Translate(
                    "Zusammenfassung wird aufgebaut …",
                    "Building summary …"),
            MeetingIntelligenceStage.VerifyingSummary =>
                Translate(
                    "Aussagen werden gegen das Transkript geprüft …",
                    "Verifying claims against the transcript …"),
            MeetingIntelligenceStage.GeneratingTitle =>
                Translate(
                    "Neuer Titelvorschlag wird erstellt …",
                    "Creating another title suggestion …"),
            MeetingIntelligenceStage.Saving =>
                Translate(
                    "Zusammenfassung wird gespeichert …",
                    "Saving summary …"),
            MeetingIntelligenceStage.Completed => string.Empty,
            _ => Translate(
                "Zusammenfassung wird erstellt …",
                "Creating summary …")
        };
        if (progress.Stage != MeetingIntelligenceStage.DownloadingModel
            && !string.IsNullOrWhiteSpace(status)
            && !string.IsNullOrWhiteSpace(estimatedRemaining))
        {
            status += $" · {estimatedRemaining}";
        }

        SetProcessingStatus(status);
    }

    private string? FormatMeetingIntelligenceEta(
        MeetingIntelligenceProgress progress)
    {
        if (progress.Stage == MeetingIntelligenceStage.DownloadingModel)
        {
            _summaryProgressEtaWasDownloading = true;
            if (progress.TotalBytes is not > 0
                || progress.DownloadedBytes <= 0
                || progress.BytesPerSecond is not > 0)
            {
                return null;
            }

            double downloadSeconds =
                (progress.TotalBytes.Value - progress.DownloadedBytes)
                / progress.BytesPerSecond.Value;
            return downloadSeconds is >= 2 and <= 86_400
                ? FormatEstimatedRemaining(
                    TimeSpan.FromSeconds(downloadSeconds))
                : null;
        }

        if (_summaryProgressEtaWasDownloading)
        {
            _summaryProgressEtaWasDownloading = false;
            _summaryProgressEtaBaselineFraction =
                _summaryProgressEtaLastFraction ?? 0;
            _summaryProgressEtaSmoothedSeconds = null;
            _summaryProgressEtaStopwatch.Restart();
        }

        if (progress.Fraction is not >= 0 or > 1)
        {
            return null;
        }

        double fraction = progress.Fraction.Value;
        if (_summaryProgressEtaLastFraction is null
            || fraction + 0.01 < _summaryProgressEtaLastFraction.Value)
        {
            _summaryProgressEtaBaselineFraction = fraction;
            _summaryProgressEtaSmoothedSeconds = null;
            _summaryProgressEtaStopwatch.Restart();
        }

        _summaryProgressEtaLastFraction = fraction;
        double measuredFraction =
            fraction - _summaryProgressEtaBaselineFraction;
        if (fraction <= 0.02
            || fraction >= 0.995
            || measuredFraction <= 0.015
            || _summaryProgressEtaStopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            return null;
        }

        double rawSeconds =
            _summaryProgressEtaStopwatch.Elapsed.TotalSeconds
            * (1 - fraction)
            / measuredFraction;
        if (!double.IsFinite(rawSeconds)
            || rawSeconds < 2
            || rawSeconds > TimeSpan.FromHours(4).TotalSeconds)
        {
            return null;
        }

        _summaryProgressEtaSmoothedSeconds =
            _summaryProgressEtaSmoothedSeconds is double previous
                ? (previous * 0.7) + (rawSeconds * 0.3)
                : rawSeconds;
        return FormatEstimatedRemaining(TimeSpan.FromSeconds(
            _summaryProgressEtaSmoothedSeconds.Value));
    }

    private string FormatEstimatedRemaining(TimeSpan remaining)
    {
        string value = remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.TotalMinutes >= 1
                ? remaining.ToString(@"m\:ss")
                : $"{Math.Ceiling(remaining.TotalSeconds):0} s";
        return Translate(
            $"noch ca. {value}",
            $"about {value} remaining");
    }

    private void ResetMeetingIntelligenceEta(bool stop = false)
    {
        _summaryProgressEtaBaselineFraction = 0;
        _summaryProgressEtaLastFraction = 0;
        _summaryProgressEtaSmoothedSeconds = null;
        _summaryProgressEtaWasDownloading = false;
        if (stop)
        {
            _summaryProgressEtaStopwatch.Reset();
        }
        else
        {
            _summaryProgressEtaStopwatch.Restart();
        }
    }

    private async Task<bool> ConfirmLanguageModelDownloadAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "Lokales Sprachmodell herunterladen?",
                "Download local language model?"),
            Content = Translate(
                $"Für echte inhaltliche Zusammenfassungen benötigt MeetingRecall einmalig „{MeetingIntelligenceService.ModelDisplayName}“ (ca. {FormatFileSize(MeetingIntelligenceService.ModelDownloadSizeBytes)}). Das Modell läuft danach vollständig lokal; Audio und Transkript werden nicht hochgeladen.",
                $"Genuine semantic summaries require a one-time download of “{MeetingIntelligenceService.ModelDisplayName}” (about {FormatFileSize(MeetingIntelligenceService.ModelDownloadSizeBytes)}). It then runs entirely locally; audio and transcripts are never uploaded."),
            PrimaryButtonText = Translate("Herunterladen", "Download"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private string FormatLanguageModelDownloadStatus(
        MeetingIntelligenceProgress progress)
    {
        double percentage = Math.Clamp((progress.Fraction ?? 0) * 100, 0, 100);
        string speed = progress.BytesPerSecond is > 0
            ? $" · {progress.BytesPerSecond.Value / 1_000_000:0.0} MB/s"
              + $" · {progress.BytesPerSecond.Value * 8 / 1_000_000:0.0} Mbit/s"
            : string.Empty;
        string eta = string.Empty;
        if (progress.TotalBytes is > 0
            && progress.DownloadedBytes > 0
            && progress.BytesPerSecond is > 0)
        {
            double seconds =
                (progress.TotalBytes.Value - progress.DownloadedBytes)
                / progress.BytesPerSecond.Value;
            if (seconds is >= 2 and <= 86_400)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(seconds);
                string value = remaining.TotalHours >= 1
                    ? remaining.ToString(@"h\:mm\:ss")
                    : remaining.TotalMinutes >= 1
                        ? remaining.ToString(@"m\:ss")
                        : $"{Math.Ceiling(remaining.TotalSeconds):0} s";
                eta = Translate(
                    $" · noch ca. {value}",
                    $" · about {value} remaining");
            }
        }

        return Translate("Sprachmodell", "Language model")
            + $" · {percentage:0.0} %"
            + speed
            + eta;
    }

    private void SummaryWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        RootGrid.Loaded -= SummaryRootGrid_Loaded;
        ConversationsList.SelectionChanged -=
            ConversationsList_SummarySelectionChanged;

        if (_meetingSummaryPanel is not null)
        {
            _meetingSummaryPanel.GenerationRequested -=
                MeetingSummaryPanel_GenerationRequested;
            _meetingSummaryPanel.ThreeVersionGenerationRequested -=
                MeetingSummaryPanel_ThreeVersionGenerationRequested;
            _meetingSummaryPanel.OpenThreeVersionPickerRequested -=
                MeetingSummaryPanel_OpenThreeVersionPickerRequested;
            _meetingSummaryPanel.CancellationRequested -=
                MeetingSummaryPanel_CancellationRequested;
            _meetingSummaryPanel.ApplySuggestedTitleRequested -=
                MeetingSummaryPanel_ApplySuggestedTitleRequested;
            _meetingSummaryPanel.GenerateAnotherTitleRequested -=
                MeetingSummaryPanel_GenerateAnotherTitleRequested;
            _meetingSummaryPanel.CustomTitleRequested -=
                MeetingSummaryPanel_CustomTitleRequested;
        }
    }
}
