using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Interfaces;
using Windows.System;

namespace MeetingRecall.Desktop.Controls;

internal sealed class ConversationQuestionPanel : UserControl
{
    private static readonly SolidColorBrush AssistantVerifiedAnswerBrush = new(
        Windows.UI.Color.FromArgb(255, 22, 92, 58));
    private static readonly SolidColorBrush AssistantBestEffortBrush = new(
        Windows.UI.Color.FromArgb(255, 116, 62, 0));
    private static readonly SolidColorBrush AssistantErrorBrush = new(
        Windows.UI.Color.FromArgb(255, 112, 24, 24));

    private readonly Func<string, string, string> _translate;
    private readonly TextBlock _emptyText = new();
    private readonly StackPanel _messagesPanel = new();
    private readonly ScrollViewer _messagesScroller = new();
    private readonly TextBox _questionBox = new();
    private readonly Button _sendButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _clearButton = new();
    private readonly StackPanel _progressPanel = new();
    private readonly TextBlock _progressText = new();
    private readonly ProgressBar _progressBar = new();
    private bool _canAsk;
    private bool _isBusy;
    private bool _hasPendingWork;
    private int _scrollRequestVersion;

    public ConversationQuestionPanel(
        Func<string, string, string> translate)
    {
        _translate = translate;
        Content = BuildContent();
    }

    public event Action<string>? QuestionSubmitted;

    public event EventHandler? CancellationRequested;

    public event EventHandler? ClearRequested;

    public event Action<TimeSpan>? SourceRequested;

    public event Action<int>? ForceAnswerRequested;

    public event Action<Guid, ConversationQuestionMessageKind>?
        MessageDeleteRequested;

    public void ShowConversation(
        Conversation? conversation,
        IReadOnlyList<ConversationQuestionExchange> exchanges,
        bool isBusy = false,
        bool inputLocked = false)
    {
        _hasPendingWork = isBusy;
        _isBusy = isBusy || inputLocked;
        _canAsk = conversation?.TranscriptSegments.Count > 0;
        _messagesPanel.Children.Clear();
        for (int index = 0; index < exchanges.Count; index++)
        {
            ConversationQuestionExchange exchange = exchanges[index];
            _messagesPanel.Children.Add(CreateUserMessage(exchange));
            if (exchange.Answer is not null)
            {
                _messagesPanel.Children.Add(CreateAssistantMessage(
                    exchange.Answer,
                    index,
                    exchange.Id));
            }
        }

        _clearButton.Visibility = exchanges.Count > 0 && !_isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        _progressPanel.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        _messagesScroller.Visibility = exchanges.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _emptyText.Visibility = exchanges.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _emptyText.Text = conversation is null
            ? _translate(
                "Wähle ein Gespräch aus.",
                "Select a conversation.")
            : conversation.TranscriptSegments.Count == 0
                ? _translate(
                    "Fragen können nach der Transkription gestellt werden.",
                    "Questions can be asked after transcription.")
                : _translate(
                    "Stelle eine konkrete Frage zum Gespräch. Jede Antwort zeigt die verwendeten Transkriptstellen.",
                    "Ask a specific question about the conversation. Every answer shows the transcript passages it used.");
        UpdateComposerState();
        ScrollToLatestMessage();
    }

    public void ShowProgress(
        ConversationQuestionProgress progress,
        bool allowInputWhileProcessing = false)
    {
        _hasPendingWork =
            progress.Stage != ConversationQuestionStage.Completed;
        _isBusy = _hasPendingWork && !allowInputWhileProcessing;
        _progressPanel.Visibility = _hasPendingWork
            ? Visibility.Visible
            : Visibility.Collapsed;
        _progressText.Text = FormatProgress(progress);
        if (progress.Fraction is >= 0 and <= 1)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = progress.Fraction.Value;
        }
        else
        {
            _progressBar.IsIndeterminate = true;
        }

