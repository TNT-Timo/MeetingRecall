using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Audio;
using MeetingRecall.Core.Interfaces;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _recordingTimer = new();
    private INativeAudioRecordingService? _nativeRecordingService;
    private Brush? _defaultRecordingButtonBackground;
    private Brush? _defaultRecordingButtonForeground;
    private bool _nativeRecordingUiInitialized;

    public void InitializeNativeRecording()
    {
        if (_nativeRecordingUiInitialized)
        {
            return;
        }

        _nativeRecordingUiInitialized = true;
        _nativeRecordingService = App.Services.GetRequiredService<INativeAudioRecordingService>();
        _defaultRecordingButtonBackground = RecordingButton.Background;
        _defaultRecordingButtonForeground = RecordingButton.Foreground;

        RecordingButton.Click -= RecordingButton_Click;
        RecordingButton.Click += NativeRecordingButton_Click;

        _recordingTimer.Interval = TimeSpan.FromMilliseconds(500);
        _recordingTimer.Tick += RecordingTimer_Tick;
        Closed += NativeRecordingWindow_Closed;
    }

    private INativeAudioRecordingService NativeRecordingService =>
        _nativeRecordingService
        ?? throw new InvalidOperationException("The native recording UI has not been initialized.");

    private async void NativeRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (NativeRecordingService.IsRecording)
        {
            await ShowRunningRecordingDialogAsync();
            return;
        }

        await StartNativeRecordingAsync();
    }

    private async Task StartNativeRecordingAsync()
    {
        SetBusy(true, "Audiogeräte werden geladen …");
        IReadOnlyList<AudioCaptureDevice> microphones;
        IReadOnlyList<AudioCaptureDevice> desktopOutputs;
        try
        {
            microphones = await NativeRecordingService.ListMicrophonesAsync();
            desktopOutputs = await NativeRecordingService.ListDesktopOutputsAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Audiogeräte konnten nicht geladen werden",
                exception.Message,
                "Schließen");
            return;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        if (microphones.Count == 0 && desktopOutputs.Count == 0)
        {
            await ShowMessageAsync(
                "Keine Audioquelle verfügbar",
                "Windows meldet weder ein aktives Mikrofon noch eine aktive Audioausgabe.",
                "Schließen");
            return;
        }

        NativeRecordingOptions? options = await ShowRecordingSetupDialogAsync(
            microphones,
            desktopOutputs);
        if (options is null)
        {
            return;
        }

        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        ResetPlaybackUi();

        SetBusy(true, "Aufnahme wird vorbereitet …");
        try
        {
            await NativeRecordingService.StartAsync(options);
            BeginNativeRecordingUi();
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                "Aufnahme konnte nicht gestartet werden",
                BuildRecordingErrorMessage(exception),
                "Schließen");
        }
        finally
        {
            _isBusy = false;
            BusyIndicator.IsActive = false;
            RecordingButton.IsEnabled = true;
            ImportButton.IsEnabled = !NativeRecordingService.IsRecording;
        }
    }

    private async Task ShowRunningRecordingDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Aufnahme läuft",
            Content = $"Mikrofon und Desktopaudio werden lokal aufgezeichnet.\n\nAktuelle Dauer: {FormatRecordingTime(NativeRecordingService.Elapsed)}",
            PrimaryButtonText = "Stoppen und speichern",
            SecondaryButtonText = "Verwerfen",
            CloseButtonText = "Weiter aufnehmen",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await StopNativeRecordingAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await CancelNativeRecordingAsync();
        }
    }

    private async Task StopNativeRecordingAsync()
    {
        _recordingTimer.Stop();
        SetBusy(true, "Aufnahme wird zusammengeführt und gespeichert …");
        try
        {
            NativeRecordingResult result = await NativeRecordingService.StopAsync();
            await LoadConversationsAsync(result.Conversation.Id);
            StatusText.Text = "Aufnahme gespeichert";
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                "Aufnahme konnte nicht gespeichert werden",
                BuildRecordingErrorMessage(exception),
                "Schließen");
        }
        finally
        {
            ResetNativeRecordingUi();
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task CancelNativeRecordingAsync()
    {
        _recordingTimer.Stop();
        SetBusy(true, "Aufnahme wird verworfen …");
        try
        {
            await NativeRecordingService.CancelAsync();
            StatusText.Text = "Aufnahme verworfen";
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            await ShowMessageAsync(
                "Aufnahme konnte nicht verworfen werden",
                exception.Message,
                "Schließen");
        }
        finally
        {
            ResetNativeRecordingUi();
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task<NativeRecordingOptions?> ShowRecordingSetupDialogAsync(
        IReadOnlyList<AudioCaptureDevice> microphones,
        IReadOnlyList<AudioCaptureDevice> desktopOutputs)
    {
        var titleBox = new TextBox
        {
            Header = "Titel (optional)",
            PlaceholderText = $"Aufnahme {DateTimeOffset.Now:dd.MM.yyyy HH:mm}",
            MaxLength = 240
        };

        var microphoneCheckBox = new CheckBox
        {
            Content = "Mikrofon aufnehmen",
            IsChecked = microphones.Count > 0,
            IsEnabled = microphones.Count > 0
        };
        var microphoneComboBox = new ComboBox
        {
            Header = "Mikrofon",
            ItemsSource = microphones,
            DisplayMemberPath = nameof(AudioCaptureDevice.DisplayName),
            SelectedIndex = microphones.Count > 0 ? 0 : -1,
            IsEnabled = microphones.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var desktopCheckBox = new CheckBox
        {
            Content = "Desktopaudio aufnehmen",
            IsChecked = desktopOutputs.Count > 0,
            IsEnabled = desktopOutputs.Count > 0
        };
        var desktopComboBox = new ComboBox
        {
            Header = "Audioausgabe für Desktop-Loopback",
            ItemsSource = desktopOutputs,
            DisplayMemberPath = nameof(AudioCaptureDevice.DisplayName),
            SelectedIndex = desktopOutputs.Count > 0 ? 0 : -1,
            IsEnabled = desktopOutputs.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        microphoneCheckBox.Checked += (_, _) => microphoneComboBox.IsEnabled = true;
        microphoneCheckBox.Unchecked += (_, _) => microphoneComboBox.IsEnabled = false;
        desktopCheckBox.Checked += (_, _) => desktopComboBox.IsEnabled = true;
        desktopCheckBox.Unchecked += (_, _) => desktopComboBox.IsEnabled = false;

        var content = new StackPanel
        {
            Width = 480,
            Spacing = 12
        };
        content.Children.Add(new TextBlock
        {
            Text = "MeetingRecall speichert Mikrofon und Windows-Audioausgabe als getrennte lokale WAV-Spuren und erstellt nach dem Stoppen automatisch eine gemeinsame Hauptspur.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.Gray)
        });
        content.Children.Add(titleBox);
        content.Children.Add(microphoneCheckBox);
        content.Children.Add(microphoneComboBox);
        content.Children.Add(desktopCheckBox);
        content.Children.Add(desktopComboBox);
        content.Children.Add(new TextBlock
        {
            Text = "Die Desktopspur erfasst die gesamte ausgewählte Windows-Audioausgabe. Einzelne Apps können in diesem Sprint noch nicht separat gefiltert werden.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Neue Aufnahme",
            Content = content,
            PrimaryButtonText = "Aufnahme starten",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        bool captureMicrophone = microphoneCheckBox.IsChecked == true;
        bool captureDesktop = desktopCheckBox.IsChecked == true;
        if (!captureMicrophone && !captureDesktop)
        {
            await ShowMessageAsync(
                "Keine Audioquelle ausgewählt",
                "Aktiviere mindestens das Mikrofon oder Desktopaudio.",
                "Schließen");
            return null;
        }

        return new NativeRecordingOptions(
            titleBox.Text,
            captureMicrophone
                ? (microphoneComboBox.SelectedItem as AudioCaptureDevice)?.Id
                : null,
            captureDesktop
                ? (desktopComboBox.SelectedItem as AudioCaptureDevice)?.Id
                : null,
            captureMicrophone,
            captureDesktop);
    }

    private void BeginNativeRecordingUi()
    {
        RecordingButton.Content = CreateRecordingButtonContent("\uE71A", "Aufnahme stoppen");
        RecordingButton.Background = new SolidColorBrush(Colors.Red);
        RecordingButton.Foreground = new SolidColorBrush(Colors.White);
        ImportButton.IsEnabled = false;
        ConversationsList.IsEnabled = false;
        _recordingTimer.Start();
        UpdateRecordingStatus();
    }

    private void ResetNativeRecordingUi()
    {
        _recordingTimer.Stop();
        RecordingButton.Content = CreateRecordingButtonContent("\uE720", "Neue Aufnahme");
        RecordingButton.Background = _defaultRecordingButtonBackground;
        RecordingButton.Foreground = _defaultRecordingButtonForeground;
        ImportButton.IsEnabled = true;
        ConversationsList.IsEnabled = true;
    }

    private void RecordingTimer_Tick(object? sender, object e)
    {
        UpdateRecordingStatus();
    }

    private void UpdateRecordingStatus()
    {
        if (!NativeRecordingService.IsRecording)
        {
            return;
        }

        StatusText.Text = $"● Aufnahme · {FormatRecordingTime(NativeRecordingService.Elapsed)}";
    }

    private void NativeRecordingWindow_Closed(object sender, WindowEventArgs args)
    {
        _recordingTimer.Stop();
        if (!NativeRecordingService.IsRecording)
        {
            return;
        }

        try
        {
            NativeRecordingService.CancelAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // The process is shutting down. The session marker preserves recovery context.
        }
    }

    private static StackPanel CreateRecordingButtonContent(string glyph, string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16
        });
        panel.Children.Add(new TextBlock
        {
            Text = text
        });
        return panel;
    }

    private static string FormatRecordingTime(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string BuildRecordingErrorMessage(Exception exception)
    {
        string message = exception.Message;
        if (message.Contains("0x88890004", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}\n\nPrüfe, ob das Audiogerät noch verbunden ist und nicht exklusiv von einer anderen Anwendung verwendet wird.";
        }

        if (message.Contains("access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}\n\nPrüfe unter Windows → Datenschutz und Sicherheit → Mikrofon, ob Desktop-Apps auf das Mikrofon zugreifen dürfen.";
        }

        return message;
    }
}
