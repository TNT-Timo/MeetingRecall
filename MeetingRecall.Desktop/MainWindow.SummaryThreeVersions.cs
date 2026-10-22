using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Intelligence;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private const int ThreeVersionCount = 3;
    private readonly Dictionary<Guid, ThreeVersionDraftSet> _pendingThreeVersionDrafts = [];

    private bool TryShowPendingThreeVersionDrafts(Conversation? conversation)
    {
        if (conversation is null
            || !_pendingThreeVersionDrafts.ContainsKey(conversation.Id))
        {
            return false;
        }

        _meetingSummaryPanel?.ShowThreeVersionDraftsReady(conversation);
        return true;
    }

    private void DiscardPendingThreeVersionDrafts(Guid conversationId)
    {
        _pendingThreeVersionDrafts.Remove(conversationId);
    }

    private async void MeetingSummaryPanel_ThreeVersionGenerationRequested(
        object? sender,
        EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem)
        {
            return;
        }

        Conversation conversation = selectedItem.Conversation;
        if (conversation.TranscriptSegments.Count == 0
            || !await ConfirmThreeVersionGenerationAsync())
        {
            return;
        }

        if (!MeetingIntelligenceService.IsModelInstalled
            && !await ConfirmLanguageModelDownloadAsync())
        {
            return;
        }

        ProcessingJobHandle handle = EnqueueProcessingJob(
            ProcessingJobKind.ThreeVersionSummary,
            conversation.Id,
            conversation.Title,
            Translate(
                "Drei Zusammenfassungen",
                "Three summaries"),
            cancellationToken => RunThreeVersionGenerationJobAsync(
                conversation,
                cancellationToken));

        await handle.Completion;
    }

    private async Task<ProcessingJobResult> RunThreeVersionGenerationJobAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        MeetingSummary? previousSummary = await MeetingSummaryRepository
            .GetByConversationIdAsync(conversation.Id);
        var drafts = new List<MeetingSummary>(ThreeVersionCount);
        bool repositoryRestored = false;

        try
        {
            for (int index = 0; index < ThreeVersionCount; index++)
            {
                int versionNumber = index + 1;
                ResetMeetingIntelligenceEta();
                var progress = new Progress<MeetingIntelligenceProgress>(item =>
                    UpdateThreeVersionGenerationProgress(
                        conversation.Id,
                        item,
                        versionNumber));

                if ((ConversationsList.SelectedItem as ConversationListItem)
                        ?.Conversation.Id == conversation.Id)
                {
                    _meetingSummaryPanel?.ShowThreeVersionProgress(
                        new MeetingIntelligenceProgress(
                            MeetingIntelligenceStage.AnalyzingTranscript,
                            0),
                        versionNumber);
                }

                MeetingSummary draft = await MeetingIntelligenceService
                    .GenerateSummaryAsync(
                        conversation.Id,
                        progress,
                        cancellationToken);
                drafts.Add(draft);
            }

            await RestoreSummarySnapshotAsync(
                conversation.Id,
                previousSummary,
                cancellationToken);
            repositoryRestored = true;

            _pendingThreeVersionDrafts[conversation.Id] =
                new ThreeVersionDraftSet(drafts.ToArray());
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                _meetingSummaryPanel?.ShowThreeVersionDraftsReady(conversation);
            }
            return ProcessingJobResult.Completed;
        }
        catch (OperationCanceledException)
        {
            await RestoreSummarySnapshotBestEffortAsync(
                conversation.Id,
                previousSummary);
            repositoryRestored = true;
            return ProcessingJobResult.Canceled;
        }
        catch (Exception exception)
        {
            await RestoreSummarySnapshotBestEffortAsync(
                conversation.Id,
                previousSummary);
            repositoryRestored = true;
            await ShowMessageAsync(
                Translate(
                    "3-Versionen-Erstellung fehlgeschlagen",
                    "Three-version generation failed"),
                exception.Message,
                Translate("Schließen", "Close"));
            return ProcessingJobResult.Failed;
        }
        finally
        {
            if (!repositoryRestored)
            {
                await RestoreSummarySnapshotBestEffortAsync(
                    conversation.Id,
                    previousSummary);
            }

            ResetMeetingIntelligenceEta(stop: true);
            ClearProcessingStatus();
            if ((ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id == conversation.Id)
            {
                RenderSelectedSummary();
            }
        }
    }

    private async void MeetingSummaryPanel_OpenThreeVersionPickerRequested(
        object? sender,
        EventArgs e)
    {
        if (_isBusy
            || ConversationsList.SelectedItem
                is not ConversationListItem selectedItem
            || !_pendingThreeVersionDrafts.TryGetValue(
                selectedItem.Conversation.Id,
                out ThreeVersionDraftSet? draftSet))
        {
            return;
        }

        await ShowThreeVersionPickerAsync(
            selectedItem.Conversation,
            draftSet);
    }

    private async Task<bool> ConfirmThreeVersionGenerationAsync()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = Translate(
                "MeetingRecall erstellt das komplette Ergebnis dreimal unabhängig voneinander. Das dauert ungefähr dreimal so lange wie eine normale Zusammenfassung.",
                "MeetingRecall will create the complete result three independent times. This takes roughly three times as long as a normal summary."),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = Translate(
                "Danach kannst du Titel, Kurzfassung und Kernpunkte aus den drei Varianten frei kombinieren und erst dann endgültig speichern.",
                "Afterwards you can freely combine the title, overview and key points from all three variants before saving the final result."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate(
                "3 Versionen erstellen?",
                "Create 3 versions?"),
            Content = body,
            PrimaryButtonText = Translate(
                "3 Versionen erstellen",
                "Create 3 versions"),
            CloseButtonText = Translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void UpdateThreeVersionGenerationProgress(
        Guid conversationId,
        MeetingIntelligenceProgress progress,
        int versionNumber)
    {
        string? estimatedRemaining = FormatMeetingIntelligenceEta(progress);
        if ((ConversationsList.SelectedItem as ConversationListItem)
                ?.Conversation.Id == conversationId)
        {
            _meetingSummaryPanel?.ShowThreeVersionProgress(
                progress,
                versionNumber,
                estimatedRemaining);
        }

        string status = Translate(
            $"Version {versionNumber} von {ThreeVersionCount} wird erstellt …",
            $"Creating version {versionNumber} of {ThreeVersionCount} …");
        if (!string.IsNullOrWhiteSpace(estimatedRemaining))
        {
            status += $" · {estimatedRemaining}";
        }

        SetProcessingStatus(status);
    }

    private async Task RestoreSummarySnapshotAsync(
        Guid conversationId,
        MeetingSummary? previousSummary,
        CancellationToken cancellationToken)
    {
        if (previousSummary is not null)
        {
            await MeetingSummaryRepository.UpsertAsync(
                previousSummary,
                cancellationToken);
        }
        else
        {
            await MeetingSummaryRepository.DeleteAsync(
                conversationId,
                cancellationToken);
        }
    }

    private async Task RestoreSummarySnapshotBestEffortAsync(
        Guid conversationId,
        MeetingSummary? previousSummary)
    {
        try
        {
            await RestoreSummarySnapshotAsync(
                conversationId,
                previousSummary,
                CancellationToken.None);
        }
        catch
        {
            // Drafts are intentionally temporary. A restore failure must not
            // hide the original generation/cancellation error.
        }
    }

    private async Task ShowThreeVersionPickerAsync(
        Conversation conversation,
        ThreeVersionDraftSet draftSet)
    {
        var overlay = new Grid
        {
            Padding = new Thickness(30, 24, 30, 28),
            Background = GetThemeBrush("SolidBackgroundFillColorBaseBrush")
                ?? GetThemeBrush("ApplicationPageBackgroundThemeBrush")
                ?? new SolidColorBrush(Colors.Black)
        };
        overlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overlay.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        Canvas.SetZIndex(overlay, 10_000);

        var headingStack = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 20)
        };
        headingStack.Children.Add(new TextBlock
        {
            Text = Translate(
                "3 Versionen vergleichen",
                "Compare 3 versions"),
            FontSize = 27,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        headingStack.Children.Add(new TextBlock
        {
            Text = Translate(
                "Wähle jeweils einen Titel, eine Kurzfassung und einen Kernpunkte-Block. Die drei Bereiche dürfen aus unterschiedlichen Versionen stammen.",
                "Choose one title, one overview and one key-points block. The three sections may come from different versions."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
        });
        var selectionStatus = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12,
            Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
        };
        headingStack.Children.Add(selectionStatus);
        overlay.Children.Add(headingStack);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var versionsGrid = new Grid { ColumnSpacing = 16 };
        for (int i = 0; i < ThreeVersionCount; i++)
        {
            versionsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }
        body.Children.Add(versionsGrid);

        var laterButton = new Button
        {
            Content = Translate("Später auswählen", "Choose later"),
            Padding = new Thickness(15, 9, 15, 9)
        };
        var finalButton = new Button
        {
            Content = Translate("Endgültig übernehmen", "Use final combination"),
            Padding = new Thickness(17, 9, 17, 9),
            IsEnabled = false,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style
        };
        var footerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 18),
            Children =
            {
                laterButton,
                finalButton
            }
        };
        Grid.SetRow(footerActions, 1);
        body.Children.Add(footerActions);

        var bodyScroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(bodyScroll, 1);
        overlay.Children.Add(bodyScroll);

        var titleButtons = new Button[ThreeVersionCount];
        var overviewButtons = new Button[ThreeVersionCount];
        var keyPointButtons = new Button[ThreeVersionCount];
        var choiceSurfaces = new Dictionary<Button, Border>();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Button CreateChoiceButton(
            int versionIndex,
            ThreeVersionComponent component,
            FrameworkElement content)
        {
            var surface = new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = GetThemeBrush("ControlStrokeColorDefaultBrush"),
                Background = GetThemeBrush("ControlFillColorSecondaryBrush"),
                Child = content
            };

            var transparent = new SolidColorBrush(Colors.Transparent);
            var button = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                BorderBrush = transparent,
                Background = transparent,
                Content = surface,
                UseSystemFocusVisuals = true
            };

            // The visible state is rendered by the inner Border. Keeping all
            // native Button pointer states transparent prevents WinUI from
            // delaying green/grey updates until PointerExited.
            button.Resources["ButtonBackground"] = transparent;
            button.Resources["ButtonBackgroundPointerOver"] = transparent;
            button.Resources["ButtonBackgroundPressed"] = transparent;
            button.Resources["ButtonBackgroundDisabled"] = transparent;
            button.Resources["ButtonBorderBrush"] = transparent;
            button.Resources["ButtonBorderBrushPointerOver"] = transparent;
            button.Resources["ButtonBorderBrushPressed"] = transparent;
            button.Resources["ButtonBorderBrushDisabled"] = transparent;
            choiceSurfaces[button] = surface;

            button.Click += (_, _) =>
            {
                switch (component)
                {
                    case ThreeVersionComponent.Title:
                        draftSet.SelectedTitleVersion =
                            draftSet.SelectedTitleVersion == versionIndex
                                ? null
                                : versionIndex;
                        break;
                    case ThreeVersionComponent.Overview:
                        draftSet.SelectedOverviewVersion =
                            draftSet.SelectedOverviewVersion == versionIndex
                                ? null
                                : versionIndex;
                        break;
                    case ThreeVersionComponent.KeyPoints:
                        draftSet.SelectedKeyPointsVersion =
                            draftSet.SelectedKeyPointsVersion == versionIndex
                                ? null
                                : versionIndex;
                        break;
                }

                // This updates the actual visible surface synchronously while
                // the pointer is still over the control.
                RefreshSelectionVisuals();
            };
            return button;
        }

        StackPanel CreateTextChoiceContent(string heading, string text)
        {
            var stack = new StackPanel { Spacing = 7 };
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
            });
            stack.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            });
            return stack;
        }

        StackPanel CreateKeyPointChoiceContent(MeetingSummary summary)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = Translate("Kernpunkte", "Key points"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
            });
            foreach (string item in summary.KeyPoints)
            {
                var row = new Grid { ColumnSpacing = 7 };
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
                    Foreground = GetThemeBrush("AccentTextFillColorPrimaryBrush")
                });
                var text = new TextBlock
                {
                    Text = item,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                stack.Children.Add(row);
            }
            return stack;
        }

        for (int index = 0; index < ThreeVersionCount; index++)
        {
            MeetingSummary summary = draftSet.Versions[index];
            var versionStack = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(0, 0, 0, 4)
            };

            // Header only: deliberately not a Button, so it cannot be mistaken
            // for another selectable section.
            versionStack.Children.Add(new Border
            {
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 31, 92, 156)),
                BorderThickness = new Thickness(0),
                Child = new TextBlock
                {
                    Text = Translate(
                        $"Version {index + 1}",
                        $"Version {index + 1}"),
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                }
            });

            titleButtons[index] = CreateChoiceButton(
                index,
                ThreeVersionComponent.Title,
                CreateTextChoiceContent(
                    Translate("Titel", "Title"),
                    summary.SuggestedTitle));
            overviewButtons[index] = CreateChoiceButton(
                index,
                ThreeVersionComponent.Overview,
                CreateTextChoiceContent(
                    Translate("Kurzfassung", "Overview"),
                    summary.Overview));
            keyPointButtons[index] = CreateChoiceButton(
                index,
                ThreeVersionComponent.KeyPoints,
                CreateKeyPointChoiceContent(summary));

            versionStack.Children.Add(titleButtons[index]);
            versionStack.Children.Add(overviewButtons[index]);
            versionStack.Children.Add(keyPointButtons[index]);

            Grid.SetColumn(versionStack, index);
            versionsGrid.Children.Add(versionStack);
        }

        void RefreshButton(Button button, bool selected)
        {
            Border surface = choiceSurfaces[button];
            if (selected)
            {
                surface.Background = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 21, 107, 56));
                surface.BorderBrush = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(255, 74, 222, 128));
                surface.BorderThickness = new Thickness(2);
            }
            else
            {
                surface.Background =
                    GetThemeBrush("ControlFillColorSecondaryBrush")
                    ?? new SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58));
                surface.BorderBrush =
                    GetThemeBrush("ControlStrokeColorDefaultBrush")
                    ?? new SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(255, 80, 80, 80));
                surface.BorderThickness = new Thickness(1);
            }
        }

        void RefreshSelectionVisuals()
        {
            for (int index = 0; index < ThreeVersionCount; index++)
            {
                RefreshButton(
                    titleButtons[index],
                    draftSet.SelectedTitleVersion == index);
                RefreshButton(
                    overviewButtons[index],
                    draftSet.SelectedOverviewVersion == index);
                RefreshButton(
                    keyPointButtons[index],
                    draftSet.SelectedKeyPointsVersion == index);
            }

            int selectedCount =
                (draftSet.SelectedTitleVersion is not null ? 1 : 0)
                + (draftSet.SelectedOverviewVersion is not null ? 1 : 0)
                + (draftSet.SelectedKeyPointsVersion is not null ? 1 : 0);
            selectionStatus.Text = Translate(
                $"{selectedCount} von 3 Bereichen ausgewählt",
                $"{selectedCount} of 3 sections selected");
            finalButton.IsEnabled = selectedCount == 3;
        }

        void CloseWorkspace(bool accepted)
        {
            RootGrid.Children.Remove(overlay);
            completion.TrySetResult(accepted);
        }

        laterButton.Click += (_, _) => CloseWorkspace(false);
        finalButton.Click += async (_, _) =>
        {
            if (draftSet.SelectedTitleVersion is not int titleIndex
                || draftSet.SelectedOverviewVersion is not int overviewIndex
                || draftSet.SelectedKeyPointsVersion is not int keyPointsIndex)
            {
                return;
            }

            laterButton.IsEnabled = false;
            finalButton.IsEnabled = false;
            try
            {
                MeetingSummary titleSource = draftSet.Versions[titleIndex];
                MeetingSummary overviewSource = draftSet.Versions[overviewIndex];
                MeetingSummary keyPointSource = draftSet.Versions[keyPointsIndex];
                MeetingSummary finalSummary = MeetingSummary.Create(
                    conversation.Id,
                    titleSource.SuggestedTitle,
                    overviewSource.Overview,
                    keyPointSource.KeyPoints,
                    keyPointSource.Decisions,
                    keyPointSource.ActionItems,
                    keyPointSource.SourceTranscriptUpdatedAtUtc);

                await MeetingSummaryRepository.UpsertAsync(finalSummary);
                _pendingThreeVersionDrafts.Remove(conversation.Id);

                if ((ConversationsList.SelectedItem as ConversationListItem)
                        ?.Conversation.Id == conversation.Id)
                {
                    _meetingSummaryPanel?.ShowConversation(
                        conversation,
                        finalSummary);
                }

                StatusText.Text = string.Empty;
                CloseWorkspace(true);
            }
            catch (Exception exception)
            {
                laterButton.IsEnabled = true;
                RefreshSelectionVisuals();
                await ShowMessageAsync(
                    Translate(
                        "Auswahl konnte nicht gespeichert werden",
                        "Selection could not be saved"),
                    exception.Message,
                    Translate("Schließen", "Close"));
            }
        };

        RefreshSelectionVisuals();
        RootGrid.Children.Add(overlay);
        await completion.Task;
    }

    private enum ThreeVersionComponent
    {
        Title,
        Overview,
        KeyPoints
    }

    private sealed class ThreeVersionDraftSet(MeetingSummary[] versions)
    {
        public MeetingSummary[] Versions { get; } = versions;
        public int? SelectedTitleVersion { get; set; }
        public int? SelectedOverviewVersion { get; set; }
        public int? SelectedKeyPointsVersion { get; set; }
    }
}
