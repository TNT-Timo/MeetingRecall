using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;

namespace MeetingRecall.Desktop.Controls;

internal sealed class MeetingSummaryPanel : UserControl
{
    private readonly Func<string, string, string> _translate;
    private readonly TextBlock _emptyText = new();
    private readonly TextBlock _progressText = new();
    private readonly ProgressBar _progressBar = new();
    private readonly StackPanel _progressPanel = new();
    private readonly StackPanel _threeVersionReadyPanel = new();
    private readonly StackPanel _summaryContent = new();
    private readonly Button _generateButton = new();
    private readonly Button _generateThreeVersionsButton = new();
    private readonly Button _openThreeVersionsButton = new();
    private readonly Button _cancelButton = new();
    private readonly Border _launchBar = new();
    private Button? _generateAnotherTitleButton;
    private Button? _customTitleButton;
    private MeetingSummary? _summary;
    private bool _isReadOnly;

    public MeetingSummaryPanel(Func<string, string, string> translate)
    {
        _translate = translate;
        Content = BuildContent();
    }

    public event EventHandler? GenerationRequested;
    public event EventHandler? ThreeVersionGenerationRequested;
    public event EventHandler? OpenThreeVersionPickerRequested;
    public event EventHandler? CancellationRequested;
    public event Action<string>? ApplySuggestedTitleRequested;
    public event EventHandler? GenerateAnotherTitleRequested;
    public event EventHandler? CustomTitleRequested;

    public void ShowConversation(
        Conversation? conversation,
        MeetingSummary? summary,
        bool isReadOnly = false)
    {
        _summary = summary;
        _isReadOnly = isReadOnly;
        _progressPanel.Visibility = Visibility.Collapsed;
        _threeVersionReadyPanel.Visibility = Visibility.Collapsed;
        _generateButton.IsEnabled = true;
        _generateThreeVersionsButton.IsEnabled = true;
        _summaryContent.Children.Clear();
        _launchBar.Visibility = conversation is not null
            && conversation.TranscriptSegments.Count > 0 && !isReadOnly
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (conversation is null)
        {
            ShowEmpty(_translate(
                "Wähle ein Gespräch aus.",
                "Select a conversation."));
            return;
        }

        if (conversation.TranscriptSegments.Count == 0)
        {
            ShowEmpty(_translate(
                "Eine Zusammenfassung kann nach der Transkription erstellt werden.",
                "A summary can be created after transcription."));
            _generateButton.IsEnabled = false;
            _generateThreeVersionsButton.IsEnabled = false;
            _generateButton.Content = _translate(
                "Transkript erforderlich",
                "Transcript required");
            return;
        }

        _generateButton.Content = summary is null
            ? _translate("Zusammenfassung erstellen", "Create summary")
            : _translate("Neu erstellen", "Regenerate");
        if (summary is null)
        {
            ShowEmpty(_translate(
                "Noch wurde keine Zusammenfassung erstellt.",
                "No summary has been created yet."));
            return;
        }

        _emptyText.Visibility = Visibility.Collapsed;
        _summaryContent.Visibility = Visibility.Visible;
        RenderSummary(summary);
    }

    public void ShowThreeVersionDraftsReady(Conversation conversation)
    {
        _summary = null;
        _isReadOnly = false;
        _progressPanel.Visibility = Visibility.Collapsed;
        _emptyText.Visibility = Visibility.Collapsed;
        _summaryContent.Visibility = Visibility.Collapsed;
        _threeVersionReadyPanel.Visibility = Visibility.Visible;
        _launchBar.Visibility = conversation.TranscriptSegments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _generateButton.IsEnabled = true;
        _generateButton.Content = _translate("Neu erstellen", "Regenerate");
        _generateThreeVersionsButton.IsEnabled = true;
    }

    private void ShowEmpty(string text)
    {
        _threeVersionReadyPanel.Visibility = Visibility.Collapsed;
        _emptyText.Text = text;
        _emptyText.Visibility = Visibility.Visible;
        _summaryContent.Visibility = Visibility.Collapsed;
    }

