using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Settings;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Desktop.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private ILocalTranscriptionService? _localTranscriptionService;
    private ISpeakerRepository? _speakerRepository;
    private TranscriptPanel? _transcriptPanel;
    private bool _transcriptionUiInitialized;
    private readonly Stopwatch _progressEtaStopwatch = new();
    private TranscriptionStage? _progressEtaStage;

    public void InitializeTranscription()
    {
        if (_transcriptionUiInitialized)
        {
            return;
        }

        _transcriptionUiInitialized = true;
        _localTranscriptionService = App.Services.GetRequiredService<ILocalTranscriptionService>();
        _speakerRepository = App.Services.GetRequiredService<ISpeakerRepository>();
        RootGrid.Loaded += TranscriptionRootGrid_Loaded;
        ConversationsList.SelectionChanged += ConversationsList_TranscriptionSelectionChanged;
        Closed += TranscriptionWindow_Closed;
    }

    private ILocalTranscriptionService LocalTranscriptionService =>
        _localTranscriptionService
        ?? throw new InvalidOperationException("Die Transkriptionsoberfläche wurde nicht initialisiert.");

    private ISpeakerRepository SpeakerRepository =>
        _speakerRepository
        ?? throw new InvalidOperationException("Die Sprecherverwaltung wurde nicht initialisiert.");

    private void TranscriptionRootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        InstallTranscriptPanel();
        RenderSelectedTranscript();
    }

    private void InstallTranscriptPanel()
    {
        if (_transcriptPanel is not null)
        {
            return;
        }

        Border? transcriptCard = ConversationDetailContent.Children
            .OfType<Border>()
            .LastOrDefault(ContainsTranscriptHeading);
        if (transcriptCard is null)
        {
            return;
        }

        _transcriptPanel = new TranscriptPanel();
        _transcriptPanel.TranscriptionRequested += TranscriptPanel_TranscriptionRequested;
        _transcriptPanel.CompleteProcessingRequested +=
            TranscriptPanel_CompleteProcessingRequested;
        _transcriptPanel.CancellationRequested += TranscriptPanel_CancellationRequested;
        _transcriptPanel.ExportRequested += TranscriptPanel_ExportRequested;
        _transcriptPanel.TimestampRequested += TranscriptPanel_TimestampRequested;
        _transcriptPanel.HopToTextRequested += TranscriptPanel_HopToTextRequested;
        transcriptCard.Child = _transcriptPanel;
    }

    private static bool ContainsTranscriptHeading(Border border)
    {
        return FindTextBlock(border, "Transkript") is not null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock textBlock
                && string.Equals(textBlock.Text, text, StringComparison.Ordinal))
            {
                return textBlock;
            }

            TextBlock? nested = FindTextBlock(child, text);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ConversationsList_TranscriptionSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RenderSelectedTranscript();
    }

    private async void RenderSelectedTranscript()
    {
        ConversationListItem? selectedItem =
            ConversationsList.SelectedItem as ConversationListItem;
        Conversation? conversation = selectedItem?.Conversation;
        Guid? selectedId = conversation?.Id;

        IReadOnlyList<Speaker> speakers = conversation is null
            ? []
            : await SpeakerRepository.ListAsync();

        if ((ConversationsList.SelectedItem as ConversationListItem)?.Conversation.Id
            != selectedId)
        {
            return;
        }

        _transcriptPanel?.ShowConversation(
            conversation,
            speakers.ToDictionary(speaker => speaker.Id));

        if (conversation is not null
            && TryGetProcessingJobState(
                conversation.Id,
                out ProcessingJobViewState queueState,
                ProcessingJobKind.Transcription))
        {
            _transcriptPanel?.ShowQueued(
                queueState.Position,
                queueState.IsActive);
        }
    }

    private async void TranscriptPanel_TranscriptionRequested(
        object? sender,
        EventArgs e)
    {
        await TranscribeConversationAsync();
    }

    private async void TranscriptPanel_CompleteProcessingRequested(
        object? sender,
        EventArgs e)
    {
        if (ConversationsList.SelectedItem is not ConversationListItem selectedItem)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        bool transcriptCreated = await TranscribeConversationAsync(conversation);
        if (!transcriptCreated)
        {
            return;
        }

        await QueueSummaryGenerationAsync(conversation.Id);
    }

    private async Task<bool> TranscribeConversationAsync()
    {
        if (_isBusy
            || ConversationsList.SelectedItem is not ConversationListItem selectedItem)
        {
            return false;
        }

        return await TranscribeConversationAsync(selectedItem.Conversation);
    }

    private async Task<bool> TranscribeConversationAsync(
        Conversation conversation)
    {
        if (conversation.TranscriptSegments.Count > 0
            && !await ConfirmRetranscriptionAsync())
        {
            return false;
        }

        if ((!LocalTranscriptionService.IsModelInstalled
             || !LocalTranscriptionService.IsSpeakerDiarizationModelInstalled)
            && !await ConfirmModelDownloadAsync())
        {
            return false;
        }

        ProcessingJobHandle handle = EnqueueProcessingJob(
            ProcessingJobKind.Transcription,
            conversation.Id,
            conversation.Title,
            Translate("Transkription", "Transcription"),
            cancellationToken => RunTranscriptionJobAsync(
                conversation.Id,
                cancellationToken));

        return await handle.Completion == ProcessingJobResult.Completed;
    }

    private async Task<ProcessingJobResult> RunTranscriptionJobAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        ResetProgressEta();
        var progress = new Progress<TranscriptionProgress>(item =>
            UpdateTranscriptionProgress(conversationId, item));

        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversationId)
        {
            _transcriptPanel?.ShowQueued(0, true);
        }

        try
        {
            Conversation updatedConversation =
                await LocalTranscriptionService.TranscribeAsync(
                    conversationId,
                    progress,
                    cancellationToken);
            await RefreshConversationsPreservingSelectionAsync(
                updatedConversation.Id);
            return ProcessingJobResult.Completed;
        }
        catch (OperationCanceledException)
        {
            await RefreshConversationsPreservingSelectionAsync(
                conversationId);
            return ProcessingJobResult.Canceled;
        }
        catch (Exception exception)
        {
            await RefreshConversationsPreservingSelectionAsync(
                conversationId);
            await ShowMessageAsync(
                "Transkription fehlgeschlagen",
                BuildTranscriptionErrorMessage(exception),
                "Schließen");
            return ProcessingJobResult.Failed;
        }
        finally
        {
            ResetProgressEta();
            ClearProcessingStatus();
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversationId)
            {
                RenderSelectedTranscript();
            }
        }
    }

    private void TranscriptPanel_CancellationRequested(object? sender, EventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationListItem selectedItem)
        {
            CancelNextProcessingJob(
                selectedItem.Conversation.Id,
                ProcessingJobKind.Transcription);
        }
    }

    private async void TranscriptPanel_ExportRequested(object? sender, EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem is not ConversationListItem selectedItem
            || selectedItem.Conversation.TranscriptSegments.Count == 0)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        IReadOnlyList<Speaker> speakers = await SpeakerRepository.ListAsync();
        if ((ConversationsList.SelectedItem as ConversationListItem)?.Conversation.Id
            != conversation.Id)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = BuildTranscriptExportFileName(conversation.Title)
        };
        picker.FileTypeChoices.Add(
            "Textdatei",
            new List<string> { ".txt" });
        picker.FileTypeChoices.Add(
            "Markdown (Sprechernamen fett)",
            new List<string> { ".md" });
        InitializeWithWindow.Initialize(
            picker,
            WindowNative.GetWindowHandle(this));

        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        try
        {
            IReadOnlyDictionary<Guid, Speaker> speakerLookup =
                speakers.ToDictionary(speaker => speaker.Id);
            string content = string.Equals(
                destination.FileType,
                ".md",
                StringComparison.OrdinalIgnoreCase)
                ? TranscriptExportFormatter.FormatMarkdown(
                    conversation,
                    speakerLookup)
                : TranscriptExportFormatter.FormatPlainText(
                    conversation,
                    speakerLookup);

            await FileIO.WriteTextAsync(destination, content);
            StatusText.Text = $"Transkript exportiert: {destination.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                "Transkript konnte nicht exportiert werden",
                exception.Message,
                "Schließen");
        }
    }

    private static string BuildTranscriptExportFileName(string title)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string safeTitle = new(
            title.Select(character =>
                invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());
        safeTitle = safeTitle.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safeTitle)
            ? "Transkript"
            : $"{safeTitle}-Transkript";
    }

    private void TranscriptPanel_HopToTextRequested(object? sender, EventArgs e)
    {
        _transcriptPanel?.HopTo(_mediaPlayer.PlaybackSession.Position);
    }

    private void TranscriptPanel_TimestampRequested(TimeSpan position)
    {
        TimeSpan duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _mediaPlayer.PlaybackSession.Position = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > duration
                ? duration
                : position;
        UpdatePlaybackUi();
    }

    private void UpdateTranscriptionProgress(
        Guid conversationId,
        TranscriptionProgress progress)
    {
        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversationId)
        {
            _transcriptPanel?.ShowProgress(progress);
        }

        string status = progress.Stage switch
        {
            TranscriptionStage.DownloadingModel => FormatModelDownloadStatus(progress),
            TranscriptionStage.PreparingAudio => "Audio wird vorbereitet …",
            TranscriptionStage.Transcribing when progress.Fraction is >= 0 and <= 1 =>
                $"Transkription · {progress.Fraction.Value * 100:0} %"
                + (progress.SegmentCount > 0
                    ? $" · {progress.SegmentCount} Segmente"
                    : string.Empty)
                + FormatProgressEta(progress),
            TranscriptionStage.Transcribing => "Transkription läuft …",
            TranscriptionStage.AnalyzingSpeakers when progress.SuppressedSegmentCount > 0 =>
                $"{progress.SuppressedSegmentCount} erkannte Wiederholungssegmente verworfen · Sprecher werden analysiert …",
            TranscriptionStage.AnalyzingSpeakers => "Sprecher werden lokal analysiert …",
            TranscriptionStage.Saving => "Transkript wird gespeichert …",
            TranscriptionStage.Completed => string.Empty,
            _ => "Transkription läuft …"
        };
        SetProcessingStatus(status);
    }

    private string FormatModelDownloadStatus(TranscriptionProgress progress)
    {
        double percentage = Math.Clamp((progress.Fraction ?? 0) * 100, 0, 100);
        if (progress.BytesPerSecond is not > 0)
        {
            return $"Whisper-Modell · {percentage:0.0} % · Geschwindigkeit wird gemessen …" + FormatProgressEta(progress);
        }

        double megabytesPerSecond = progress.BytesPerSecond.Value / 1_000_000;
        double megabitsPerSecond = progress.BytesPerSecond.Value * 8 / 1_000_000;
        return $"Whisper-Modell · {percentage:0.0} % · {megabytesPerSecond:0.0} MB/s · {megabitsPerSecond:0.0} Mbit/s" + FormatProgressEta(progress);
    }

    private string FormatProgressEta(TranscriptionProgress progress)
    {
        if (_progressEtaStage != progress.Stage)
        {
            _progressEtaStage = progress.Stage;
            _progressEtaStopwatch.Restart();
        }

        double? fraction = progress.Fraction;
        if (fraction is null
            || fraction.Value <= 0.02
            || fraction.Value >= 0.995
            || _progressEtaStopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            return string.Empty;
        }

        double remainingSeconds =
            _progressEtaStopwatch.Elapsed.TotalSeconds * (1 - fraction.Value) / fraction.Value;
        if (double.IsNaN(remainingSeconds)
            || double.IsInfinity(remainingSeconds)
            || remainingSeconds < 2
            || remainingSeconds > TimeSpan.FromHours(4).TotalSeconds)
        {
            return string.Empty;
        }

        TimeSpan remaining = TimeSpan.FromSeconds(remainingSeconds);
        string value = remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.TotalMinutes >= 1
                ? remaining.ToString(@"m\:ss")
                : $"{Math.Ceiling(remaining.TotalSeconds):0} s";
        return $" · noch ca. {value}";
    }

    private void ResetProgressEta()
    {
        _progressEtaStopwatch.Reset();
        _progressEtaStage = null;
    }

    private async Task<bool> ConfirmModelDownloadAsync()
    {
        var germanModels = new List<string>();
        var englishModels = new List<string>();
        long downloadSize = 0;
        if (!LocalTranscriptionService.IsModelInstalled)
        {
            downloadSize += LocalTranscriptionService.ModelDownloadSizeBytes;
            germanModels.Add(
                $"• {LocalTranscriptionService.ModelDisplayName} · "
                + FormatFileSize(LocalTranscriptionService.ModelDownloadSizeBytes));
            englishModels.Add(
                $"• {LocalTranscriptionService.ModelDisplayName} · "
                + FormatFileSize(LocalTranscriptionService.ModelDownloadSizeBytes));
        }

        if (!LocalTranscriptionService.IsSpeakerDiarizationModelInstalled)
        {
            downloadSize +=
                LocalTranscriptionService.SpeakerDiarizationDownloadSizeBytes;
            germanModels.Add(
                "• Neuronale Sprechererkennung · "
                + FormatFileSize(
                    LocalTranscriptionService.SpeakerDiarizationDownloadSizeBytes));
            englishModels.Add(
                "• Neural speaker recognition · "
                + FormatFileSize(
                    LocalTranscriptionService.SpeakerDiarizationDownloadSizeBytes));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "Lokale Modelle herunterladen?",
                "Download local models?"),
            Content = Translate(
                "Für die gewählte Transkription fehlen noch:\n\n"
                + string.Join("\n", germanModels)
                + $"\n\nEinmaliger Download: ca. {FormatFileSize(downloadSize)}. "
                + "Alle Modelle bleiben lokal; Audio und Transkript verlassen deinen Rechner nicht.",
                "The selected transcription still requires:\n\n"
                + string.Join("\n", englishModels)
                + $"\n\nOne-time download: about {FormatFileSize(downloadSize)}. "
                + "All models remain local; audio and transcripts never leave your computer."),
            PrimaryButtonText = Translate("Herunterladen", "Download"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmRetranscriptionAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Transkript neu erstellen?",
            Content = "Das vorhandene automatisch erzeugte Transkript wird ersetzt. Die Audiodatei bleibt unverändert.",
            PrimaryButtonText = "Neu transkribieren",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string BuildTranscriptionErrorMessage(Exception exception)
    {
        string message = exception.Message;
        if (exception is DllNotFoundException
            || message.Contains("native", StringComparison.OrdinalIgnoreCase)
            || message.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}\n\nPrüfe, ob Windows 11 aktuell ist und die Microsoft Visual C++-Laufzeit für Visual Studio 2022 installiert ist.";
        }

        if (message.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("codec", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}\n\nDas Audioformat konnte nicht in das von Whisper benötigte 16-kHz-WAV-Format umgewandelt werden.";
        }

        return message;
    }

    private void TranscriptionWindow_Closed(object sender, WindowEventArgs args)
    {
        RootGrid.Loaded -= TranscriptionRootGrid_Loaded;
        ConversationsList.SelectionChanged -= ConversationsList_TranscriptionSelectionChanged;

        if (_transcriptPanel is not null)
        {
            _transcriptPanel.TranscriptionRequested -= TranscriptPanel_TranscriptionRequested;
            _transcriptPanel.CompleteProcessingRequested -=
                TranscriptPanel_CompleteProcessingRequested;
            _transcriptPanel.CancellationRequested -= TranscriptPanel_CancellationRequested;
            _transcriptPanel.ExportRequested -= TranscriptPanel_ExportRequested;
            _transcriptPanel.TimestampRequested -= TranscriptPanel_TimestampRequested;
            _transcriptPanel.HopToTextRequested -= TranscriptPanel_HopToTextRequested;
        }
    }
}
