using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private readonly List<ProcessingQueueItem> _processingQueue = [];
    private ProcessingQueueItem? _activeProcessingJob;
    private bool _processingQueueRunnerActive;
    private bool _processingQueueIsShuttingDown;
    private Flyout? _processingQueueFlyout;

    private bool HasProcessingJobs =>
        _activeProcessingJob is not null || _processingQueue.Count > 0;

    private ProcessingJobHandle EnqueueProcessingJob(
        ProcessingJobKind kind,
        Guid conversationId,
        string conversationTitle,
        string displayName,
        Func<CancellationToken, Task<ProcessingJobResult>> work,
        bool allowDuplicate = false,
        Action? canceledBeforeStart = null)
    {
        if (!allowDuplicate)
        {
            ProcessingQueueItem? existing = FindProcessingJob(kind, conversationId);
            if (existing is not null)
            {
                return new ProcessingJobHandle(existing.Id, existing.Completion.Task);
            }
        }

        if (_processingQueueIsShuttingDown)
        {
            return new ProcessingJobHandle(
                Guid.Empty,
                Task.FromResult(ProcessingJobResult.Canceled));
        }

        var item = new ProcessingQueueItem(
            Guid.NewGuid(),
            kind,
            conversationId,
            conversationTitle,
            displayName,
            new CancellationTokenSource(),
            work,
            canceledBeforeStart);
        _processingQueue.Add(item);
        UpdateProcessingQueueUi();
        _ = RunProcessingQueueAsync();
        return new ProcessingJobHandle(item.Id, item.Completion.Task);
    }

    private async Task RunProcessingQueueAsync()
    {
        if (_processingQueueRunnerActive)
        {
            return;
        }

        _processingQueueRunnerActive = true;
        try
        {
            while (!_processingQueueIsShuttingDown && _processingQueue.Count > 0)
            {
                ProcessingQueueItem item = _processingQueue[0];
                _processingQueue.RemoveAt(0);

                if (item.Cancellation.IsCancellationRequested)
                {
                    CompletePendingCanceledJob(item);
                    continue;
                }

                _activeProcessingJob = item;
                UpdateProcessingQueueUi();

                ProcessingJobResult result;
                try
                {
                    result = await item.Work(item.Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    result = ProcessingJobResult.Canceled;
                }
                catch (Exception exception)
                {
                    result = ProcessingJobResult.Failed;
                    if (!_processingQueueIsShuttingDown)
                    {
                        await ShowMessageAsync(
                            Translate("Verarbeitung fehlgeschlagen", "Processing failed"),
                            exception.Message,
                            Translate("Schließen", "Close"));
                    }
                }

                item.Completion.TrySetResult(result);
                item.Cancellation.Dispose();
                _activeProcessingJob = null;

                if (!_processingQueueIsShuttingDown)
                {
                    UpdateProcessingQueueUi();
                }
            }
        }
        finally
        {
            _processingQueueRunnerActive = false;
            if (!_processingQueueIsShuttingDown)
            {
                UpdateProcessingQueueUi();
                if (_processingQueue.Count > 0)
                {
                    _ = RunProcessingQueueAsync();
                }
            }
        }
    }

    private ProcessingQueueItem? FindProcessingJob(
        ProcessingJobKind kind,
        Guid conversationId)
    {
        if (_activeProcessingJob is
            {
                Kind: var activeKind,
                ConversationId: var activeConversationId
            }
            && activeKind == kind
            && activeConversationId == conversationId)
        {
            return _activeProcessingJob;
        }

        return _processingQueue.FirstOrDefault(item =>
            item.Kind == kind && item.ConversationId == conversationId);
    }

    private bool TryGetProcessingJobState(
        Guid conversationId,
        out ProcessingJobViewState state,
        params ProcessingJobKind[] kinds)
    {
        bool Matches(ProcessingQueueItem item) =>
            item.ConversationId == conversationId && kinds.Contains(item.Kind);

        int matchingJobs =
            (_activeProcessingJob is not null && Matches(_activeProcessingJob) ? 1 : 0)
            + _processingQueue.Count(Matches);

        if (_activeProcessingJob is not null && Matches(_activeProcessingJob))
        {
            state = new ProcessingJobViewState(
                _activeProcessingJob.Kind,
                true,
                0,
                matchingJobs);
            return true;
        }

        for (int index = 0; index < _processingQueue.Count; index++)
        {
            ProcessingQueueItem item = _processingQueue[index];
            if (Matches(item))
            {
                state = new ProcessingJobViewState(
                    item.Kind,
                    false,
                    index + 1,
                    matchingJobs);
                return true;
            }
        }

        state = default;
        return false;
    }

    private bool HasProcessingJobForConversation(Guid conversationId) =>
        _activeProcessingJob?.ConversationId == conversationId
        || _processingQueue.Any(item => item.ConversationId == conversationId);

    private bool CancelNextProcessingJob(
        Guid conversationId,
        params ProcessingJobKind[] kinds)
    {
        bool Matches(ProcessingQueueItem item) =>
            item.ConversationId == conversationId && kinds.Contains(item.Kind);

        if (_activeProcessingJob is not null && Matches(_activeProcessingJob))
        {
            _activeProcessingJob.Cancellation.Cancel();
            UpdateProcessingQueueUi();
            return true;
        }

        int index = _processingQueue.FindIndex(item => Matches(item));
        if (index < 0)
        {
            return false;
        }

        ProcessingQueueItem queued = _processingQueue[index];
        _processingQueue.RemoveAt(index);
        CompletePendingCanceledJob(queued);
        UpdateProcessingQueueUi();
        return true;
    }

private bool CancelProcessingJob(Guid jobId)
    {
        if (_activeProcessingJob?.Id == jobId)
        {
            if (!_activeProcessingJob.Cancellation.IsCancellationRequested)
            {
                _activeProcessingJob.Cancellation.Cancel();
            }

            UpdateProcessingQueueUi();
            return true;
        }

        int index = _processingQueue.FindIndex(item => item.Id == jobId);
        if (index < 0)
        {
            return false;
        }

        ProcessingQueueItem queued = _processingQueue[index];
        _processingQueue.RemoveAt(index);
        CompletePendingCanceledJob(queued);
        UpdateProcessingQueueUi();
        return true;
    }

    private void CompletePendingCanceledJob(ProcessingQueueItem item)
    {
        item.Cancellation.Cancel();
        try
        {
            item.CanceledBeforeStart?.Invoke();
        }
        finally
        {
            item.Completion.TrySetResult(ProcessingJobResult.Canceled);
            item.Cancellation.Dispose();
        }
    }

    private void CancelWaitingProcessingJobs()
    {
        ProcessingQueueItem[] waiting = _processingQueue.ToArray();
        _processingQueue.Clear();
        foreach (ProcessingQueueItem item in waiting)
        {
            CompletePendingCanceledJob(item);
        }

        UpdateProcessingQueueUi();
    }

    private void CancelAllProcessingJobs()
    {
        _processingQueueIsShuttingDown = true;
        _activeProcessingJob?.Cancellation.Cancel();

        ProcessingQueueItem[] waiting = _processingQueue.ToArray();
        _processingQueue.Clear();
        foreach (ProcessingQueueItem item in waiting)
        {
            CompletePendingCanceledJob(item);
        }
    }

    private void SetProcessingStatus(string status)
    {
        if (_isBusy || _nativeRecordingService?.IsRecording == true)
        {
            return;
        }

        StatusText.Text = status;
    }

    private void ClearProcessingStatus()
    {
        if (_isBusy || _nativeRecordingService?.IsRecording == true)
        {
            return;
        }

        StatusText.Text = string.Empty;
    }

    private void UpdateProcessingQueueUi()
    {
        int waitingCount = _processingQueue.Count;
        bool hasActive = _activeProcessingJob is not null;
        int totalCount = waitingCount + (hasActive ? 1 : 0);

        ProcessingQueueButton.Visibility = Visibility.Visible;
        ProcessingQueueButton.Content = Translate(
            $"Warteschlange · {totalCount}",
            $"Queue · {totalCount}");
        ProcessingQueueButton.Opacity = totalCount == 0 ? 0.62 : 1;
        ProcessingQueueButton.Foreground = GetThemeBrush(
            totalCount == 0
                ? "TextFillColorSecondaryBrush"
                : "TextFillColorPrimaryBrush");
        ProcessingQueueButton.Background = GetThemeBrush(
            totalCount == 0
                ? "ControlFillColorSecondaryBrush"
                : "SubtleFillColorSecondaryBrush");
        ProcessingQueueButton.BorderBrush = GetThemeBrush(
            totalCount == 0
                ? "ControlStrokeColorDefaultBrush"
                : "ControlStrokeColorSecondaryBrush");

        string tooltip = totalCount == 0
            ? Translate(
                "0 Aufträge in der Warteschlange",
                "0 jobs in the queue")
            : hasActive
                ? Translate(
                    $"{totalCount} Aufträge · aktiv: {_activeProcessingJob!.DisplayName} · {waitingCount} wartend",
                    $"{totalCount} jobs · active: {_activeProcessingJob!.DisplayName} · {waitingCount} waiting")
                : Translate(
                    $"{waitingCount} Aufträge warten",
                    $"{waitingCount} jobs waiting");
        ToolTipService.SetToolTip(ProcessingQueueButton, tooltip);

        if (totalCount == 0)
        {
            ClearProcessingStatus();
        }

        if (_processingQueueFlyout is not null)
        {
            _processingQueueFlyout.Content =
                CreateProcessingQueueFlyoutContent(_processingQueueFlyout);
        }

        RefreshSelectedProcessingPanels();
    }

    private void RefreshSelectedProcessingPanels()
    {
        RenderSelectedTranscript();
        RenderSelectedSummary();
        RenderSelectedConversationQuestions();
    }

    private void ProcessingQueueButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _processingQueueFlyout?.Hide();

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter
        {
            Property = ScrollViewer.HorizontalScrollModeProperty,
            Value = ScrollMode.Disabled
        });
        presenterStyle.Setters.Add(new Setter
        {
            Property = ScrollViewer.HorizontalScrollBarVisibilityProperty,
            Value = ScrollBarVisibility.Disabled
        });

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = presenterStyle
        };
        flyout.Content = CreateProcessingQueueFlyoutContent(flyout);
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_processingQueueFlyout, flyout))
            {
                _processingQueueFlyout = null;
            }
        };

        _processingQueueFlyout = flyout;
        flyout.ShowAt(ProcessingQueueButton);
    }

