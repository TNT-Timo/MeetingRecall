using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Desktop.Controls;
using Windows.System;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private Grid? _settingsPageHost;

    private Task ShowApplicationSettingsPageAsync()
    {
        if (_settingsPageHost is not null)
        {
            return Task.CompletedTask;
        }

        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = false
        };
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        host.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        Grid.SetRowSpan(host, 2);
        Canvas.SetZIndex(host, 10_000);

        var header = new Grid
        {
            Padding = new Thickness(20, 8, 150, 8)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var backButton = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false,
            Content = new FontIcon
            {
                Glyph = "\uE72B",
                FontSize = 17
            }
        };
        ToolTipService.SetToolTip(backButton, Translate("Zurück", "Back"));
        AutomationProperties.SetName(
            backButton,
            Translate("Einstellungen schließen", "Close settings"));
        backButton.Click += (_, _) => CloseApplicationSettingsPage();
        header.Children.Add(backButton);

        var title = new TextBlock
        {
            Text = Translate("Einstellungen", "Settings"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var headerDivider = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetColumnSpan(headerDivider, 2);
        header.Children.Add(headerDivider);
        host.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        Grid.SetRow(body, 1);

        var sidebar = new Border
        {
            Padding = new Thickness(14, 18, 14, 18),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var sidebarStack = new StackPanel { Spacing = 6 };
        sidebar.Child = sidebarStack;
        body.Children.Add(sidebar);

        var bodyDivider = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(bodyDivider, 1);
        body.Children.Add(bodyDivider);

        var contentHost = new Grid
        {
            Padding = new Thickness(26, 18, 26, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentHost.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        Grid.SetColumn(contentHost, 2);
        body.Children.Add(contentHost);
        host.Children.Add(body);

        Button generalButton = CreateSettingsPageNavigationButton(
            "\uE713",
            Translate("Allgemein", "General"),
            "general");
        Button peopleButton = CreateSettingsPageNavigationButton(
            "\uE77B",
            Translate("Personen", "People"),
            "people");
        Button dataButton = CreateSettingsPageNavigationButton(
            "\uE8B7",
            Translate("Daten und Sicherung", "Data & backup"),
            "data");
        sidebarStack.Children.Add(generalButton);
        sidebarStack.Children.Add(peopleButton);
        sidebarStack.Children.Add(dataButton);

        string selectedTag = "general";

        void ApplyTheme()
        {
            Brush pageBrush = GetThemeBrush("ApplicationPageBackgroundThemeBrush");
            Brush dividerBrush = GetThemeBrush("DividerStrokeColorDefaultBrush");

            host.Background = pageBrush;
            header.Background = pageBrush;
            sidebar.Background = pageBrush;
            contentHost.Background = pageBrush;
            headerDivider.Background = dividerBrush;
            bodyDivider.Background = dividerBrush;
            backButton.Background = GetThemeBrush("SubtleFillColorTransparentBrush");

            ApplySettingsPageNavigationSelection(
                generalButton,
                selectedTag == "general");
            ApplySettingsPageNavigationSelection(
                peopleButton,
                selectedTag == "people");
            ApplySettingsPageNavigationSelection(
                dataButton,
                selectedTag == "data");
        }

        void DisplayPage(string tag)
        {
            selectedTag = tag;
            contentHost.Children.Clear();

            (string heading, FrameworkElement page) = tag switch
            {
                "people" => (
                    Translate("Personen", "People"),
                    BuildSpeakerProfilesPage()),
                "data" => (
                    Translate("Daten und Sicherung", "Data & backup"),
                    BuildDataSettingsPage(ExecuteSettingsPageAction)),
                _ => (
                    Translate("Allgemein", "General"),
                    BuildGeneralSettingsPage())
            };

            var headingText = new TextBlock
            {
                Text = heading,
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 16)
            };
            contentHost.Children.Add(headingText);

            page.MaxWidth = 980;
            page.HorizontalAlignment = HorizontalAlignment.Stretch;
            page.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(page, 1);
            contentHost.Children.Add(page);

            ApplyTheme();
        }

        generalButton.Click += (_, _) => DisplayPage("general");
        peopleButton.Click += (_, _) => DisplayPage("people");
        dataButton.Click += (_, _) => DisplayPage("data");
        host.ActualThemeChanged += (_, _) => ApplyTheme();

        DisplayPage("general");

        _settingsPageHost = host;
        RootGrid.Children.Add(host);
        RootGrid.KeyDown += SettingsPageRootGrid_KeyDown;
        DisableSystemFocusVisuals(host);
        backButton.Focus(FocusState.Programmatic);

        return Task.CompletedTask;
    }

    private Button CreateSettingsPageNavigationButton(
        string glyph,
        string label,
        string tag)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var indicator = new Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(indicator);

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        content.Children.Add(icon);

        var text = new TextBlock
        {
            Text = label,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 2);
        content.Children.Add(text);

        var button = new Button
        {
            Tag = tag,
            Content = content,
            Height = 44,
            Padding = new Thickness(10, 0, 12, 0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = false
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void ApplySettingsPageNavigationSelection(Button button, bool selected)
    {
        button.Background = selected
            ? GetThemeBrush("SubtleFillColorSecondaryBrush")
            : GetThemeBrush("SubtleFillColorTransparentBrush");

        if (button.Content is Grid content
            && content.Children.OfType<Border>().FirstOrDefault() is Border indicator)
        {
            indicator.Background = GetThemeBrush("AccentFillColorDefaultBrush");
            indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private FrameworkElement BuildSpeakerProfilesPage()
    {
        ISpeakerRepository repository =
            App.Services.GetRequiredService<ISpeakerRepository>();

        return new SpeakerProfilesPanel(
            repository,
            async () =>
            {
                Guid? selectedConversationId =
                    (ConversationsList.SelectedItem as ConversationListItem)
                    ?.Conversation.Id;
                await LoadConversationsAsync(selectedConversationId);
                RenderSelectedTranscript();
            },
            PlayRandomSpeakerPassageAsync,
            Translate);
    }

    private async void ExecuteSettingsPageAction(SettingsAction action)
    {
        // File pickers and confirmation dialogs are transient. The settings page
        // stays mounted so closing the Explorer returns to the same place.
        switch (action)
        {
            case SettingsAction.Export:
                await ExportCompleteBackupAsync();
                break;
            case SettingsAction.Import:
                await ImportCompleteBackupAsync();
                ReloadAndApplyApplicationSettings();
                break;
            case SettingsAction.Reset:
                await ResetAllDataAsync();
                ReloadAndApplyApplicationSettings();
                break;
            case SettingsAction.OpenDataFolder:
                OpenDataFolderButton_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void SettingsPageRootGrid_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || _settingsPageHost is null)
        {
            return;
        }

        args.Handled = true;
        CloseApplicationSettingsPage();
    }

    private void CloseApplicationSettingsPage()
    {
        if (_settingsPageHost is null)
        {
            return;
        }

        RootGrid.KeyDown -= SettingsPageRootGrid_KeyDown;
        StopSpeakerPreview();
        RootGrid.Children.Remove(_settingsPageHost);
        _settingsPageHost = null;
        _settingsButton?.Focus(FocusState.Programmatic);
    }
}
