using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Models;
using MeetingRecall.Core.Settings;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private IApplicationSettingsService? _applicationSettingsService;
    private bool _applicationSettingsInitialized;
    private CultureInfo? _originalCulture;
    private CultureInfo? _originalUiCulture;
    private CancellationTokenSource? _modelBundleDownloadCancellation;
    private Task? _modelBundleDownloadTask;
    private LocalModelBundleProgress? _modelBundleDownloadProgress;
    private ProgressBar? _modelBundleProgressBar;
    private TextBlock? _modelBundleStatusText;
    private Button? _modelBundleDownloadButton;

    public void InitializeApplicationSettings()
    {
        if (_applicationSettingsInitialized)
        {
            return;
        }

        _applicationSettingsInitialized = true;
        _originalCulture = CultureInfo.CurrentCulture;
        _originalUiCulture = CultureInfo.CurrentUICulture;
        _applicationSettingsService = App.Services.GetRequiredService<IApplicationSettingsService>();
        ApplyApplicationSettings(ApplicationSettingsService.Current);
        RootGrid.Loaded += ApplicationSettingsRootGrid_Loaded;
        Closed += ApplicationSettingsWindow_Closed;
    }

    private IApplicationSettingsService ApplicationSettingsService =>
        _applicationSettingsService
        ?? throw new InvalidOperationException("Die Anwendungseinstellungen wurden nicht initialisiert.");

    private ApplicationSettings CurrentApplicationSettings => ApplicationSettingsService.Current;

    private void ApplicationSettingsRootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyApplicationSettings(CurrentApplicationSettings);
    }

    private async Task ShowApplicationSettingsAsync()
    {
        SettingsAction requestedAction = SettingsAction.None;

        double availableWidth = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280;
        double availableHeight = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 800;

        double dialogWidth = Math.Clamp(availableWidth - 96, 560, 940);
        dialogWidth = Math.Min(dialogWidth, Math.Max(420, availableWidth - 32));

        double dialogHeight = Math.Clamp(availableHeight - 96, 500, 760);
        dialogHeight = Math.Min(dialogHeight, Math.Max(420, availableHeight - 32));

        double navigationWidth = Math.Max(372, dialogWidth - 48);
        double navigationHeight = Math.Max(320, dialogHeight - 132);

        var navigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            Width = navigationWidth,
            MinWidth = navigationWidth,
            MaxWidth = navigationWidth,
            Height = navigationHeight,
            MinHeight = navigationHeight,
            MaxHeight = navigationHeight,
            OpenPaneLength = 184,
            CompactPaneLength = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = false
        };

        NavigationViewItem generalItem = CreateNavigationItem(
            "\uE713",
            Translate("Allgemein", "General"),
            "general");
        NavigationViewItem dataItem = CreateNavigationItem(
            "\uE8B7",
            Translate("Daten und Sicherung", "Data & backup"),
            "data");

        navigation.MenuItems.Add(generalItem);
        navigation.MenuItems.Add(dataItem);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Translate("Einstellungen", "Settings"),
            Content = navigation,
            CloseButtonText = Translate("Schließen", "Close"),
            DefaultButton = ContentDialogButton.Close,
            Width = dialogWidth,
            MinWidth = dialogWidth,
            MaxWidth = dialogWidth,
            Height = dialogHeight,
            MinHeight = dialogHeight,
            MaxHeight = dialogHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = false
        };

        // The WinUI ContentDialog template otherwise caps itself at 548 DIPs.
        // Local resources and local size values keep this one dialog independent
        // from the sizes of confirmation and error dialogs elsewhere in the app.
        dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;

        void CloseForAction(SettingsAction action)
        {
            requestedAction = action;
            dialog.Hide();
        }

        void DisplayPage(string tag)
        {
            (string header, FrameworkElement page) = tag switch
            {
                "data" => (
                    Translate("Daten und Sicherung", "Data & backup"),
                    BuildDataSettingsPage(CloseForAction)),
                _ => (
                    Translate("Allgemein", "General"),
                    BuildGeneralSettingsPage())
            };

            navigation.Header = new TextBlock
            {
                Text = header,
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 8)
            };
            navigation.Content = page;
        }

        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is NavigationViewItem { Tag: string tag })
            {
                DisplayPage(tag);
            }
        };

        navigation.SelectedItem = generalItem;
        DisplayPage("general");
        await dialog.ShowAsync();

        switch (requestedAction)
        {
            case SettingsAction.Export:
                await ExportCompleteBackupAsync();
                break;
            case SettingsAction.Import:
                await ImportCompleteBackupAsync();
                break;
            case SettingsAction.Reset:
                await ResetAllDataAsync();
                break;
            case SettingsAction.OpenDataFolder:
                OpenDataFolderButton_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private FrameworkElement BuildGeneralSettingsPage()
    {
        ApplicationSettings settings = CurrentApplicationSettings;
        StackPanel stack = CreateSettingsPageStack();

        var languageOptions = new[]
        {
            new SettingOption<ApplicationLanguage>(
                ApplicationLanguage.System,
                Translate("Systemsprache", "System language")),
            new SettingOption<ApplicationLanguage>(ApplicationLanguage.German, "Deutsch"),
            new SettingOption<ApplicationLanguage>(ApplicationLanguage.English, "English (Beta)")
        };
        var languageBox = new ComboBox
        {
            Width = 190,
            HorizontalAlignment = HorizontalAlignment.Right,
            DisplayMemberPath = nameof(SettingOption<ApplicationLanguage>.Label),
            ItemsSource = languageOptions,
            SelectedItem = languageOptions.First(option => option.Value == settings.Language),
            UseSystemFocusVisuals = false
        };
        languageBox.SelectionChanged += async (_, _) =>
        {
            if (languageBox.SelectedItem is SettingOption<ApplicationLanguage> option)
            {
                await SaveApplicationSettingsAsync(
                    CurrentApplicationSettings with { Language = option.Value });
            }
        };
        stack.Children.Add(CreateSettingCard(
            "\uE775",
            Translate("Sprache", "Language"),
            Translate(
                "Steuert die Oberflächensprache und die lokalen Datumsformate.",
                "Controls the interface language and local date formats."),
            languageBox));

        int[] skipIntervals = [3, 5, 10, 15, 30];
        SettingOption<int>[] skipOptions = skipIntervals
            .Select(seconds => new SettingOption<int>(
                seconds,
                Translate($"{seconds} Sekunden", $"{seconds} seconds")))
            .ToArray();
        var skipIntervalBox = new ComboBox
        {
            Width = 190,
            HorizontalAlignment = HorizontalAlignment.Right,
            DisplayMemberPath = nameof(SettingOption<int>.Label),
            ItemsSource = skipOptions,
            SelectedItem = skipOptions.First(option =>
                option.Value == settings.SkipIntervalSeconds),
            UseSystemFocusVisuals = false
        };
        skipIntervalBox.SelectionChanged += async (_, _) =>
        {
            if (skipIntervalBox.SelectedItem is SettingOption<int> option)
            {
                await SaveApplicationSettingsAsync(
                    CurrentApplicationSettings with
                    {
                        SkipIntervalSeconds = option.Value
                    });
            }
        };
        stack.Children.Add(CreateSettingCard(
            "\uE916",
            Translate("Sprungweite", "Skip interval"),
            Translate(
                "Legt fest, wie weit die Zurück- und Vorspultasten springen.",
                "Controls how far the back and forward buttons skip."),
            skipIntervalBox));

        var liveTranscriptToggle = new ToggleSwitch
        {
            OffContent = Translate("Aus", "Off"),
            OnContent = Translate("An", "On"),
            IsOn = settings.LiveTranscriptEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false
        };
        liveTranscriptToggle.Toggled += async (_, _) =>
        {
            await SaveApplicationSettingsAsync(
                CurrentApplicationSettings with
                {
                    LiveTranscriptEnabled = liveTranscriptToggle.IsOn
                });
        };
        stack.Children.Add(CreateSettingCard(
            "\uE8D2",
            Translate("Live-Transkript", "Live transcript"),
            Translate(
                "Hebt den gesprochenen Text während der Audiowiedergabe schrittweise hervor.",
                "Progressively highlights spoken text during audio playback."),
            liveTranscriptToggle));

        stack.Children.Add(CreateUpdateSettingsCard());
        stack.Children.Add(CreateDownloadAllModelsCard());

        return CreateSettingsScrollViewer(stack);
    }

    private Border CreateDownloadAllModelsCard()
    {
        ILocalModelBundleService modelBundle =
            App.Services.GetRequiredService<ILocalModelBundleService>();
        string totalSize = FormatFileSize(modelBundle.TotalDownloadSizeBytes);

        var progressBar = new ProgressBar
        {
            Width = 230,
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed
        };
        var statusText = new TextBlock
        {
            Width = 230,
            FontSize = 11,
            Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            Visibility = Visibility.Collapsed
        };
        var downloadButton = new Button
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
            UseSystemFocusVisuals = false
        };
        var downloadControl = new StackPanel
        {
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                statusText,
                progressBar,
                downloadButton
            }
        };

        // The settings page is recreated whenever navigation returns to it.
        // Keep the active controls on the MainWindow so the new card can bind
        // to the download task that outlives the previous page instance.
        _modelBundleProgressBar = progressBar;
        _modelBundleStatusText = statusText;
        _modelBundleDownloadButton = downloadButton;
        RefreshModelBundleDownloadUi(modelBundle);

        downloadButton.Click += async (_, _) =>
        {
            if (_modelBundleDownloadTask is { IsCompleted: false })
            {
                _modelBundleDownloadCancellation?.Cancel();
                downloadButton.Content = Translate(
                    "Wird abgebrochen …",
                    "Cancelling …");
                downloadButton.IsEnabled = false;
                return;
            }

            var operationCancellation = new CancellationTokenSource();
            _modelBundleDownloadCancellation = operationCancellation;
            _modelBundleDownloadProgress = null;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusText.Text = Translate(
                "Download wird vorbereitet …",
                "Preparing download …");
            statusText.Visibility = Visibility.Visible;
            downloadButton.Content = Translate("Abbrechen", "Cancel");
            downloadButton.IsEnabled = true;

            var progress = new Progress<LocalModelBundleProgress>(item =>
            {
                _modelBundleDownloadProgress = item;
                UpdateModelBundleDownloadProgressUi(item);
            });

            Task downloadTask = modelBundle.DownloadAllAsync(
                progress,
                operationCancellation.Token);
            _modelBundleDownloadTask = downloadTask;

            try
            {
                await downloadTask;
                progressBar.IsIndeterminate = false;
                progressBar.Value = 100;
                statusText.Text = Translate(
                    "Alle benötigten Modelle sind lokal verfügbar.",
                    "All required models are available locally.");
                statusText.Visibility = Visibility.Visible;
                downloadButton.Content = Translate(
                    "Vollständig installiert",
                    "Fully installed");
                downloadButton.IsEnabled = false;
            }
            catch (OperationCanceledException)
            {
                progressBar.Visibility = Visibility.Collapsed;
                statusText.Text = Translate(
                    "Abgebrochen · vollständig geladene Modelle bleiben erhalten.",
                    "Cancelled · completed models are kept.");
                statusText.Visibility = Visibility.Visible;
                downloadButton.Content = Translate(
                    "Fortsetzen",
                    "Continue");
                downloadButton.IsEnabled = true;
            }
            catch (Exception exception)
            {
                progressBar.Visibility = Visibility.Collapsed;
                statusText.Text = Translate(
                    "Download nicht abgeschlossen.",
                    "Download not completed.");
                statusText.Visibility = Visibility.Visible;
                downloadButton.Content = Translate(
                    "Erneut versuchen",
                    "Try again");
                downloadButton.IsEnabled = true;
                await ShowMessageAsync(
                    Translate(
                        "Modelle konnten nicht vollständig heruntergeladen werden",
                        "Models could not be downloaded completely"),
                    exception.Message,
                    Translate("Schließen", "Close"));
            }
            finally
            {
                if (ReferenceEquals(_modelBundleDownloadTask, downloadTask))
                {
                    _modelBundleDownloadTask = null;
                    _modelBundleDownloadCancellation = null;
                }

                operationCancellation.Dispose();
            }
        };

        return CreateSettingCard(
            "\uE896",
            Translate("Alle benötigten Modelle", "All required models"),
            Translate(
                $"Lädt Whisper Large-v3, Qwen3 14B und die zwei Dateien der Sprechererkennung vorab herunter. Bereits vorhandene Dateien werden übersprungen · insgesamt ca. {totalSize}.",
                $"Downloads Whisper Large-v3, Qwen3 14B, and the two speaker-recognition files in advance. Existing files are skipped · about {totalSize} total."),
            downloadControl);
    }

    private void RefreshModelBundleDownloadUi(
        ILocalModelBundleService modelBundle)
    {
        if (_modelBundleProgressBar is null
            || _modelBundleStatusText is null
            || _modelBundleDownloadButton is null)
        {
            return;
        }

        if (modelBundle.AreAllModelsInstalled)
        {
            _modelBundleProgressBar.IsIndeterminate = false;
            _modelBundleProgressBar.Value = 100;
            _modelBundleProgressBar.Visibility = Visibility.Visible;
            _modelBundleStatusText.Text = Translate(
                "Alle benötigten Modelle sind lokal verfügbar.",
                "All required models are available locally.");
            _modelBundleStatusText.Visibility = Visibility.Visible;
            _modelBundleDownloadButton.Content = Translate(
                "Vollständig installiert",
                "Fully installed");
            _modelBundleDownloadButton.IsEnabled = false;
            return;
        }

        if (_modelBundleDownloadTask is { IsCompleted: false })
        {
            _modelBundleProgressBar.Visibility = Visibility.Visible;
            _modelBundleStatusText.Visibility = Visibility.Visible;
            _modelBundleDownloadButton.Content = Translate("Abbrechen", "Cancel");
            _modelBundleDownloadButton.IsEnabled = true;

            if (_modelBundleDownloadProgress is { } currentProgress)
            {
                UpdateModelBundleDownloadProgressUi(currentProgress);
            }
            else
            {
                _modelBundleProgressBar.IsIndeterminate = true;
                _modelBundleStatusText.Text = Translate(
                    "Download wird vorbereitet …",
                    "Preparing download …");
            }

            return;
        }

        _modelBundleProgressBar.Visibility = Visibility.Collapsed;
        _modelBundleStatusText.Visibility = Visibility.Collapsed;
        _modelBundleDownloadButton.Content = Translate(
            "Alle herunterladen",
            "Download all");
        _modelBundleDownloadButton.IsEnabled = true;
    }

    private void UpdateModelBundleDownloadProgressUi(
        LocalModelBundleProgress item)
    {
        if (_modelBundleProgressBar is null
            || _modelBundleStatusText is null)
        {
            return;
        }

        _modelBundleProgressBar.Visibility = Visibility.Visible;
        _modelBundleProgressBar.IsIndeterminate = item.Fraction is null;
        if (item.Fraction is double fraction)
        {
            _modelBundleProgressBar.Value =
                Math.Clamp(fraction * 100, 0, 100);
        }

        string byteProgress = item.TotalBytes > 0
            ? $" · {FormatFileSize(item.DownloadedBytes)} / "
              + FormatFileSize(item.TotalBytes)
            : string.Empty;
        _modelBundleStatusText.Text =
            $"{item.ModelName} · {item.CompletedModels}/{item.TotalModels}"
            + byteProgress;
        _modelBundleStatusText.Visibility = Visibility.Visible;
    }

    private FrameworkElement BuildDataSettingsPage(Action<SettingsAction> requestAction)
    {
        StackPanel stack = CreateSettingsPageStack();
        var encryptToggle = new ToggleSwitch
        {
            OffContent = Translate("Aus", "Off"),
            OnContent = Translate("Ein", "On"),
            IsOn = CurrentApplicationSettings.EncryptBackupsByDefault,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false
        };
        ToolTipService.SetToolTip(
            encryptToggle,
            Translate(
                "Sicherungen standardmäßig verschlüsseln (empfohlen)",
                "Encrypt backups by default (recommended)"));
        AutomationProperties.SetName(
            encryptToggle,
            Translate(
                "Sicherungen standardmäßig verschlüsseln",
                "Encrypt backups by default"));
        encryptToggle.Toggled += async (_, _) =>
        {
            await SaveApplicationSettingsAsync(
                CurrentApplicationSettings with
                {
                    EncryptBackupsByDefault = encryptToggle.IsOn
                });
        };
        stack.Children.Add(CreateSettingCard(
            "\uE72E",
            Translate("Sicherungsschutz", "Backup protection"),
            Translate(
                "Der Exportdialog übernimmt diese Auswahl. Unverschlüsselte Sicherungen erfordern weiterhin eine ausdrückliche Bestätigung.",
                "The export dialog uses this preference. Unencrypted backups still require explicit confirmation."),
            encryptToggle));

        stack.Children.Add(CreateActionCard(
            "\uE898",
            Translate("Vollständige Sicherung exportieren", "Export complete backup"),
            Translate(
                "Speichert Gespräche, Audio, Transkripte, Modelle, Sprecherprofile und Einstellungen in einer einzigen .mrbackup-Datei.",
                "Stores conversations, audio, transcripts, models, speaker profiles and settings in one .mrbackup file."),
            Translate("Exportieren", "Export"),
            () => requestAction(SettingsAction.Export)));

        stack.Children.Add(CreateActionCard(
            "\uE8B5",
            Translate("Sicherung importieren", "Import backup"),
            Translate(
                "Importiert eine .mrbackup-Datei zur Wiederherstellung eines alten Zustands.",
                "Imports a .mrbackup file to restore an earlier state."),
            Translate("Importieren", "Import"),
            () => requestAction(SettingsAction.Import)));

        stack.Children.Add(CreateActionCard(
            "\uE8B7",
            Translate("Datenordner", "Data folder"),
            ResolvePhysicalDataRoot(),
            Translate("Öffnen", "Open"),
            () => requestAction(SettingsAction.OpenDataFolder)));

        stack.Children.Add(CreateDangerCard(
            Translate("Werkseinstellungen", "Factory reset"),
            Translate(
                "Entfernt alle lokalen MeetingRecall-Daten.",
                "Removes all local MeetingRecall data."),
            Translate("Alles zurücksetzen", "Reset everything"),
            () => requestAction(SettingsAction.Reset)));

        return CreateSettingsScrollViewer(stack);
    }

    private static ScrollViewer CreateSettingsScrollViewer(StackPanel content)
    {
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static StackPanel CreateSettingsPageStack()
    {
        return new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(4, 4, 14, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private Border CreateSettingCard(
        string glyph,
        string title,
        string description,
        FrameworkElement control)
    {
        var grid = new Grid
        {
            ColumnSpacing = 18,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(CreateSettingDescription(glyph, title, description));
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return CreateCard(grid);
    }

    private Border CreateActionCard(
        string glyph,
        string title,
        string description,
        string buttonText,
        Action action)
    {
        var button = new Button
        {
            Content = buttonText,
            MinWidth = 118,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false
        };
        button.Click += (_, _) => action();
        return CreateSettingCard(glyph, title, description, button);
    }

    private Border CreateDangerCard(
        string title,
        string description,
        string buttonText,
        Action action)
    {
        var button = new Button
        {
            Content = buttonText,
            MinWidth = 145,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetThemeBrush("SystemFillColorCriticalBrush"),
            UseSystemFocusVisuals = false
        };
        button.Click += (_, _) => action();
        return CreateSettingCard("\uE74D", title, description, button);
    }

    private static Grid CreateSettingDescription(
        string glyph,
        string title,
        string description)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var iconBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(35, 80, 140, 255)),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 92, 151, 255))
            }
        };
        grid.Children.Add(iconBorder);

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private Border CreateCard(FrameworkElement child)
    {
        return new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = child
        };
    }

    private Brush GetThemeBrush(string resourceKey)
    {
        return Application.Current.Resources[resourceKey] as Brush
            ?? new SolidColorBrush(Colors.Transparent);
    }

    private static NavigationViewItem CreateNavigationItem(
        string glyph,
        string label,
        string tag)
    {
        return new NavigationViewItem
        {
            Content = label,
            Tag = tag,
            Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
            UseSystemFocusVisuals = false
        };
    }

    private async Task SaveApplicationSettingsAsync(ApplicationSettings settings)
    {
        try
        {
            await ApplicationSettingsService.SaveAsync(settings);
            ApplyApplicationSettings(ApplicationSettingsService.Current);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                Translate(
                    "Einstellung konnte nicht gespeichert werden",
                    "Setting could not be saved"),
                exception.Message,
                Translate("Schließen", "Close"));
        }
    }

    private void ReloadAndApplyApplicationSettings()
    {
        ApplyApplicationSettings(ApplicationSettingsService.Reload());
    }

    private void ApplyApplicationSettings(ApplicationSettings settings)
    {
        RootGrid.RequestedTheme = ElementTheme.Dark;
        UpdateSkipIntervalUi(settings.SkipIntervalSeconds);
        CultureInfo culture = settings.Language switch
        {
            ApplicationLanguage.German => CultureInfo.GetCultureInfo("de-DE"),
            ApplicationLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => _originalCulture ?? CultureInfo.CurrentCulture
        };
        CultureInfo uiCulture = settings.Language switch
        {
            ApplicationLanguage.German => CultureInfo.GetCultureInfo("de-DE"),
            ApplicationLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => _originalUiCulture ?? CultureInfo.CurrentUICulture
        };

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
        ApplyShellLanguage(settings.Language == ApplicationLanguage.English);
    }

    private void UpdateSkipIntervalUi(int seconds)
    {
        SkipBackwardText.Text = $"−{seconds}";
        SkipForwardText.Text = $"+{seconds}";

        string backwardLabel = Translate(
            $"{seconds} Sekunden zurück",
            $"Back {seconds} seconds");
        string forwardLabel = Translate(
            $"{seconds} Sekunden vor",
            $"Forward {seconds} seconds");
        ToolTipService.SetToolTip(SkipBackwardButton, backwardLabel);
        ToolTipService.SetToolTip(SkipForwardButton, forwardLabel);
        AutomationProperties.SetName(SkipBackwardButton, backwardLabel);
        AutomationProperties.SetName(SkipForwardButton, forwardLabel);
    }

    private void ApplyShellLanguage(bool english)
    {
        foreach ((string German, string English) pair in ShellTranslations)
        {
            ReplaceExactUiText(RootGrid, pair.German, pair.English, english);
        }
    }

    private static void ReplaceExactUiText(
        DependencyObject root,
        string german,
        string english,
        bool useEnglish)
    {
        string target = useEnglish ? english : german;

        if (root is TextBlock textBlock
            && (string.Equals(textBlock.Text, german, StringComparison.Ordinal)
                || string.Equals(textBlock.Text, english, StringComparison.Ordinal)))
        {
            textBlock.Text = target;
        }
        else if (root is ContentControl contentControl
                 && contentControl.Content is string content
                 && (string.Equals(content, german, StringComparison.Ordinal)
                     || string.Equals(content, english, StringComparison.Ordinal)))
        {
            contentControl.Content = target;
        }
        else if (root is AutoSuggestBox autoSuggestBox
                 && (string.Equals(autoSuggestBox.PlaceholderText, german, StringComparison.Ordinal)
                     || string.Equals(autoSuggestBox.PlaceholderText, english, StringComparison.Ordinal)))
        {
            autoSuggestBox.PlaceholderText = target;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            ReplaceExactUiText(
                VisualTreeHelper.GetChild(root, index),
                german,
                english,
                useEnglish);
        }
    }

    private string Translate(string german, string english)
    {
        return CurrentApplicationSettings.Language == ApplicationLanguage.English
            ? english
            : german;
    }

    private void ApplicationSettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        RootGrid.Loaded -= ApplicationSettingsRootGrid_Loaded;
    }

    private static readonly (string German, string English)[] ShellTranslations =
    [
        ("Gespräche durchsuchen", "Search conversations"),
        ("Exakt", "Exact"),
        ("Inhaltlich", "Semantic"),
        ("Neue Aufnahme", "New recording"),
        ("Datei importieren", "Import file"),
        ("ALLE GESPRÄCHE", "ALL CONVERSATIONS"),
        ("Datenordner öffnen", "Open data folder"),
        ("Deine lokalen Gespräche", "Your local conversations"),
        ("Audio", "Audio"),
        ("Tempo", "Speed"),
        ("Transkript", "Transcript")
    ];

    private sealed record SettingOption<T>(T Value, string Label);

    private enum SettingsAction
    {
        None = 0,
        Export = 1,
        Import = 2,
        Reset = 3,
        OpenDataFolder = 4
    }
}
