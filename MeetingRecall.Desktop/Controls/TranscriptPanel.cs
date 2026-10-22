using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Settings;
using MeetingRecall.Core.Transcription;
using Windows.UI;

namespace MeetingRecall.Desktop.Controls;

internal sealed class TranscriptPanel : UserControl
{
    private readonly TextBlock _headingText = new();
    private readonly TextBlock _emptyText = new();
    private readonly TextBlock _progressText = new();
    private readonly TextBlock _interactionHint = new();
    private readonly ProgressBar _progressBar = new();
    private readonly StackPanel _progressPanel = new();
    private readonly StackPanel _segmentsPanel = new();
    private readonly Button _actionButton = new();
    private readonly Button _completeButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _hopToTextButton = new();
    private readonly Button _scrollToTopButton = new();
    private readonly Border _launchBar = new();
    private readonly ScrollViewer _transcriptScroller = new();
    private readonly List<SegmentRow> _segmentRows = [];
    private SegmentRow? _activeLiveRow;

    public TranscriptPanel()
    {
        Content = BuildContent();
    }

    public event EventHandler? TranscriptionRequested;

    public event EventHandler? CompleteProcessingRequested;

    public event EventHandler? CancellationRequested;

    public event EventHandler? ExportRequested;

    public event EventHandler? HopToTextRequested;

    public event Action<TimeSpan>? TimestampRequested;

    public void ShowConversation(
        Conversation? conversation,
        IReadOnlyDictionary<Guid, Speaker> speakers,
        bool isReadOnly = false)
    {
        ResetLiveHighlight();
        _segmentsPanel.Children.Clear();
        _segmentRows.Clear();
        _progressPanel.Visibility = Visibility.Collapsed;
        _launchBar.Visibility = conversation is null || isReadOnly
            ? Visibility.Collapsed
            : Visibility.Visible;
        _actionButton.IsEnabled = true;
        _completeButton.IsEnabled = true;
        bool hasTranscript = conversation?.TranscriptSegments.Count > 0;
        _exportButton.Visibility = hasTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        _hopToTextButton.Visibility = hasTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        _scrollToTopButton.Visibility = hasTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        _interactionHint.Visibility = hasTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (conversation is null)
        {
            _emptyText.Text = "Wähle ein Gespräch aus, um das Transkript anzuzeigen.";
            _emptyText.Visibility = Visibility.Visible;
            _segmentsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _actionButton.Content = conversation.ProcessingState switch
        {
            ConversationProcessingState.Failed => "Erneut versuchen",
            ConversationProcessingState.Ready when conversation.TranscriptSegments.Count > 0 => "Neu transkribieren",
            ConversationProcessingState.Processing => "Transkription neu starten",
            _ => "Transkript erstellen"
        };
        _completeButton.Content = hasTranscript
            ? "Beides neu erstellen"
            : "Beides erstellen";

        if (conversation.TranscriptSegments.Count == 0)
        {
            _emptyText.Text = conversation.ProcessingState == ConversationProcessingState.Failed
                ? "Die letzte Transkription ist fehlgeschlagen. Die Audiodatei wurde nicht verändert."
                : "Für dieses Gespräch wurde noch kein Transkript erstellt.";
            _emptyText.Visibility = Visibility.Visible;
            _segmentsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _emptyText.Visibility = Visibility.Collapsed;
        _segmentsPanel.Visibility = Visibility.Visible;

        foreach (TranscriptSegment segment in conversation.TranscriptSegments)
        {
            Speaker? speaker = speakers.GetValueOrDefault(segment.SpeakerId);
            SegmentRow row = CreateSegmentRow(segment, speaker);
            _segmentRows.Add(row);
            _segmentsPanel.Children.Add(row.Container);
        }
    }

    public void HopTo(TimeSpan position)
    {
        if (_segmentRows.Count == 0)
        {
            return;
        }

        SegmentRow target = _segmentRows
            .FirstOrDefault(row =>
                position >= row.Segment.StartTime
                && position < row.Segment.EndTime)
            ?? _segmentRows
                .Where(row => row.Segment.StartTime <= position)
                .LastOrDefault()
            ?? _segmentRows[0];

        target.Container.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.2
        });
    }