private FrameworkElement CreateProcessingQueueFlyoutContent(
        Flyout owner)
    {
        double availableWidth = RootGrid.ActualWidth > 0
            ? RootGrid.ActualWidth - 140
            : 400;
        double flyoutWidth = Math.Clamp(
            availableWidth,
            300,
            440);

        var root = new StackPanel
        {
            Width = flyoutWidth,
            Spacing = 12
        };

        var header = new Grid
        {
            ColumnSpacing = 16
        };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var headerText = new StackPanel
        {
            Spacing = 2
        };
        headerText.Children.Add(new TextBlock
        {
            Text = Translate(
                "Verarbeitungswarteschlange",
                "Processing queue"),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        headerText.Children.Add(new TextBlock
        {
            Text = BuildProcessingQueueSummary(),
            FontSize = 12,
            Foreground = GetThemeBrush(
                "TextFillColorSecondaryBrush")
        });
        header.Children.Add(headerText);

        int waitingCount = _processingQueue.Count;
        if (waitingCount > 1)
        {
            var cancelWaitingButton = new Button
            {
                Content = Translate(
                    "Wartende leeren",
                    "Clear waiting"),
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Background = GetThemeBrush(
                    "SubtleFillColorTransparentBrush"),
                BorderThickness = new Thickness(0),
                Foreground = GetThemeBrush(
                    "TextFillColorSecondaryBrush")
            };
            cancelWaitingButton.Click += (_, _) =>
            {
                CancelWaitingProcessingJobs();
                if (_processingQueue.Count == 0
                    && _activeProcessingJob is null)
                {
                    owner.Hide();
                }
            };
            Grid.SetColumn(cancelWaitingButton, 1);
            header.Children.Add(cancelWaitingButton);
        }

        root.Children.Add(header);
        root.Children.Add(new Border
        {
            Height = 1,
            Background = GetThemeBrush(
                "DividerStrokeColorDefaultBrush")
        });

        var jobs = new StackPanel
        {
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        int rowIndex = 0;
        if (_activeProcessingJob is not null)
        {
            jobs.Children.Add(CreateProcessingQueueRow(
                _activeProcessingJob,
                isActive: true,
                queuePosition: 0,
                rowIndex++,
                owner));
        }

        for (int index = 0; index < _processingQueue.Count; index++)
        {
            jobs.Children.Add(CreateProcessingQueueRow(
                _processingQueue[index],
                isActive: false,
                queuePosition: index + 1,
                rowIndex++,
                owner));
        }

        if (rowIndex == 0)
        {
            root.Children.Add(CreateEmptyProcessingQueueState());
            return root;
        }

        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 430,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility =
                ScrollBarVisibility.Disabled,
            HorizontalContentAlignment =
                HorizontalAlignment.Stretch,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility =
                ScrollBarVisibility.Auto,
            Content = jobs
        });

        return root;
    }

    private Border CreateProcessingQueueRow(
        ProcessingQueueItem item,
        bool isActive,
        int queuePosition,
        int rowIndex,
        Flyout owner)
    {
        var row = new Grid
        {
            ColumnSpacing = 11
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var stateDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = GetThemeBrush(
                isActive
                    ? "AccentFillColorDefaultBrush"
                    : "ControlStrongFillColorDefaultBrush"),
            Opacity = isActive ? 1 : 0.62
        };
        row.Children.Add(stateDot);

        var text = new StackPanel
        {
            Spacing = 2,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = item.ConversationTitle,
            FontSize = 12,
            Foreground = GetThemeBrush(
                "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = isActive
                ? item.Cancellation.IsCancellationRequested
                    ? Translate(
                        "Wird abgebrochen …",
                        "Cancelling …")
                    : Translate(
                        "Läuft gerade",
                        "Running")
                : Translate(
                    $"Wartet · Position {queuePosition}",
                    $"Waiting · position {queuePosition}"),
            FontSize = 11,
            Foreground = GetThemeBrush(
                isActive
                    ? "AccentTextFillColorPrimaryBrush"
                    : "TextFillColorTertiaryBrush")
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var cancelButton = new Button
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = GetThemeBrush(
                "SubtleFillColorTransparentBrush"),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !isActive
                || !item.Cancellation.IsCancellationRequested,
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 12,
                Foreground = GetThemeBrush(
                    "SystemFillColorCriticalBrush")
            }
        };
        ToolTipService.SetToolTip(
            cancelButton,
            isActive
                ? Translate(
                    "Diesen laufenden Auftrag abbrechen",
                    "Cancel this running job")
                : Translate(
                    "Diesen wartenden Auftrag entfernen",
                    "Remove this waiting job"));
        cancelButton.Click += (_, _) =>
        {
            CancelProcessingJob(item.Id);
            owner.Content =
                CreateProcessingQueueFlyoutContent(owner);
        };
        Grid.SetColumn(cancelButton, 2);
        row.Children.Add(cancelButton);

        return new Border
        {
            Padding = new Thickness(12, 10, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = GetThemeBrush(
                rowIndex % 2 == 0
                    ? "CardBackgroundFillColorDefaultBrush"
                    : "ControlFillColorSecondaryBrush"),
            BorderBrush = GetThemeBrush(
                "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = row
        };
    }

    private Border CreateEmptyProcessingQueueState()
    {
        var content = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE8EE",
            FontSize = 18,
            Opacity = 0.42,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = Translate(
                "Keine Aufträge",
                "No jobs"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = Translate(
                "Neue Aufträge erscheinen hier in ihrer Reihenfolge.",
                "New jobs appear here in processing order."),
            FontSize = 11,
            Foreground = GetThemeBrush(
                "TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return new Border
        {
            Padding = new Thickness(18, 22, 18, 20),
            CornerRadius = new CornerRadius(10),
            Background = GetThemeBrush(
                "ControlFillColorSecondaryBrush"),
            Child = content
        };
    }

    private string BuildProcessingQueueSummary()
    {
        int waitingCount = _processingQueue.Count;
        if (_activeProcessingJob is null)
        {
            return waitingCount == 0
                ? Translate(
                    "0 Aufträge",
                    "0 jobs")
                : Translate(
                    $"{waitingCount} wartend",
                    $"{waitingCount} waiting");
        }

        return waitingCount == 0
            ? Translate(
                "1 aktiv · nichts wartet",
                "1 active · nothing waiting")
            : Translate(
                $"1 aktiv · {waitingCount} wartend",
                $"1 active · {waitingCount} waiting");
    }

    private async Task RefreshConversationsPreservingSelectionAsync(
        Guid fallbackConversationId)
    {
        Guid? selectedConversationId =
            (ConversationsList.SelectedItem as ConversationListItem)
            ?.Conversation.Id;
        await LoadConversationsAsync(selectedConversationId ?? fallbackConversationId);
    }

    private enum ProcessingJobKind
    {
        Transcription,
        Summary,
        ThreeVersionSummary,
        TitleSuggestion,
        Question
    }

    private enum ProcessingJobResult
    {
        Completed,
        Canceled,
        Failed
    }

    private readonly record struct ProcessingJobHandle(
        Guid Id,
        Task<ProcessingJobResult> Completion);

    private readonly record struct ProcessingJobViewState(
        ProcessingJobKind Kind,
        bool IsActive,
        int Position,
        int MatchingJobs);

    private sealed class ProcessingQueueItem
    {
        public ProcessingQueueItem(
            Guid id,
            ProcessingJobKind kind,
            Guid conversationId,
            string conversationTitle,
            string displayName,
            CancellationTokenSource cancellation,
            Func<CancellationToken, Task<ProcessingJobResult>> work,
            Action? canceledBeforeStart)
        {
            Id = id;
            Kind = kind;
            ConversationId = conversationId;
            ConversationTitle = conversationTitle;
            DisplayName = displayName;
            Cancellation = cancellation;
            Work = work;
            CanceledBeforeStart = canceledBeforeStart;
        }

        public Guid Id { get; }
        public ProcessingJobKind Kind { get; }
        public Guid ConversationId { get; }
        public string ConversationTitle { get; }
        public string DisplayName { get; }
        public CancellationTokenSource Cancellation { get; }
        public Func<CancellationToken, Task<ProcessingJobResult>> Work { get; }
        public Action? CanceledBeforeStart { get; }
        public TaskCompletionSource<ProcessingJobResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
