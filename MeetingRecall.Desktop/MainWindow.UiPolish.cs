using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private bool _uiPolishInitialized;
    private bool _visualPolishApplied;
    private bool _isUpdatingConversationMetadata;
    private bool _isUpdatingListMetadata;
    private bool _isUpdatingStatus;
    private Grid? _archiveHeaderGrid;
    private StackPanel? _archiveStatusPanel;
    private Button? _titleEditButton;
    private long _conversationMetadataCallbackToken = -1;
    private long _statusCallbackToken = -1;
    private long _busyCallbackToken = -1;

    public void InitializeUiPolish()
    {
        if (_uiPolishInitialized)
        {
            return;
        }

        _uiPolishInitialized = true;
        RootGrid.Loaded += UiPolishRootGrid_Loaded;

        _conversationItems.CollectionChanged += ConversationItems_CollectionChanged;
        ConversationsList.SelectionChanged += ConversationsList_UiPolishSelectionChanged;

        _conversationMetadataCallbackToken = ConversationMetadataText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => ApplySelectedConversationMetadata());
        _statusCallbackToken = StatusText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => NormalizeStatusAndHeader());
        _busyCallbackToken = BusyIndicator.RegisterPropertyChangedCallback(
            ProgressRing.IsActiveProperty,
            (_, _) => UpdateArchiveHeaderVisibility());

        Closed += UiPolishWindow_Closed;
    }

    private void UiPolishRootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_visualPolishApplied)
        {
            return;
        }

        _visualPolishApplied = true;
        ConfigureArchiveHeader();
        AddTitleEditButton();
        NormalizeStatusAndHeader();
    }

    private void ConfigureArchiveHeader()
    {
        if (StatusText.Parent is not StackPanel statusPanel
            || statusPanel.Parent is not Grid headerGrid)
        {
            return;
        }

        _archiveHeaderGrid = headerGrid;
        _archiveStatusPanel = statusPanel;
        ConversationPaneHeadingText.Visibility = Visibility.Visible;
    }

    private void AddTitleEditButton()
    {
        if (ConversationTitleText.Parent is not StackPanel titleStack)
        {
            return;
        }

        int titleIndex = titleStack.Children.IndexOf(ConversationTitleText);
        if (titleIndex < 0)
        {
            return;
        }

        titleStack.Children.RemoveAt(titleIndex);

        var titleRow = new Grid
        {
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        ConversationTitleText.HorizontalAlignment = HorizontalAlignment.Stretch;
        titleRow.Children.Add(ConversationTitleText);

        var editButton = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Opacity = 0.55,
            Content = new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 14
            }
        };
        Grid.SetColumn(editButton, 1);
        ToolTipService.SetToolTip(editButton, "Titel bearbeiten");
        AutomationProperties.SetName(editButton, "Gesprächstitel bearbeiten");
        editButton.PointerEntered += (_, _) => editButton.Opacity = 1;
        editButton.PointerExited += (_, _) => editButton.Opacity = 0.55;
        editButton.Click += EditConversationTitleButton_Click;
        editButton.Visibility =
            ConversationsList.SelectedItem is ConversationListItem
                ? Visibility.Visible
                : Visibility.Collapsed;
        titleRow.Children.Add(editButton);
        _titleEditButton = editButton;

        titleStack.Children.Insert(titleIndex, titleRow);
    }

    private async void EditConversationTitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem is not ConversationListItem selectedItem)
        {
            return;
        }

        var titleBox = new TextBox
        {
            Text = selectedItem.Conversation.Title,
            Header = "Titel",
            MaxLength = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Gesprächstitel bearbeiten",
            Content = titleBox,
            PrimaryButtonText = "Speichern",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) =>
        {
            titleBox.Focus(FocusState.Programmatic);
            titleBox.SelectAll();
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (!selectedItem.Conversation.Rename(titleBox.Text))
            {
                return;
            }

            SetBusy(true, "Titel wird gespeichert …");
            await _conversationRepository.UpsertAsync(selectedItem.Conversation);
            await LoadConversationsAsync(selectedItem.Conversation.Id);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Titel konnte nicht geändert werden",
                exception.Message,
                "Schließen");
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void ConversationsList_UiPolishSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_titleEditButton is not null)
        {
            _titleEditButton.Visibility =
                ConversationsList.SelectedItem is ConversationListItem
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        ApplySelectedConversationMetadata();
    }

    private void ApplySelectedConversationMetadata()
    {
        if (_isUpdatingConversationMetadata
            || ConversationsList.SelectedItem is not ConversationListItem selectedItem)
        {
            return;
        }

        string metadata = FormatDetailMetadata(selectedItem.Conversation);
        if (string.Equals(ConversationMetadataText.Text, metadata, StringComparison.Ordinal))
        {
            return;
        }

        _isUpdatingConversationMetadata = true;
        try
        {
            ConversationMetadataText.Text = metadata;
        }
        finally
        {
            _isUpdatingConversationMetadata = false;
        }
    }

    private void ConversationItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isUpdatingListMetadata || e.NewItems is null)
        {
            return;
        }

        ConversationListItem[] addedItems = e.NewItems
            .OfType<ConversationListItem>()
            .ToArray();
        if (addedItems.Length == 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => ApplyListMetadata(addedItems));
    }

    private void ApplyListMetadata(IReadOnlyCollection<ConversationListItem> items)
    {
        if (_isUpdatingListMetadata)
        {
            return;
        }

        Guid? selectedConversationId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        ConversationListItem? replacementSelection = null;
        bool wasRefreshing = _isRefreshingConversations;
        _isUpdatingListMetadata = true;
        _isRefreshingConversations = true;
        try
        {
            foreach (ConversationListItem item in items)
            {
                int index = _conversationItems.IndexOf(item);
                if (index < 0)
                {
                    continue;
                }

                string metadata = FormatListMetadata(item);
                if (!string.Equals(item.Metadata, metadata, StringComparison.Ordinal))
                {
                    ConversationListItem replacement = item with
                    {
                        Metadata = metadata
                    };
                    _conversationItems[index] = replacement;
                    if (selectedConversationId == item.Conversation.Id)
                    {
                        replacementSelection = replacement;
                    }
                }
            }

            if (replacementSelection is not null)
            {
                ConversationsList.SelectedItem = replacementSelection;
            }
        }
        finally
        {
            _isRefreshingConversations = wasRefreshing;
            _isUpdatingListMetadata = false;
        }
    }

    private void NormalizeStatusAndHeader()
    {
        if (_isUpdatingStatus)
        {
            return;
        }

        _isUpdatingStatus = true;
        try
        {
            if (string.Equals(StatusText.Text, "Aufnahme gespeichert", StringComparison.Ordinal)
                || string.Equals(StatusText.Text, "Recording saved", StringComparison.Ordinal))
            {
                StatusText.Text = string.Empty;
            }
        }
        finally
        {
            _isUpdatingStatus = false;
        }

        UpdateArchiveHeaderVisibility();
    }

    private void UpdateArchiveHeaderVisibility()
    {
        if (_archiveHeaderGrid is null)
        {
            return;
        }

        _archiveHeaderGrid.Visibility = Visibility.Visible;
        if (_archiveStatusPanel is not null)
        {
            _archiveStatusPanel.Visibility =
                BusyIndicator.IsActive
                || !string.IsNullOrWhiteSpace(StatusText.Text)
                || CancelOperationButton.Visibility == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private static string FormatDetailMetadata(Conversation conversation)
    {
        return $"{conversation.RecordedAt:dddd, dd. MMMM yyyy · HH:mm} · "
               + $"{DurationLabel}: {FormatDuration(conversation.Duration)}";
    }

    private static string FormatListMetadata(ConversationListItem item)
    {
        return $"{item.Conversation.RecordedAt:dd.MM.yyyy HH:mm} · "
               + $"{DurationLabel}: {FormatDuration(item.Conversation.Duration)}";
    }

    private static string DurationLabel =>
        string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "de",
            StringComparison.OrdinalIgnoreCase)
            ? "Länge"
            : "Duration";

    private void UiPolishWindow_Closed(object sender, WindowEventArgs args)
    {
        RootGrid.Loaded -= UiPolishRootGrid_Loaded;
        _conversationItems.CollectionChanged -= ConversationItems_CollectionChanged;
        ConversationsList.SelectionChanged -= ConversationsList_UiPolishSelectionChanged;

        if (_conversationMetadataCallbackToken >= 0)
        {
            ConversationMetadataText.UnregisterPropertyChangedCallback(
                TextBlock.TextProperty,
                _conversationMetadataCallbackToken);
        }

        if (_statusCallbackToken >= 0)
        {
            StatusText.UnregisterPropertyChangedCallback(
                TextBlock.TextProperty,
                _statusCallbackToken);
        }

        if (_busyCallbackToken >= 0)
        {
            BusyIndicator.UnregisterPropertyChangedCallback(
                ProgressRing.IsActiveProperty,
                _busyCallbackToken);
        }
    }
}