    public void UpdatePlaybackPosition(TimeSpan position, bool liveSyncEnabled)
    {
        if (!liveSyncEnabled || _segmentRows.Count == 0)
        {
            ResetLiveHighlight();
            return;
        }

        SegmentRow? target = _segmentRows.FirstOrDefault(row =>
            position >= row.Segment.StartTime
            && position < row.Segment.EndTime);
        if (target is null)
        {
            ResetLiveHighlight();
            return;
        }

        if (!ReferenceEquals(_activeLiveRow, target))
        {
            ResetLiveHighlight();
            _activeLiveRow = target;
        }

        TimeSpan duration = target.Segment.EndTime - target.Segment.StartTime;
        double ratio = duration > TimeSpan.Zero
            ? Math.Clamp(
                (position - target.Segment.StartTime).TotalMilliseconds
                / duration.TotalMilliseconds,
                0,
                1)
            : 1;
        int revealedCharacters = Math.Clamp(
            (int)Math.Round(target.Segment.CurrentText.Length * ratio),
            0,
            target.Segment.CurrentText.Length);
        if (target.RevealedCharacters == revealedCharacters)
        {
            return;
        }

        target.RevealedCharacters = revealedCharacters;
        RenderLiveText(target, revealedCharacters);
    }

    public void ShowProgress(TranscriptionProgress progress)
    {
        _progressPanel.Visibility = Visibility.Visible;
        _launchBar.Visibility = Visibility.Visible;
        _actionButton.IsEnabled = false;
        _completeButton.IsEnabled = false;
        _actionButton.Content = "Transkript wird erstellt …";
        _exportButton.Visibility = Visibility.Collapsed;
        _hopToTextButton.Visibility = Visibility.Collapsed;
        _scrollToTopButton.Visibility = Visibility.Collapsed;
        _interactionHint.Visibility = Visibility.Collapsed;
        _emptyText.Visibility = Visibility.Collapsed;

        _progressText.Text = progress.Stage switch
        {
            TranscriptionStage.DownloadingModel =>
                FormatDownloadProgress(progress, "Whisper-Modell"),
            TranscriptionStage.DownloadingSpeakerModel =>
                FormatDownloadProgress(
                    progress,
                    "Modelle für neuronale Sprechererkennung"),
            TranscriptionStage.PreparingAudio => "Audio wird für Whisper vorbereitet …",
            TranscriptionStage.Transcribing when progress.Fraction is >= 0 and <= 1 =>
                $"Transkription läuft · {progress.Fraction.Value * 100:0} %"
                + (progress.SegmentCount > 0
                    ? $" · {progress.SegmentCount} Segmente erkannt"
                    : string.Empty),
            TranscriptionStage.Transcribing => "Transkription läuft …",
            TranscriptionStage.AnalyzingSpeakers => "Sprecher werden vollständig lokal getrennt und abgeglichen …",
            TranscriptionStage.Saving => "Transkript wird lokal gespeichert …",
            TranscriptionStage.Completed => "Transkript wurde erstellt.",
            _ => "Transkription läuft …"
        };

        if (progress.Fraction is >= 0 and <= 1)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = progress.Fraction.Value;
        }
        else
        {
            _progressBar.IsIndeterminate = true;
        }
    }