        if (_hasPendingWork)
        {
            _clearButton.Visibility = Visibility.Collapsed;
        }

        UpdateComposerState();
    }

public void ShowQueueState(
        int position,
        bool isRunning,
        int queuedForConversation,
        bool inputLocked = false)
    {
        _hasPendingWork = true;
        _isBusy = inputLocked;
        _progressPanel.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _progressText.Text = isRunning
            ? _translate(
                "Antwort wird gestartet …",
                "Starting answer …")
            : _translate(
                $"In Warteschlange · Position {position}"
                + (queuedForConversation > 1
                    ? $" · {queuedForConversation} Fragen dieses Gesprächs"
                    : string.Empty),
                $"Queued · position {position}"
                + (queuedForConversation > 1
                    ? $" · {queuedForConversation} questions for this conversation"
                    : string.Empty));
        _clearButton.Visibility = Visibility.Collapsed;
        UpdateComposerState();
    }

    private FrameworkElement BuildContent()
    {
        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(BuildHeader());

        var messageArea = new Grid
        {
            Height = 560
        };

        _emptyText.TextWrapping = TextWrapping.Wrap;
        _emptyText.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
        _emptyText.VerticalAlignment = VerticalAlignment.Center;
        _emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyText.TextAlignment = TextAlignment.Center;
        messageArea.Children.Add(_emptyText);

        _messagesPanel.Spacing = 12;
        _messagesScroller.Content = _messagesPanel;
        _messagesScroller.HorizontalScrollMode = ScrollMode.Disabled;
        _messagesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _messagesScroller.VerticalScrollMode = ScrollMode.Auto;
        _messagesScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _messagesScroller.Visibility = Visibility.Collapsed;
        messageArea.Children.Add(_messagesScroller);
        root.Children.Add(messageArea);

        _progressPanel.Spacing = 8;
        _progressPanel.Visibility = Visibility.Collapsed;
        _progressText.TextWrapping = TextWrapping.Wrap;
        _progressPanel.Children.Add(_progressText);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1;
        _progressBar.IsIndeterminate = true;
        _progressPanel.Children.Add(_progressBar);
        _cancelButton.Content = _translate("Abbrechen", "Cancel");
        _cancelButton.HorizontalAlignment = HorizontalAlignment.Left;
        _cancelButton.Click += (_, _) =>
            CancellationRequested?.Invoke(this, EventArgs.Empty);
        _progressPanel.Children.Add(_cancelButton);
        root.Children.Add(_progressPanel);

        root.Children.Add(BuildComposer());
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = _translate(
                "Fragen zum Gespräch",
                "Questions about the conversation"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        heading.Children.Add(BuildAnswerLegend());
        grid.Children.Add(heading);

        _clearButton.Content = _translate("Chat leeren", "Clear chat");
        _clearButton.VerticalAlignment = VerticalAlignment.Top;
        _clearButton.Visibility = Visibility.Collapsed;
        _clearButton.Click += (_, _) =>
            ClearRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_clearButton, 1);
        grid.Children.Add(_clearButton);
        return grid;
    }

    private FrameworkElement BuildAnswerLegend()
    {
        var items = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16
        };
        items.Children.Add(new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 13,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        items.Children.Add(CreateLegendItem(
            AssistantVerifiedAnswerBrush,
            _translate("Geprüfte Antwort", "Verified answer")));
        items.Children.Add(CreateLegendItem(
            AssistantBestEffortBrush,
            _translate("Unsichere Antwort", "Uncertain answer")));
        items.Children.Add(CreateLegendItem(
            AssistantErrorBrush,
            _translate("Keine Antwort", "No answer")));

        var legend = new Border
        {
            Margin = new Thickness(0, 5, 0, 0),
            Padding = new Thickness(10, 6, 12, 6),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("ControlFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = items
        };
        AutomationProperties.SetName(
            legend,
            _translate(
                "Bedeutung der Antwortfarben: Grün geprüft, Orange unsicher, Rot keine Antwort",
                "Answer colors: green verified, orange uncertain, red no answer"));
        ToolTipService.SetToolTip(
            legend,
            _translate(
                "Grün wurde vollständig geprüft. Orange ist am Transkript belegt, aber nicht vollständig bestätigt. Rot bedeutet, dass keine belastbare Antwort gefunden wurde.",
                "Green was fully verified. Orange is grounded in the transcript but not fully confirmed. Red means no reliable answer was found."));
        return legend;
    }

    private static FrameworkElement CreateLegendItem(
        Brush color,
        string label)
    {
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        item.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center
        });
        item.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return item;
    }

    private FrameworkElement BuildComposer()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        _questionBox.PlaceholderText = _translate(
            "Frage zu diesem Gespräch …",
            "Ask about this conversation …");
        _questionBox.AcceptsReturn = false;
        _questionBox.TextWrapping = TextWrapping.Wrap;
        _questionBox.MaxLength = 1_000;
        _questionBox.MinHeight = 44;
        _questionBox.MaxHeight = 120;
        _questionBox.Padding = new Thickness(14, 8, 14, 8);
        _questionBox.CornerRadius = new CornerRadius(10);
        _questionBox.BorderThickness = new Thickness(0);
        _questionBox.Background = ThemeBrush("ControlFillColorDefaultBrush");
        _questionBox.VerticalContentAlignment = VerticalAlignment.Center;
        _questionBox.TextChanged += (_, _) => UpdateComposerState();
        _questionBox.KeyDown += QuestionBox_KeyDown;
        AutomationProperties.SetName(
            _questionBox,
            _translate(
                "Frage zum ausgewählten Gespräch",
                "Question about the selected conversation"));
        grid.Children.Add(_questionBox);

        _sendButton.Width = 44;
        _sendButton.Height = 44;
        _sendButton.Padding = new Thickness(0);
        _sendButton.VerticalAlignment = VerticalAlignment.Bottom;
        _sendButton.Style =
            Application.Current.Resources["AccentButtonStyle"] as Style;
        _sendButton.Content = new FontIcon
        {
            Glyph = "\uE724",
            FontSize = 16
        };
        _sendButton.Click += (_, _) => SubmitQuestion();
        AutomationProperties.SetName(
            _sendButton,
            _translate("Frage senden", "Send question"));
        ToolTipService.SetToolTip(
            _sendButton,
            _translate("Frage senden", "Send question"));
        Grid.SetColumn(_sendButton, 1);
        grid.Children.Add(_sendButton);
        UpdateComposerState();
        return grid;
    }

    private void QuestionBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        SubmitQuestion();
    }

    private void SubmitQuestion()
    {
        string question = _questionBox.Text.Trim();
        if (!_canAsk || _isBusy || question.Length == 0)
        {
            return;
        }

        _questionBox.Text = string.Empty;
        QuestionSubmitted?.Invoke(question);
    }

    private void UpdateComposerState()
    {
        _questionBox.IsEnabled = _canAsk && !_isBusy;
        _sendButton.IsEnabled = _canAsk
            && !_isBusy
            && !string.IsNullOrWhiteSpace(_questionBox.Text);
        _cancelButton.IsEnabled = _hasPendingWork;
    }

    private FrameworkElement CreateUserMessage(
        ConversationQuestionExchange exchange)
    {
        var text = new TextBlock
        {
            Text = exchange.Question,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush")
        };
        var message = new Border
        {
            MaxWidth = 650,
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(14, 14, 4, 14),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = ThemeBrush("AccentFillColorDefaultBrush"),
            Child = text
        };
        message.ContextFlyout = CreateDeleteMessageFlyout(
            exchange.Id,
            ConversationQuestionMessageKind.Question);
        return message;
    }

    private FrameworkElement CreateAssistantMessage(
        GroundedConversationAnswer answer,
        int exchangeIndex,
        Guid exchangeId)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = answer.IsAnswerable
                ? answer.Text
                : _translate(
                    "Das lässt sich aus dem Transkript nicht zuverlässig beantworten.",
                    "The transcript does not support a reliable answer."),
            TextWrapping = TextWrapping.Wrap
        });

        if (answer.IsAnswerable && answer.Sources.Count > 0)
        {
            if (answer.IsBestEffort)
            {
                content.Children.Add(new TextBlock
                {
                    Text = _translate(
                        "Unsichere Antwort · kann ungenau sein",
                        "Uncertain answer · may be inaccurate"),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                });
            }

            var sourceList = new StackPanel { Spacing = 10 };
            foreach (ConversationAnswerSource source in answer.Sources)
            {
                sourceList.Children.Add(CreateSource(source));
            }

            var evidenceExpander = new Expander
            {
                Header = new TextBlock
                {
                    Text = _translate(
                        "Belege aus dem Transkript",
                        "Transcript evidence"),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                },
                Content = sourceList,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(
                evidenceExpander,
                _translate(
                    "Belege aus dem Transkript ein- oder ausklappen",
                    "Expand or collapse transcript evidence"));
            content.Children.Add(evidenceExpander);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = _translate(
                    "Es wurde bewusst keine Vermutung ergänzt.",
                    "No guess was added."),
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            });

            var forceButton = new Button
            {
                Content = _translate(
                    "Antwort erzwingen",
                    "Force answer"),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !_isBusy,
                Tag = exchangeIndex
            };
            forceButton.Click += (_, _) =>
                ForceAnswerRequested?.Invoke(exchangeIndex);
            AutomationProperties.SetName(
                forceButton,
                _translate(
                    "Unsichere Best-Effort-Antwort erzwingen",
                    "Force an uncertain best-effort answer"));
            ToolTipService.SetToolTip(
                forceButton,
                _translate(
                    "Antwortet bestmöglich, kann aber ungenau sein",
                    "Answers as well as possible, but may be inaccurate"));
            content.Children.Add(forceButton);
        }

        var message = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(14, 14, 14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = !answer.IsAnswerable
                ? AssistantErrorBrush
                : answer.IsBestEffort
                    ? AssistantBestEffortBrush
                    : AssistantVerifiedAnswerBrush,
            Child = content
        };
        message.ContextFlyout = CreateDeleteMessageFlyout(
            exchangeId,
            ConversationQuestionMessageKind.Answer);
        return message;
    }

    private MenuFlyout CreateDeleteMessageFlyout(
        Guid exchangeId,
        ConversationQuestionMessageKind messageKind)
    {
        var deleteItem = new MenuFlyoutItem
        {
            Text = _translate("Löschen", "Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = !_isBusy,
            Foreground = ThemeBrush("SystemFillColorCriticalBrush")
        };
        deleteItem.Click += (_, _) => MessageDeleteRequested?.Invoke(
            exchangeId,
            messageKind);
        AutomationProperties.SetName(
            deleteItem,
            messageKind == ConversationQuestionMessageKind.Question
                ? _translate(
                    "Frage und zugehörige Antwort löschen",
                    "Delete question and its answer")
                : _translate(
                    "Antwort löschen",
                    "Delete answer"));
        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private FrameworkElement CreateSource(ConversationAnswerSource source)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        header.Children.Add(new TextBlock
        {
            Text = source.SpeakerName,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var timestampButton = new Button
        {
            Content = FormatTimestamp(source.StartTime),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 12,
            Tag = source.StartTime
        };
        timestampButton.Click += (_, _) =>
            SourceRequested?.Invoke(source.StartTime);
        AutomationProperties.SetName(
            timestampButton,
            _translate(
                $"Audio bei {FormatTimestamp(source.StartTime)} öffnen",
                $"Open audio at {FormatTimestamp(source.StartTime)}"));
        Grid.SetColumn(timestampButton, 1);
        header.Children.Add(timestampButton);

        var sourceContent = new StackPanel { Spacing = 6 };
        sourceContent.Children.Add(header);
        sourceContent.Children.Add(new TextBlock
        {
            Text = source.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            Child = sourceContent
        };
    }

    private string FormatProgress(ConversationQuestionProgress progress)
    {
        string stage = progress.Stage switch
        {
            ConversationQuestionStage.SearchingTranscript => _translate(
                "Passende Transkriptstellen werden gesucht …",
                "Finding relevant transcript passages …"),
            ConversationQuestionStage.DownloadingModel => _translate(
                "Lokales Sprachmodell wird heruntergeladen …",
                "Downloading local language model …"),
            ConversationQuestionStage.LoadingModel => _translate(
                "Lokales Sprachmodell wird geladen …",
                "Loading local language model …"),
            ConversationQuestionStage.FormulatingAnswer => _translate(
                "Quellengebundene Antwort wird formuliert …",
                "Writing a source-grounded answer …"),
            ConversationQuestionStage.VerifyingAnswer => _translate(
                "Jede Aussage wird gegen die Quellen geprüft …",
                "Checking every claim against its sources …"),
            _ => _translate("Antwort abgeschlossen.", "Answer completed.")
        };
        if (progress.Fraction is not >= 0 or > 1)
        {
            return stage;
        }

        string details = $"{progress.Fraction.Value * 100:0} %";
        if (progress.Stage == ConversationQuestionStage.DownloadingModel
            && progress.BytesPerSecond is > 0)
        {
            details +=
                $" · {progress.BytesPerSecond.Value / 1_000_000:0.0} MB/s"
                + $" · {progress.BytesPerSecond.Value * 8 / 1_000_000:0.0} Mbit/s";
            if (progress.TotalBytes is > 0
                && progress.DownloadedBytes > 0
                && progress.TotalBytes.Value > progress.DownloadedBytes)
            {
                double seconds =
                    (progress.TotalBytes.Value - progress.DownloadedBytes)
                    / progress.BytesPerSecond.Value;
                if (seconds is >= 2 and <= 86_400)
                {
                    TimeSpan remaining = TimeSpan.FromSeconds(seconds);
                    string value = remaining.TotalHours >= 1
                        ? remaining.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                        : remaining.TotalMinutes >= 1
                            ? remaining.ToString(@"m\:ss", CultureInfo.InvariantCulture)
                            : $"{Math.Ceiling(remaining.TotalSeconds):0} s";
                    details += _translate(
                        $" · noch ca. {value}",
                        $" · about {value} remaining");
                }
            }
        }

        return stage + Environment.NewLine + details;
    }

    private void ScrollToLatestMessage()
    {
        int requestVersion = ++_scrollRequestVersion;
        DispatcherQueue.TryEnqueue(() =>
            ScrollToLatestMessageAfterLayout(requestVersion, retry: true));
    }

    private void ScrollToLatestMessageAfterLayout(
        int requestVersion,
        bool retry)
    {
        if (requestVersion != _scrollRequestVersion
            || _messagesScroller.Visibility != Visibility.Visible)
        {
            return;
        }

        _messagesScroller.UpdateLayout();
        _messagesScroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: _messagesScroller.ScrollableHeight,
            zoomFactor: null,
            disableAnimation: true);

        if (retry)
        {
            // Newly created message controls can finish measuring one dispatcher
            // cycle after their parent. Recheck once against the final extent.
            DispatcherQueue.TryEnqueue(() =>
                ScrollToLatestMessageAfterLayout(
                    requestVersion,
                    retry: false));
        }
    }

    private static string FormatTimestamp(TimeSpan timestamp) =>
        timestamp.TotalHours >= 1
            ? timestamp.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : timestamp.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Brush
            : null;
}

internal enum ConversationQuestionMessageKind
{
    Question = 0,
    Answer = 1
}