    public void SetTitleGenerationBusy(bool isBusy)
    {
        if (_generateAnotherTitleButton is not null)
        {
            _generateAnotherTitleButton.IsEnabled = !isBusy;
            _generateAnotherTitleButton.Content = isBusy
                ? _translate("Titel wird erstellt …", "Creating title …")
                : _translate("Weiteren Vorschlag", "Generate another");
        }

        if (_customTitleButton is not null)
        {
            _customTitleButton.IsEnabled = !isBusy;
        }
    }

    public void ShowProgress(
        MeetingIntelligenceProgress progress,
        string? estimatedRemainingText = null)
    {
        _emptyText.Visibility = Visibility.Collapsed;
        _threeVersionReadyPanel.Visibility = Visibility.Collapsed;
        _summaryContent.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _launchBar.Visibility = Visibility.Visible;
        _generateButton.IsEnabled = false;
        _generateThreeVersionsButton.IsEnabled = false;
        _generateButton.Content = _translate(
            "Zusammenfassung wird erstellt …",
            "Creating summary …");

        string stageText = progress.Stage switch
        {
            MeetingIntelligenceStage.DownloadingModel => _translate(
                "Lokales Sprachmodell wird heruntergeladen …",
                "Downloading local language model …"),
            MeetingIntelligenceStage.LoadingModel => _translate(
                "Lokales Sprachmodell wird geladen …",
                "Loading local language model …"),
            MeetingIntelligenceStage.AnalyzingTranscript => _translate(
                "Transkript wird lokal analysiert …",
                "Analyzing transcript locally …"),
            MeetingIntelligenceStage.SummarizingSections => _translate(
                "Inhalt und Zusammenhänge werden abschnittsweise verstanden …",
                "Understanding content and context section by section …"),
            MeetingIntelligenceStage.BuildingSummary => _translate(
                "Übergreifende Zusammenfassung wird formuliert …",
                "Writing the overall summary …"),
            MeetingIntelligenceStage.VerifyingSummary => _translate(
                "Aussagen werden gegen das Transkript geprüft …",
                "Checking claims against the transcript …"),
            MeetingIntelligenceStage.Saving => _translate(
                "Zusammenfassung wird gespeichert …",
                "Saving summary …"),
            MeetingIntelligenceStage.Completed => _translate(
                "Zusammenfassung erstellt.",
                "Summary created."),
            _ => _translate(
                "Zusammenfassung wird erstellt …",
                "Creating summary …")
        };

        _progressText.Text = FormatProgressText(
            stageText,
            progress,
            estimatedRemainingText);
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

public void ShowQueued(
        string operation,
        int position,
        bool isRunning)
    {
        _emptyText.Visibility = Visibility.Collapsed;
        _threeVersionReadyPanel.Visibility = Visibility.Collapsed;
        _summaryContent.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _launchBar.Visibility = Visibility.Visible;
        _generateButton.IsEnabled = false;
        _generateThreeVersionsButton.IsEnabled = false;
        _progressBar.IsIndeterminate = true;
        _progressText.Text = isRunning
            ? _translate(
                $"{operation} wird gestartet …",
                $"Starting {operation} …")
            : _translate(
                $"{operation} · Warteschlange · Position {position}",
                $"{operation} · queue position {position}");
    }

    public void ShowThreeVersionProgress(
        MeetingIntelligenceProgress progress,
        int versionNumber,
        string? estimatedRemainingText = null)
    {
        ShowProgress(progress, estimatedRemainingText);
        _progressText.Text = _translate(
            $"Version {versionNumber} von 3\n{_progressText.Text}",
            $"Version {versionNumber} of 3\n{_progressText.Text}");
    }

    private string FormatProgressText(
        string stageText,
        MeetingIntelligenceProgress progress,
        string? estimatedRemainingText)
    {
        if (progress.Fraction is not >= 0 or > 1)
        {
            return stageText;
        }

        string percentage = $"{progress.Fraction.Value * 100:0} %";
        int? phase = progress.Stage switch
        {
            MeetingIntelligenceStage.AnalyzingTranscript => 1,
            MeetingIntelligenceStage.SummarizingSections => 2,
            MeetingIntelligenceStage.BuildingSummary => 3,
            MeetingIntelligenceStage.VerifyingSummary => 4,
            MeetingIntelligenceStage.Saving => 5,
            MeetingIntelligenceStage.Completed => 6,
            _ => null
        };
        string details = phase is null
            ? percentage
            : _translate(
                $"Phase {phase} von 6 · {percentage}",
                $"Phase {phase} of 6 · {percentage}");

        if (progress.Stage == MeetingIntelligenceStage.DownloadingModel
            && progress.BytesPerSecond is > 0)
        {
            details +=
                $" · {progress.BytesPerSecond.Value / 1_000_000:0.0} MB/s"
                + $" · {progress.BytesPerSecond.Value * 8 / 1_000_000:0.0} Mbit/s";
        }

        if (!string.IsNullOrWhiteSpace(estimatedRemainingText))
        {
            details += $" · {estimatedRemainingText}";
        }

        return $"{stageText}\n{details}";
    }

    private FrameworkElement BuildContent()
    {
        var root = new StackPanel { Spacing = 14 };
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = _translate("Zusammenfassung", "Summary"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = _translate(
                "Lokal erstellt · Transkript bleibt auf diesem Gerät",
                "Created locally · transcript stays on this device"),
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
        root.Children.Add(header);
        root.Children.Add(BuildLaunchBar());

        _emptyText.TextWrapping = TextWrapping.Wrap;
        _emptyText.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
        root.Children.Add(_emptyText);

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

        BuildThreeVersionReadyPanel();
        root.Children.Add(_threeVersionReadyPanel);

        _summaryContent.Spacing = 18;
        _summaryContent.Visibility = Visibility.Collapsed;
        root.Children.Add(_summaryContent);
        return root;
    }

    private FrameworkElement BuildLaunchBar()
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        BuildPremiumThreeVersionsButton();
        actions.Children.Add(_generateThreeVersionsButton);

        _generateButton.Padding = new Thickness(16, 8, 16, 8);
        _generateButton.Style =
            Application.Current.Resources["AccentButtonStyle"] as Style;
        _generateButton.Click += (_, _) =>
            GenerationRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(
            _generateButton,
            _translate(
                "Lokale Zusammenfassung erstellen",
                "Create local summary"));
        actions.Children.Add(_generateButton);

        _launchBar.Padding = new Thickness(0);
        _launchBar.CornerRadius = new CornerRadius(0);
        _launchBar.Background = null;
        _launchBar.BorderThickness = new Thickness(0);
        _launchBar.Child = actions;
        return _launchBar;
    }

    private void BuildPremiumThreeVersionsButton()
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var baseGradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop
                {
                    Color = Microsoft.UI.ColorHelper.FromArgb(255, 88, 42, 246),
                    Offset = 0
                },
                new GradientStop
                {
                    Color = Microsoft.UI.ColorHelper.FromArgb(255, 114, 43, 248),
                    Offset = 0.55
                },
                new GradientStop
                {
                    Color = Microsoft.UI.ColorHelper.FromArgb(255, 133, 48, 245),
                    Offset = 1
                }
            }
        };
        var disabledGradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop
                {
                    Color = Microsoft.UI.ColorHelper.FromArgb(255, 82, 82, 86),
                    Offset = 0
                },
                new GradientStop
                {
                    Color = Microsoft.UI.ColorHelper.FromArgb(255, 91, 91, 96),
                    Offset = 1
                }
            }
        };

        var baseSurface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = baseGradient
        };

        var surfaceGrid = new Grid
        {
            MinWidth = 132,
            Height = 32
        };
        baseSurface.Child = surfaceGrid;

        var ambientGlow = new Border
        {
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = 0.24,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 71, 24, 203))
        };

        var hoverWash = new Border
        {
            CornerRadius = new CornerRadius(8),
            Opacity = 0,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop
                    {
                        Color = Microsoft.UI.ColorHelper.FromArgb(0, 255, 255, 255),
                        Offset = 0
                    },
                    new GradientStop
                    {
                        Color = Microsoft.UI.ColorHelper.FromArgb(38, 255, 255, 255),
                        Offset = 0.5
                    },
                    new GradientStop
                    {
                        Color = Microsoft.UI.ColorHelper.FromArgb(0, 255, 255, 255),
                        Offset = 1
                    }
                }
            }
        };
        surfaceGrid.Children.Add(hoverWash);

        var label = new TextBlock
        {
            Text = _translate("3 Versionen", "3 versions"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            CharacterSpacing = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 18, 13, 23))
        };
        surfaceGrid.Children.Add(label);

        var contentGrid = new Grid
        {
            MinWidth = 132,
            Height = 32
        };
        contentGrid.Children.Add(ambientGlow);
        contentGrid.Children.Add(baseSurface);

        _generateThreeVersionsButton.Content = contentGrid;
        _generateThreeVersionsButton.Padding = new Thickness(0);
        _generateThreeVersionsButton.CornerRadius = new CornerRadius(8);
        _generateThreeVersionsButton.Background = transparent;
        _generateThreeVersionsButton.Foreground = transparent;
        _generateThreeVersionsButton.BorderBrush = transparent;
        _generateThreeVersionsButton.BorderThickness = new Thickness(0);
        _generateThreeVersionsButton.HorizontalContentAlignment =
            HorizontalAlignment.Stretch;
        _generateThreeVersionsButton.VerticalContentAlignment =
            VerticalAlignment.Stretch;
        _generateThreeVersionsButton.UseSystemFocusVisuals = true;

        _generateThreeVersionsButton.Resources["ButtonBackground"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBackgroundPointerOver"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBackgroundPressed"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBackgroundDisabled"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBorderBrush"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBorderBrushPointerOver"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBorderBrushPressed"] = transparent;
        _generateThreeVersionsButton.Resources["ButtonBorderBrushDisabled"] = transparent;

        void RefreshVisualState()
        {
            bool enabled = _generateThreeVersionsButton.IsEnabled;
            baseSurface.Background = enabled ? baseGradient : disabledGradient;
            ambientGlow.Opacity = enabled ? 0.24 : 0;
            hoverWash.Opacity = 0;
            label.Foreground = enabled
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 18, 13, 23))
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 181, 181, 186));
            _generateThreeVersionsButton.Opacity = enabled ? 1 : 0.78;
        }

        _generateThreeVersionsButton.IsEnabledChanged += (_, _) => RefreshVisualState();

        var hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(18)
        };
        bool hoverIn = false;
        hoverTimer.Tick += (_, _) =>
        {
            if (!_generateThreeVersionsButton.IsEnabled)
            {
                hoverWash.Opacity = 0;
                hoverTimer.Stop();
                return;
            }

            double target = hoverIn ? 0.48 : 0;
            double delta = target - hoverWash.Opacity;
            if (Math.Abs(delta) < 0.018)
            {
                hoverWash.Opacity = target;
                hoverTimer.Stop();
                return;
            }

            hoverWash.Opacity += delta * 0.12;
        };

        _generateThreeVersionsButton.PointerEntered += (_, _) =>
        {
            if (!_generateThreeVersionsButton.IsEnabled)
            {
                return;
            }

            hoverIn = true;
            hoverTimer.Start();
        };
        _generateThreeVersionsButton.PointerExited += (_, _) =>
        {
            hoverIn = false;
            hoverTimer.Start();
        };
        _generateThreeVersionsButton.Click += (_, _) =>
            ThreeVersionGenerationRequested?.Invoke(this, EventArgs.Empty);

        RefreshVisualState();

        AutomationProperties.SetName(
            _generateThreeVersionsButton,
            _translate(
                "Drei lokale Zusammenfassungen erstellen",
                "Create three local summaries"));
        ToolTipService.SetToolTip(
            _generateThreeVersionsButton,
            _translate(
                "Drei unabhängige Varianten erzeugen und anschließend kombinieren",
                "Create three independent variants and combine them afterwards"));
    }

    private void BuildThreeVersionReadyPanel()
    {
        _threeVersionReadyPanel.Spacing = 12;
        _threeVersionReadyPanel.Visibility = Visibility.Collapsed;
        _threeVersionReadyPanel.Children.Add(new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new TextBlock
                    {
                        Text = _translate(
                            "3 Varianten sind bereit",
                            "3 variants are ready"),
                        FontSize = 17,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = _translate(
                            "Wähle jetzt deinen bevorzugten Titel, deine Kurzfassung und deine Kernpunkte aus den drei Ergebnissen aus.",
                            "Now choose your preferred title, overview and key points from the three results."),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                    }
                }
            }
        });
        _openThreeVersionsButton.Content = _translate(
            "3 Versionen vergleichen",
            "Compare 3 versions");
        _openThreeVersionsButton.Padding = new Thickness(16, 9, 16, 9);
        _openThreeVersionsButton.HorizontalAlignment = HorizontalAlignment.Left;
        _openThreeVersionsButton.Style =
            Application.Current.Resources["AccentButtonStyle"] as Style;
        _openThreeVersionsButton.Click += (_, _) =>
            OpenThreeVersionPickerRequested?.Invoke(this, EventArgs.Empty);
        _threeVersionReadyPanel.Children.Add(_openThreeVersionsButton);
    }

    private void RenderSummary(MeetingSummary summary)
    {
        _summaryContent.Children.Add(CreateSuggestedTitle(summary));
        _summaryContent.Children.Add(CreateTextSection(
            _translate("Kurzfassung", "Overview"),
            summary.Overview));
        AddListSection(
            _translate("Kernpunkte", "Key points"),
            summary.KeyPoints);
        AddListSection(
            _translate("Entscheidungen", "Decisions"),
            summary.Decisions);
        if (summary.ActionItems.Count > 0)
        {
            _summaryContent.Children.Add(CreateListSection(
                _translate("Aufgaben", "Action items"),
                summary.ActionItems.Select(item => string.IsNullOrWhiteSpace(item.Owner)
                    ? item.Description
                    : $"{item.Owner}: {item.Description}")));
        }

        _summaryContent.Children.Add(new TextBlock
        {
            Text = _translate(
                $"Lokal erstellt · {summary.GeneratedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}",
                $"Created locally · {summary.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"),
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
    }

    private FrameworkElement CreateSuggestedTitle(MeetingSummary summary)
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

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock
        {
            Text = _translate("Titelvorschlag", "Suggested title"),
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
        text.Children.Add(new TextBlock
        {
            Text = summary.SuggestedTitle,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        grid.Children.Add(text);

        if (!_isReadOnly)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            var apply = new Button
            {
                Content = _translate("Als Titel übernehmen", "Use as title")
            };
            apply.Click += (_, _) =>
                ApplySuggestedTitleRequested?.Invoke(summary.SuggestedTitle);
            actions.Children.Add(apply);

            _generateAnotherTitleButton = new Button
            {
                Content = _translate("Weiteren Vorschlag", "Generate another")
            };
            _generateAnotherTitleButton.Click += (_, _) =>
                GenerateAnotherTitleRequested?.Invoke(this, EventArgs.Empty);
            actions.Children.Add(_generateAnotherTitleButton);

            _customTitleButton = new Button
            {
                Content = _translate("Eigener Titel", "Custom title")
            };
            _customTitleButton.Click += (_, _) =>
                CustomTitleRequested?.Invoke(this, EventArgs.Empty);
            actions.Children.Add(_customTitleButton);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
        }

        return new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            Child = grid
        };
    }

    private FrameworkElement CreateTextSection(string heading, string content)
    {
        var stack = CreateSectionHeading(heading);
        stack.Children.Add(new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });
        return stack;
    }

    private void AddListSection(string heading, IReadOnlyList<string> items)
    {
        if (items.Count > 0)
        {
            _summaryContent.Children.Add(CreateListSection(heading, items));
        }
    }

    private FrameworkElement CreateListSection(string heading, IEnumerable<string> items)
    {
        var stack = CreateSectionHeading(heading);
        foreach (string item in items)
        {
            var row = new Grid { ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.Children.Add(new TextBlock
            {
                Text = "•",
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush")
            });
            var itemText = new TextBlock
            {
                Text = item,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(itemText, 1);
            row.Children.Add(itemText);
            stack.Children.Add(row);
        }

        return stack;
    }

    private static StackPanel CreateSectionHeading(string heading)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        return stack;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Brush
            : null;
}