public void ShowQueued(int position, bool isRunning)
    {
        ResetLiveHighlight();
        _progressPanel.Visibility = Visibility.Visible;
        _launchBar.Visibility = Visibility.Visible;
        _actionButton.IsEnabled = false;
        _completeButton.IsEnabled = false;
        _actionButton.Content = isRunning
            ? "Transkript wird erstellt …"
            : "In Warteschlange …";
        _exportButton.Visibility = Visibility.Collapsed;
        _hopToTextButton.Visibility = Visibility.Collapsed;
        _scrollToTopButton.Visibility = Visibility.Collapsed;
        _interactionHint.Visibility = Visibility.Collapsed;
        _emptyText.Visibility = Visibility.Collapsed;
        _progressBar.IsIndeterminate = true;
        _progressText.Text = isRunning
            ? "Transkription wird gestartet …"
            : $"In Warteschlange · Position {position}";
    }

    private FrameworkElement BuildContent()
    {
        var root = new StackPanel
        {
            Spacing = 12
        };

        var header = new Grid
        {
            ColumnSpacing = 12
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headingStack = new StackPanel
        {
            Spacing = 3
        };
        _headingText.Text = "Transkript";
        _headingText.FontSize = 18;
        _headingText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        headingStack.Children.Add(_headingText);
        headingStack.Children.Add(new TextBlock
        {
            Text = "Lokal verarbeitet · Audio bleibt auf diesem Gerät",
            FontSize = 12,
            Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
        });
        header.Children.Add(headingStack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _exportButton.Content = "Exportieren";
        _exportButton.Padding = new Thickness(12, 7, 12, 7);
        _exportButton.Visibility = Visibility.Collapsed;
        _exportButton.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(
            _exportButton,
            "Transkript als Text- oder Markdown-Datei speichern");
        AutomationProperties.SetName(
            _exportButton,
            "Transkript exportieren");
        actions.Children.Add(_exportButton);

        _hopToTextButton.Content = "Zur Textpassage springen";
        _hopToTextButton.Padding = new Thickness(12, 7, 12, 7);
        _hopToTextButton.Visibility = Visibility.Collapsed;
        _hopToTextButton.Click += (_, _) => HopToTextRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(
            _hopToTextButton,
            "Zur Passage an der aktuellen Audioposition springen");
        AutomationProperties.SetName(
            _hopToTextButton,
            "Zur aktuellen Textpassage springen");
        actions.Children.Add(_hopToTextButton);

        _scrollToTopButton.Content = new FontIcon
        {
            Glyph = "\uE74A",
            FontSize = 14
        };
        _scrollToTopButton.Width = 36;
        _scrollToTopButton.Height = 32;
        _scrollToTopButton.Padding = new Thickness(0);
        _scrollToTopButton.Visibility = Visibility.Collapsed;
        _scrollToTopButton.Click += (_, _) => ScrollToTop();
        ToolTipService.SetToolTip(
            _scrollToTopButton,
            "Ganz nach oben springen");
        AutomationProperties.SetName(
            _scrollToTopButton,
            "Zum Anfang des Transkripts springen");
        actions.Children.Add(_scrollToTopButton);

        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        root.Children.Add(header);

        root.Children.Add(BuildLaunchBar());

        _emptyText.TextWrapping = TextWrapping.Wrap;
        _emptyText.Foreground = GetThemeBrush("TextFillColorSecondaryBrush");
        root.Children.Add(_emptyText);

        _progressPanel.Spacing = 8;
        _progressPanel.Visibility = Visibility.Collapsed;
        _progressText.TextWrapping = TextWrapping.Wrap;
        _progressPanel.Children.Add(_progressText);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1;
        _progressBar.IsIndeterminate = true;
        _progressPanel.Children.Add(_progressBar);
        _cancelButton.Content = "Abbrechen";
        _cancelButton.HorizontalAlignment = HorizontalAlignment.Left;
        _cancelButton.Click += (_, _) => CancellationRequested?.Invoke(this, EventArgs.Empty);
        _progressPanel.Children.Add(_cancelButton);
        root.Children.Add(_progressPanel);

        var transcriptContent = new StackPanel
        {
            Spacing = 8
        };
        _segmentsPanel.Spacing = 8;
        _segmentsPanel.Visibility = Visibility.Collapsed;
        transcriptContent.Children.Add(_segmentsPanel);

        _interactionHint.Text =
            "Klicke auf eine Passage, um das Audio an ihrer Startzeit fortzusetzen.";
        _interactionHint.Margin = new Thickness(0, 4, 0, 0);
        _interactionHint.FontSize = 12;
        _interactionHint.TextWrapping = TextWrapping.Wrap;
        _interactionHint.Foreground =
            GetThemeBrush("TextFillColorSecondaryBrush");
        _interactionHint.Visibility = Visibility.Collapsed;

        _transcriptScroller.MaxHeight = 560;
        _transcriptScroller.VerticalScrollBarVisibility =
            ScrollBarVisibility.Auto;
        _transcriptScroller.HorizontalScrollBarVisibility =
            ScrollBarVisibility.Disabled;
        _transcriptScroller.Content = transcriptContent;
        root.Children.Add(_interactionHint);
        root.Children.Add(_transcriptScroller);

        return root;
    }

    private FrameworkElement BuildLaunchBar()
    {
        _actionButton.Padding = new Thickness(16, 8, 16, 8);
        _actionButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        _actionButton.Click += (_, _) =>
            TranscriptionRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(_actionButton, "Lokale Transkription starten");

        _completeButton.Padding = new Thickness(14, 8, 14, 8);
        _completeButton.Click += (_, _) =>
            CompleteProcessingRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(
            _completeButton,
            "Erstellt zuerst das Transkript und anschließend automatisch die Zusammenfassung.");
        AutomationProperties.SetName(
            _completeButton,
            "Transkript und Zusammenfassung automatisch erstellen");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(_actionButton);
        actions.Children.Add(_completeButton);

        _launchBar.Background = null;
        _launchBar.BorderThickness = new Thickness(0);
        _launchBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        _launchBar.Child = actions;
        return _launchBar;
    }

    private void ScrollToTop()
    {
        _transcriptScroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: 0,
            zoomFactor: null,
            disableAnimation: false);
    }

    private SegmentRow CreateSegmentRow(TranscriptSegment segment, Speaker? speaker)
    {
        string speakerName = speaker?.DisplayName ?? "Person";
        string colorHex = speaker?.ColorHex ?? SpeakerColorPalette.ForSequence(1);
        SolidColorBrush accentBrush = CreateColorBrush(colorHex, 255);
        SolidColorBrush backgroundBrush = CreateColorBrush(colorHex, 28);

        var border = new Border
        {
            Padding = new Thickness(14, 10, 14, 12),
            CornerRadius = new CornerRadius(10),
            Background = backgroundBrush,
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Tag = segment.StartTime
        };
        ToolTipService.SetToolTip(border, "Audio an dieser Stelle abspielen");
        AutomationProperties.SetName(
            border,
            $"{speakerName}, {FormatTimestamp(segment.StartTime)}: {segment.CurrentText}");
        border.Tapped += SegmentRow_Tapped;

        var stack = new StackPanel { Spacing = 7 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var speakerLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        speakerLabel.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = accentBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        speakerLabel.Children.Add(new TextBlock
        {
            Text = speakerName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(speakerLabel);

        var timestampButton = new Button
        {
            Content = FormatTimestamp(segment.StartTime),
            Padding = new Thickness(8, 3, 8, 3),
            Tag = segment.StartTime
        };
        ToolTipService.SetToolTip(timestampButton, "Audio an dieser Stelle abspielen");
        AutomationProperties.SetName(
            timestampButton,
            $"Zu {FormatTimestamp(segment.StartTime)} springen");
        timestampButton.Click += (_, _) => TimestampRequested?.Invoke(segment.StartTime);
        Grid.SetColumn(timestampButton, 1);
        header.Children.Add(timestampButton);
        stack.Children.Add(header);

        var transcriptText = new TextBlock
        {
            Text = segment.CurrentText,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        stack.Children.Add(transcriptText);

        border.Child = stack;
        return new SegmentRow(
            segment,
            border,
            transcriptText,
            accentBrush);
    }

    private void ResetLiveHighlight()
    {
        if (_activeLiveRow is null)
        {
            return;
        }

        _activeLiveRow.Text.Inlines.Clear();
        _activeLiveRow.Text.Text = _activeLiveRow.Segment.CurrentText;
        _activeLiveRow.RevealedCharacters = -1;
        _activeLiveRow = null;
    }

    private static void RenderLiveText(
        SegmentRow row,
        int revealedCharacters)
    {
        string text = row.Segment.CurrentText;
        row.Text.Text = string.Empty;
        row.Text.Inlines.Clear();

        if (revealedCharacters > 0)
        {
            row.Text.Inlines.Add(new Run
            {
                Text = text[..revealedCharacters],
                Foreground = row.AccentBrush
            });
        }

        if (revealedCharacters < text.Length)
        {
            row.Text.Inlines.Add(new Run
            {
                Text = text[revealedCharacters..]
            });
        }
    }

    private void SegmentRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: TimeSpan position })
        {
            TimestampRequested?.Invoke(position);
        }
    }

    private static SolidColorBrush CreateColorBrush(string colorHex, byte alpha)
    {
        string normalized = SpeakerColorPalette.Normalize(colorHex);
        byte red = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
    }

    private static string FormatDownloadProgress(
        TranscriptionProgress progress,
        string modelName)
    {
        double percentage = Math.Clamp((progress.Fraction ?? 0) * 100, 0, 100);
        if (progress.BytesPerSecond is not > 0)
        {
            return $"{modelName} werden lokal eingerichtet · {percentage:0.0} % · Geschwindigkeit wird gemessen …";
        }

        double megabytesPerSecond = progress.BytesPerSecond.Value / 1_000_000;
        double megabitsPerSecond = progress.BytesPerSecond.Value * 8 / 1_000_000;
        return $"{modelName} werden lokal eingerichtet · {percentage:0.0} % · {megabytesPerSecond:0.0} MB/s · {megabitsPerSecond:0.0} Mbit/s";
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static Brush? GetThemeBrush(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? value)
            ? value as Brush
            : null;
    }

    private sealed class SegmentRow(
        TranscriptSegment segment,
        Border container,
        TextBlock text,
        SolidColorBrush accentBrush)
    {
        public TranscriptSegment Segment { get; } = segment;
        public Border Container { get; } = container;
        public TextBlock Text { get; } = text;
        public SolidColorBrush AccentBrush { get; } = accentBrush;
        public int RevealedCharacters { get; set; } = -1;
    }
}
