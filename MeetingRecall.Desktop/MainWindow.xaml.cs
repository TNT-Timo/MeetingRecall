using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Search;
using MeetingRecall.Desktop.Services;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecallDataPaths = MeetingRecall.Infrastructure.Storage.AppDataPaths;
using Windows.Graphics;
using MeetingRecallMediaSource = Windows.Media.Core.MediaSource;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<ConversationListItem> _conversationItems = [];
    private readonly List<ConversationListItem> _allConversationItems = [];
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _playbackTimer = new();
    private readonly IConversationRepository _conversationRepository;
    private readonly IAudioArchiveRepository _audioArchiveRepository;
    private readonly ConversationDeletionService _conversationDeletionService;
    private readonly AudioImportService _audioImportService;
    private readonly MeetingRecallDataPaths _paths;
    private readonly Dictionary<Guid, Guid> _lastSpeakerPreviewSegments = [];
    private TimeSpan? _speakerPreviewEnd;
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _isBusy;
    private bool _isSeeking;
    private bool _isRefreshingConversations;
    private ConversationSortMode _conversationSortMode =
        ConversationSortMode.UpdatedDescending;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = GetVersionLabel();

        _conversationRepository = App.Services.GetRequiredService<IConversationRepository>();
        _audioArchiveRepository = App.Services.GetRequiredService<IAudioArchiveRepository>();
        _conversationDeletionService = App.Services.GetRequiredService<ConversationDeletionService>();
        _audioImportService = App.Services.GetRequiredService<AudioImportService>();
        _paths = App.Services.GetRequiredService<MeetingRecallDataPaths>();

        ConversationsList.ItemsSource = _conversationItems;
        _mediaPlayer.Volume = 1;
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(200);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
        RootGrid.PreviewKeyDown += PlaybackRootGrid_KeyDown;
        Closed += MainWindow_Closed;

        Title = "MeetingRecall";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeWindow(1280, 820);
    }

    private static string GetVersionLabel()
    {
        string informationalVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
        string[] parts = informationalVersion.Split('+', 2);
        string version = parts[0];
        string revision = parts.Length == 2 ? parts[1] : string.Empty;
        return string.IsNullOrWhiteSpace(revision)
            ? $"Version {version}"
            : $"Version {version} · {revision}";
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConversationsAsync();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List
        };

        foreach (string extension in AudioImportService.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? sourceFile = await picker.PickSingleFileAsync();
        if (sourceFile is null)
        {
            return;
        }

        if (AudioImportService.IsVideoFile(sourceFile) && !await ConfirmVideoImportAsync(sourceFile.Name))
        {
            return;
        }

        _importCancellation = new CancellationTokenSource();
        SetBusy(
            true,
            "Datei wird lokal geprüft …",
            _importCancellation);
        try
        {
            AudioImportResult result = await _audioImportService.ImportAsync(
                sourceFile,
                _importCancellation.Token);
            await LoadConversationsAsync(result.Conversation.Id);
            StatusText.Text = result.WasAlreadyImported
                ? "Bereits importiert – vorhandenes Gespräch geöffnet"
                : "Import abgeschlossen";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Import wurde abgebrochen";
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                "Import fehlgeschlagen",
                exception.Message,
                "Schließen");
        }
        finally
        {
            _importCancellation.Dispose();
            _importCancellation = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private async void RecordingButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(
            "Native Aufnahme",
            "Die gemeinsame Aufzeichnung von Mikrofon und Desktopaudio wird als nächster Audio-Schritt aktiviert. Bereits vorhandene Dateien kannst du jetzt vollständig lokal importieren und abspielen.",
            "Verstanden");
    }

    private async void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingConversations)
        {
            return;
        }

        if (ConversationsList.SelectedItem is not ConversationListItem selectedItem)
        {
            // Refreshing the observable collection briefly clears the ListView
            // selection. Only show the start screen when there are truly no
            // conversations; otherwise the replacement item is selected below.
            if (_conversationItems.Count == 0)
            {
                ShowEmptyState();
            }

            return;
        }

        await ShowConversationAsync(selectedItem.Conversation);
    }

    private async void RenameConversationMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem
            {
                Tag: ConversationListItem selectedItem
            })
        {
            return;
        }

        ConversationsList.SelectedItem = selectedItem;
        await RenameConversationAsync(selectedItem);
    }

    private async Task RenameConversationAsync(
        ConversationListItem selectedItem)
    {
        if (_isBusy)
        {
            return;
        }

        var titleBox = new TextBox
        {
            Text = selectedItem.Conversation.Title,
            MaxLength = 240,
            PlaceholderText = "Gesprächstitel"
        };
        titleBox.Loaded += (_, _) =>
        {
            titleBox.Focus(FocusState.Programmatic);
            titleBox.SelectAll();
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Gespräch umbenennen",
            Content = titleBox,
            PrimaryButtonText = "Speichern",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(titleBox.Text);
        titleBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(titleBox.Text);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string previousTitle = selectedItem.Conversation.Title;
        bool renamed = false;
        try
        {
            renamed = selectedItem.Conversation.Rename(titleBox.Text);
            if (!renamed)
            {
                return;
            }

            SetBusy(true, "Titel wird gespeichert …");
            await _conversationRepository.UpsertAsync(
                selectedItem.Conversation);
            await LoadConversationsAsync(selectedItem.Conversation.Id);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            if (renamed)
            {
                selectedItem.Conversation.Rename(previousTitle);
            }

            await ShowMessageAsync(
                "Titel konnte nicht geändert werden",
                exception.Message,
                "Schließen");
        }
        finally
        {
            if (renamed)
            {
                SetBusy(false, string.Empty);
            }
        }
    }

    private async void DeleteConversationMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem
            {
                Tag: ConversationListItem selectedItem
            })
        {
            return;
        }

        await DeleteConversationAsync(selectedItem);
    }

    private async Task DeleteConversationAsync(
        ConversationListItem selectedItem)
    {
        if (_isBusy)
        {
            return;
        }

        if (HasProcessingJobForConversation(selectedItem.Conversation.Id))
        {
            await ShowMessageAsync(
                Translate(
                    "Gespräch wird noch verarbeitet",
                    "Conversation is still processing"),
                Translate(
                    "Brich zuerst die laufenden oder wartenden Verarbeitungen dieses Gesprächs über die Warteschlange ab.",
                    "Cancel the active or queued processing jobs for this conversation first."),
                Translate("Schließen", "Close"));
            return;
        }

        if (!await ConfirmConversationDeletionAsync(
                selectedItem.Conversation.Title))
        {
            return;
        }

        Guid deletedId = selectedItem.Conversation.Id;
        Guid? selectedId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        SetBusy(true, "Gespräch wird gelöscht …");

        try
        {
            if (selectedId == deletedId)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                ResetPlaybackUi();
                await Task.Yield();
            }

            if (!await _conversationDeletionService.DeleteAsync(deletedId))
            {
                throw new InvalidOperationException(
                    "Das Gespräch wurde nicht mehr im lokalen Datenbestand gefunden. Verwaiste Gesprächsdateien wurden dennoch entfernt.");
            }

            _conversationQuestionSessions.Remove(deletedId);
            _conversationQuestionLoadFailures.Remove(deletedId);
            await LoadConversationsAsync(
                selectedId == deletedId ? null : selectedId);
            StatusText.Text = Translate(
                "Gespräch endgültig gelöscht",
                "Conversation permanently deleted");
        }
        catch (Exception exception)
        {
            await LoadConversationsAsync(selectedId);
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                Translate(
                    "Gespräch konnte nicht vollständig gelöscht werden",
                    "Conversation could not be deleted completely"),
                exception.Message,
                Translate("Schließen", "Close"));
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task<bool> ConfirmConversationDeletionAsync(string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "Gespräch endgültig löschen?",
                "Permanently delete conversation?"),
            Content = Translate(
                $"„{title}“ wird einschließlich aller Audiodateien, Transkripte, Zusammenfassungen und nicht mehr verwendeter erkannter Personen dauerhaft entfernt. Diese Aktion kann nicht rückgängig gemacht werden.",
                $"“{title}” and all of its audio files, transcripts, summaries, and no-longer-used detected people will be permanently removed. This action cannot be undone."),
            PrimaryButtonText = Translate("Endgültig löschen", "Delete permanently"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folderPath = ResolvePhysicalDataRoot();
            Directory.CreateDirectory(folderPath);

            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });

            if (process is null)
            {
                throw new InvalidOperationException("Windows Explorer konnte nicht gestartet werden.");
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Datenordner konnte nicht geöffnet werden",
                $"{exception.Message}\n\nPfad: {ResolvePhysicalDataRoot()}",
                "Schließen");
        }
    }

    private void PlaybackRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox
            || comboBox.SelectedItem is not ComboBoxItem selectedItem
            || selectedItem.Tag is not string rateText
            || !double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
        {
            return;
        }

        _mediaPlayer.PlaybackSession.PlaybackRate = rate;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
    }

    private void CancelOperationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_activeOperationCancellation is null
            || _activeOperationCancellation.IsCancellationRequested)
        {
            return;
        }

        _activeOperationCancellation.Cancel();
        CancelOperationButton.IsEnabled = false;
        StatusText.Text = "Wird abgebrochen …";
    }

    private async void PlaybackRootGrid_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        object? focusedElement =
            FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        bool isTextInput = focusedElement is
            TextBox or RichEditBox or PasswordBox or AutoSuggestBox
            or NumberBox or ComboBox;
        bool isEditingControl = isTextInput || focusedElement is Slider;

        if (e.Key == VirtualKey.F2)
        {
            if (!isTextInput
                && ConversationsList.SelectedItem
                    is ConversationListItem selectedItem)
            {
                e.Handled = true;
                await RenameConversationAsync(selectedItem);
            }

            return;
        }

        if (isEditingControl)
        {
            return;
        }

        if (e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            TogglePlayback();
            return;
        }

        if (e.Key == VirtualKey.Left)
        {
            e.Handled = true;
            SeekBy(TimeSpan.FromSeconds(
                -CurrentApplicationSettings.SkipIntervalSeconds));
            return;
        }

        if (e.Key == VirtualKey.Right)
        {
            e.Handled = true;
            SeekBy(TimeSpan.FromSeconds(
                CurrentApplicationSettings.SkipIntervalSeconds));
        }
    }

    private bool TogglePlayback()
    {
        if (_mediaPlayer.Source is null)
        {
            return false;
        }

        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }

        UpdatePlaybackUi();
        return true;
    }

    private void SkipBackwardButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(
            -CurrentApplicationSettings.SkipIntervalSeconds));
    }

    private void SkipForwardButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(
            CurrentApplicationSettings.SkipIntervalSeconds));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.IsMuted = !_mediaPlayer.IsMuted;
        UpdateVolumeIcon();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaPlayer.Volume = Math.Clamp(e.NewValue, 0, 1);
        if (_mediaPlayer.Volume > 0 && _mediaPlayer.IsMuted)
        {
            _mediaPlayer.IsMuted = false;
        }

        UpdateVolumeIcon();
    }

    private void TimelineTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mediaPlayer.PlaybackSession.NaturalDuration <= TimeSpan.Zero)
        {
            return;
        }

        _isSeeking = true;
        TimelineTrack.CapturePointer(e.Pointer);
        SeekToPointer(e);
        e.Handled = true;
    }

    private void TimelineTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking || !e.GetCurrentPoint(TimelineTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        SeekToPointer(e);
        e.Handled = true;
    }

    private void TimelineTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        SeekToPointer(e);
        _isSeeking = false;
        TimelineTrack.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void TimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlaybackUi();
    }

    private void ConversationPane_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (ConversationDetail.Visibility != Visibility.Visible)
        {
            return;
        }

        int delta = e.GetCurrentPoint(ConversationPane)
            .Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        double targetOffset = Math.Clamp(
            ConversationDetail.VerticalOffset - delta,
            0,
            ConversationDetail.ScrollableHeight);
        if (Math.Abs(targetOffset - ConversationDetail.VerticalOffset) < 0.5)
        {
            return;
        }

        ConversationDetail.ChangeView(
            horizontalOffset: null,
            verticalOffset: targetOffset,
            zoomFactor: null,
            disableAnimation: false);
        e.Handled = true;
    }

    private void PlaybackTimer_Tick(object? sender, object e)
    {
        if (_speakerPreviewEnd is TimeSpan previewEnd
            && _mediaPlayer.PlaybackSession.Position >= previewEnd)
        {
            _mediaPlayer.Pause();
            _speakerPreviewEnd = null;
        }

        if (!_isSeeking)
        {
            UpdatePlaybackUi();
            _transcriptPanel?.UpdatePlaybackPosition(
                _mediaPlayer.PlaybackSession.Position,
                CurrentApplicationSettings.LiveTranscriptEnabled);
        }
    }

    private async void ConversationSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_isRefreshingConversations)
        {
            return;
        }

        Guid? selectedId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        await ApplyConversationSearchAsync(selectedId);
    }

    private async void SearchModeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRefreshingConversations)
        {
            return;
        }

        Guid? selectedId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        await ApplyConversationSearchAsync(selectedId);
    }

    private async void ConversationSortMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string sortTag }
            || !Enum.TryParse(sortTag, out ConversationSortMode sortMode))
        {
            return;
        }

        _conversationSortMode = sortMode;
        ToolTipService.SetToolTip(
            ConversationSortButton,
            $"Gespräche sortieren: {GetConversationSortLabel(sortMode)}");

        Guid? selectedId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        await ApplyConversationSearchAsync(selectedId);
    }

    private async Task LoadConversationsAsync(Guid? selectConversationId = null)
    {
        _allConversationItems.Clear();
        IReadOnlyList<Conversation> conversations =
            await _conversationRepository.ListAsync();
        foreach (Conversation conversation in conversations)
        {
            var item = new ConversationListItem(
                conversation,
                conversation.Title,
                $"{conversation.RecordedAt:dd.MM.yyyy HH:mm} · "
                + FormatDuration(conversation.Duration),
                conversation.UpdatedAtUtc);
            _allConversationItems.Add(item with
            {
                Metadata = FormatListMetadata(item)
            });
        }

        await ApplyConversationSearchAsync(
            selectConversationId,
            refreshSelectedConversation: true);
    }

    private async Task ApplyConversationSearchAsync(
        Guid? selectConversationId = null,
        bool refreshSelectedConversation = false)
    {
        string query = ConversationSearchBox.Text.Trim();
        bool exactPhrase = ExactSearchModeButton.IsChecked == true;
        Guid? previousSelectedId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        IEnumerable<ConversationListItem> matchingItems = _allConversationItems
            .Where(item => ConversationSearchMatcher.IsMatch(
                item.Conversation,
                query,
                exactPhrase));
        List<ConversationListItem> filtered = SortConversationItems(
                matchingItems,
                _conversationSortMode)
            .ToList();

        _isRefreshingConversations = true;
        try
        {
            _conversationItems.Clear();
            foreach (ConversationListItem item in filtered)
            {
                _conversationItems.Add(item);
            }

            int count = _conversationItems.Count;
            int totalCount = _allConversationItems.Count;
            bool isFiltered = !string.IsNullOrWhiteSpace(query);
            ArchiveCountText.Text = isFiltered
                ? $"{count}/{totalCount}"
                : totalCount.ToString(CultureInfo.CurrentCulture);
            ConversationCountText.Text = isFiltered
                ? count == 1
                    ? $"1 von {totalCount} Gesprächen"
                    : $"{count} von {totalCount} Gesprächen"
                : count == 1
                    ? "1 Gespräch"
                    : $"{count} Gespräche";

            if (count == 0)
            {
                ConversationsList.SelectedItem = null;
                ShowEmptyState(
                    isFiltered
                        ? "Keine passenden Ergebnisse"
                        : "Deine lokalen Gespräche",
                    isFiltered
                        ? "Passe den Suchbegriff oder den Suchmodus an."
                        : "Importiere eine Audio- oder Videodatei. Videos werden nicht gespeichert; MeetingRecall extrahiert ausschließlich eine MP3-Audiospur.");
                return;
            }

            ConversationListItem itemToSelect = selectConversationId is null
                ? _conversationItems[0]
                : _conversationItems.FirstOrDefault(
                    item => item.Conversation.Id == selectConversationId.Value)
                    ?? _conversationItems[0];

            ConversationsList.SelectedItem = itemToSelect;
            bool canKeepCurrentDetail =
                !refreshSelectedConversation
                && previousSelectedId == itemToSelect.Conversation.Id
                && ConversationDetail.Visibility == Visibility.Visible;
            if (!canKeepCurrentDetail)
            {
                await ShowConversationAsync(itemToSelect.Conversation);
            }
        }
        finally
        {
            _isRefreshingConversations = false;
        }
    }

    private async Task ShowConversationAsync(Conversation conversation)
    {
        AudioTrack? track = await _audioArchiveRepository.GetPrimaryAsync(conversation.Id);
        if (ConversationsList.SelectedItem is not ConversationListItem selected
            || selected.Conversation.Id != conversation.Id)
        {
            return;
        }

        _mediaPlayer.Pause();
        _speakerPreviewEnd = null;
        _mediaPlayer.Source = null;
        ResetPlaybackUi();

        EmptyState.Visibility = Visibility.Collapsed;
        ConversationDetail.Visibility = Visibility.Visible;
        ConversationTitleText.Text = conversation.Title;
        DownloadAudioButton.IsEnabled = false;
        ConversationMetadataText.Text =
            $"{conversation.RecordedAt:dddd, dd. MMMM yyyy · HH:mm} · "
            + $"{FormatDuration(conversation.Duration)} · "
            + FormatSource(conversation.Source);

        if (track is null)
        {
            AudioFileNameText.Text = "Keine Audiodatei gefunden";
            return;
        }

        string absolutePath = _paths.ToAbsolutePath(track.RelativePath);
        DownloadAudioButton.IsEnabled = File.Exists(absolutePath);
        AudioFileNameText.Text = conversation.Source == ConversationSource.NativeRecording
            ? FormatFileSize(track.FileSizeBytes)
            : $"{track.OriginalFileName} · {FormatFileSize(track.FileSizeBytes)}";
        TotalTimeText.Text = FormatPlaybackTime(track.Duration);
        _mediaPlayer.Source = MeetingRecallMediaSource.CreateFromUri(new Uri(absolutePath, UriKind.Absolute));
    }

    private async void DownloadAudioButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        AudioTrack? track = await _audioArchiveRepository
            .GetPrimaryAsync(conversation.Id);
        if (track is null
            || (ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id != conversation.Id)
        {
            return;
        }

        string sourcePath = _paths.ToAbsolutePath(track.RelativePath);
        if (!File.Exists(sourcePath))
        {
            DownloadAudioButton.IsEnabled = false;
            await ShowMessageAsync(
                "Audiodatei nicht gefunden",
                "Die lokale Original-Audiodatei ist nicht mehr vorhanden.",
                "Schließen");
            return;
        }

        string extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mp3";
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = BuildAudioExportFileName(conversation.Title)
        };
        picker.FileTypeChoices.Add(
            "Audiodatei",
            new List<string> { extension.ToLowerInvariant() });
        InitializeWithWindow.Initialize(
            picker,
            WindowNative.GetWindowHandle(this));

        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        string completionStatus = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(destination.Path)
                && string.Equals(
                    Path.GetFullPath(destination.Path),
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Wähle einen anderen Speicherort als den internen MeetingRecall-Datenordner.");
            }

            SetBusy(true, "Audiodatei wird gespeichert …");
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using Stream output = await destination.OpenStreamForWriteAsync();
            output.SetLength(0);
            await source.CopyToAsync(output, 1024 * 1024);
            await output.FlushAsync();
            completionStatus = $"Audiodatei gespeichert: {destination.Name}";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Audiodatei konnte nicht gespeichert werden",
                exception.Message,
                "Schließen");
        }
        finally
        {
            SetBusy(false, completionStatus);
        }
    }

    private static string BuildAudioExportFileName(string title)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string safeTitle = new(
            title.Select(character =>
                invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());
        safeTitle = safeTitle.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safeTitle)
            ? "Gespräch"
            : safeTitle;
    }

    private async Task PlayRandomSpeakerPassageAsync(Guid speakerId)
    {
        IReadOnlyList<Conversation> conversations = await _conversationRepository.ListAsync();
        List<(Conversation Conversation, TranscriptSegment Segment)> candidates = conversations
            .SelectMany(conversation => conversation.TranscriptSegments
                .Where(segment => segment.SpeakerId == speakerId
                    && segment.EndTime > segment.StartTime)
                .Select(segment => (conversation, segment)))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Für diese Person wurde noch keine abspielbare Sprachpassage gefunden.");
        }

        if (_lastSpeakerPreviewSegments.TryGetValue(speakerId, out Guid previousSegmentId)
            && candidates.Count > 1)
        {
            candidates.RemoveAll(candidate => candidate.Segment.Id == previousSegmentId);
        }

        foreach ((Conversation conversation, TranscriptSegment segment) in
                 candidates.OrderBy(_ => Random.Shared.Next()))
        {
            AudioTrack? track = await _audioArchiveRepository.GetPrimaryAsync(conversation.Id);
            if (track is null)
            {
                continue;
            }

            await PlaySpeakerPassageAsync(track, segment);
            _lastSpeakerPreviewSegments[speakerId] = segment.Id;
            return;
        }

        throw new InvalidOperationException(
            "Die Audiodatei für die gefundenen Sprachpassagen ist nicht mehr verfügbar.");
    }

    private async Task PlaySpeakerPassageAsync(
        AudioTrack track,
        TranscriptSegment segment)
    {
        var opened = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void MediaOpened(MediaPlayer sender, object args) => opened.TrySetResult(true);
        void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            opened.TrySetException(new InvalidOperationException(
                $"Die Sprachpassage konnte nicht abgespielt werden: {args.ErrorMessage}"));

        _mediaPlayer.Pause();
        _speakerPreviewEnd = null;
        _mediaPlayer.MediaOpened += MediaOpened;
        _mediaPlayer.MediaFailed += MediaFailed;

        try
        {
            string absolutePath = _paths.ToAbsolutePath(track.RelativePath);
            _mediaPlayer.Source = MeetingRecallMediaSource.CreateFromUri(
                new Uri(absolutePath, UriKind.Absolute));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

            TimeSpan duration = _mediaPlayer.PlaybackSession.NaturalDuration;
            TimeSpan start = segment.StartTime < TimeSpan.Zero
                ? TimeSpan.Zero
                : segment.StartTime;
            TimeSpan end = segment.EndTime;

            if (duration > TimeSpan.Zero)
            {
                start = start > duration ? duration : start;
                end = end > duration ? duration : end;
            }

            if (end <= start)
            {
                end = duration > TimeSpan.Zero
                    ? TimeSpan.FromTicks(Math.Min(
                        duration.Ticks,
                        start.Ticks + TimeSpan.FromSeconds(5).Ticks))
                    : start + TimeSpan.FromSeconds(5);
            }

            _mediaPlayer.PlaybackSession.Position = start;
            _speakerPreviewEnd = end;
            _mediaPlayer.Play();
            UpdatePlaybackUi();
        }
        finally
        {
            _mediaPlayer.MediaOpened -= MediaOpened;
            _mediaPlayer.MediaFailed -= MediaFailed;
        }
    }

    private void StopSpeakerPreview()
    {
        if (_speakerPreviewEnd is null)
        {
            return;
        }

        _mediaPlayer.Pause();
        _speakerPreviewEnd = null;
        UpdatePlaybackUi();
    }

    private void ShowEmptyState(
        string title = "Deine lokalen Gespräche",
        string description =
            "Importiere eine Audio- oder Videodatei. Videos werden nicht gespeichert; MeetingRecall extrahiert ausschließlich eine MP3-Audiospur.")
    {
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        ResetPlaybackUi();
        EmptyStateTitleText.Text = title;
        EmptyStateDescriptionText.Text = description;
        EmptyStateImportButton.Visibility = Visibility.Visible;
        ConversationDetail.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async Task<bool> ConfirmVideoImportAsync(string fileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Audio aus Video extrahieren?",
            Content = $"„{fileName}“ ist eine Videodatei. MeetingRecall speichert das Video nicht. Es wird ausschließlich eine lokale MP3-Audiospur erzeugt und dauerhaft gespeichert.",
            PrimaryButtonText = "Audio extrahieren",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string content, string closeButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void SetBusy(
        bool isBusy,
        string status,
        CancellationTokenSource? cancellation = null)
    {
        _isBusy = isBusy;
        _activeOperationCancellation = isBusy
            ? cancellation
            : null;
        BusyIndicator.IsActive = isBusy;
        ImportButton.IsEnabled = !isBusy;
        RecordingButton.IsEnabled = !isBusy;
        CancelOperationButton.Visibility =
            isBusy && cancellation is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        CancelOperationButton.IsEnabled =
            isBusy
            && cancellation is not null
            && !cancellation.IsCancellationRequested;
        StatusText.Text = status;
        if (_conversationQuestionPanel is not null)
        {
            RenderSelectedConversationQuestions();
        }
    }

    private void SeekBy(TimeSpan offset)
    {
        TimeSpan duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan position = _mediaPlayer.PlaybackSession.Position + offset;
        _mediaPlayer.PlaybackSession.Position = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > duration
                ? duration
                : position;
        UpdatePlaybackUi();
    }

    private void SeekToPointer(PointerRoutedEventArgs e)
    {
        TimeSpan duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero || TimelineTrack.ActualWidth <= 0)
        {
            return;
        }

        double pointerX = e.GetCurrentPoint(TimelineTrack).Position.X;
        double ratio = Math.Clamp(pointerX / TimelineTrack.ActualWidth, 0, 1);
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromTicks((long)(duration.Ticks * ratio));
        UpdatePlaybackUi();
    }

    private void UpdatePlaybackUi()
    {
        TimeSpan duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        TimeSpan position = _mediaPlayer.PlaybackSession.Position;

        CurrentTimeText.Text = FormatPlaybackTime(position);
        if (duration > TimeSpan.Zero)
        {
            TotalTimeText.Text = FormatPlaybackTime(duration);
        }

        double ratio = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1)
            : 0;

        double trackWidth = Math.Max(0, TimelineTrack.ActualWidth);
        TimelineProgress.Width = trackWidth * ratio;

        double thumbWidth = TimelineThumb.Width;
        double thumbLeft = Math.Clamp((trackWidth * ratio) - (thumbWidth / 2), 0, Math.Max(0, trackWidth - thumbWidth));
        TimelineThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);

        PlayPauseIcon.Glyph = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            ? "\uE769"
            : "\uE768";
    }

    private void ResetPlaybackUi()
    {
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";
        TimelineProgress.Width = 0;
        TimelineThumb.Margin = new Thickness(0);
        PlayPauseIcon.Glyph = "\uE768";
    }

    private void UpdateVolumeIcon()
    {
        if (MuteIcon is null)
        {
            return;
        }

        MuteIcon.Glyph = _mediaPlayer.IsMuted || _mediaPlayer.Volume <= 0
            ? "\uE74F"
            : _mediaPlayer.Volume < 0.5
                ? "\uE993"
                : "\uE767";
    }

    private string ResolvePhysicalDataRoot()
    {
        try
        {
            string virtualizedRoot = Path.Combine(
                ApplicationData.Current.LocalCacheFolder.Path,
                "Local",
                "MeetingRecall");

            if (Directory.Exists(virtualizedRoot))
            {
                return virtualizedRoot;
            }
        }
        catch
        {
            // Unpackaged execution has no ApplicationData package container.
        }

        return _paths.RootDirectory;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        CancelAllProcessingJobs();
        _importCancellation?.Cancel();
        _importCancellation?.Dispose();
        _importCancellation = null;
        _activeOperationCancellation = null;
        RootGrid.PreviewKeyDown -= PlaybackRootGrid_KeyDown;
        _playbackTimer.Stop();
        _mediaPlayer.Dispose();
    }

    private void ResizeWindow(int width, int height)
    {
        nint windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(width, height));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "Dauer unbekannt";
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatPlaybackTime(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSource(ConversationSource source)
    {
        return source switch
        {
            ConversationSource.ImportedVideoAudio => "aus Video extrahiert",
            ConversationSource.ImportedAudio => "Audioimport",
            ConversationSource.NativeRecording => "native Aufnahme",
            _ => "lokale Datei"
        };
    }

    private static string FormatFileSize(long sizeBytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;

        return sizeBytes switch
        {
            >= (long)gigabyte => $"{sizeBytes / gigabyte:0.##} GB",
            >= (long)megabyte => $"{sizeBytes / megabyte:0.##} MB",
            >= (long)kilobyte => $"{sizeBytes / kilobyte:0.##} KB",
            _ => $"{sizeBytes} B"
        };
    }

    private static IOrderedEnumerable<ConversationListItem> SortConversationItems(
        IEnumerable<ConversationListItem> items,
        ConversationSortMode sortMode)
    {
        return sortMode switch
        {
            ConversationSortMode.UpdatedAscending => items
                .OrderBy(item => item.SortTimestamp)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            ConversationSortMode.NameAscending => items
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(item => item.SortTimestamp),
            ConversationSortMode.NameDescending => items
                .OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(item => item.SortTimestamp),
            _ => items
                .OrderByDescending(item => item.SortTimestamp)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private static string GetConversationSortLabel(ConversationSortMode sortMode) =>
        sortMode switch
        {
            ConversationSortMode.UpdatedAscending => "älteste Änderung",
            ConversationSortMode.NameAscending => "Name A–Z",
            ConversationSortMode.NameDescending => "Name Z–A",
            _ => "zuletzt geändert"
        };

    private sealed record ConversationListItem(
        Conversation Conversation,
        string Title,
        string Metadata,
        DateTimeOffset SortTimestamp)
    {
        public string PrimaryContextAction => "Titel umbenennen";

        public string DestructiveContextAction => "Endgültig löschen";
    }

    private enum ConversationSortMode
    {
        UpdatedDescending = 0,
        UpdatedAscending = 1,
        NameAscending = 2,
        NameDescending = 3
    }
}
