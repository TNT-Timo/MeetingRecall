using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingRecall.Desktop.Services;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private readonly UpdateService _updateService = new();
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private CancellationTokenSource? _updateMonitorCancellation;
    private DateTimeOffset? _lastUpdateCheck;
    private TextBlock? _updateStatusText;
    private bool _updateWindowClosed;
    private bool _isDownloadingUpdate;
    private UpdateRelease? _offeredUpdate;
    private CancellationTokenSource? _activeUpdateDownloadCancellation;

    public void StartUpdateChecking()
    {
        if (_updateMonitorCancellation is not null)
        {
            return;
        }

        _updateService.CleanupInstalledUpdateInstallers();
        _updateMonitorCancellation = new CancellationTokenSource();
        Closed += (_, _) =>
        {
            _updateWindowClosed = true;
            _updateMonitorCancellation.Cancel();
            _updateService.Dispose();
        };
        _ = MonitorUpdatesAsync(_updateMonitorCancellation.Token);
    }

    private async Task MonitorUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let WinUI display the window before contacting the network.
            await Task.Yield();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CheckForUpdatesAsync(manual: false, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A background update failure must not terminate MeetingRecall.
                    Debug.WriteLine($"MeetingRecall update check failed: {exception}");
                }

                await Task.Delay(TimeSpan.FromHours(24), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window was closed.
        }
    }

    private Border CreateUpdateSettingsCard()
    {
        var button = new Button
        {
            Content = Translate("Nach Updates suchen", "Check for updates"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false
        };

        _updateStatusText = new TextBlock
        {
            Text = _lastUpdateCheck is null
                ? Translate("Noch nicht geprüft", "Not checked yet")
                : Translate("Zuletzt geprüft: ", "Last checked: ") + _lastUpdateCheck.Value.ToString("g"),
            FontSize = 11,
            Opacity = 0.7,
            TextAlignment = TextAlignment.Right
        };

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await CheckForUpdatesAsync(manual: true, CancellationToken.None);
            }
            catch (Exception exception)
            {
                await ShowUpdateMessageAsync(
                    Translate("Update-Prüfung", "Update check"),
                    Translate("Die Update-Prüfung ist fehlgeschlagen: ",
                              "Update check failed: ") + exception.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        var right = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        right.Children.Add(button);
        right.Children.Add(_updateStatusText);

        return CreateSettingCard(
            "\uE895",
            Translate("Softwareupdates", "Software updates"),
            $"Version {_updateService.InstalledVersion.ToString(3)}",
            right);
    }

    private async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken)
    {
        // Never replace the active download's progress card with another update offer.
        if (_isDownloadingUpdate)
        {
            return;
        }

        // Manual checks wait for an in-flight automatic check.
        if (manual)
        {
            await _updateCheckGate.WaitAsync(cancellationToken);
        }
        else if (!await _updateCheckGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            UpdateCheckResult result = await _updateService.CheckAsync(cancellationToken);
            _lastUpdateCheck = DateTimeOffset.Now;

            if (_updateStatusText is not null)
            {
                _updateStatusText.Text = Translate("Zuletzt geprüft: ", "Last checked: ")
                    + _lastUpdateCheck.Value.ToString("g");
            }

            if (_updateWindowClosed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result is { Status: UpdateCheckStatus.Available, Release: not null })
            {
                if (manual || !_updateService.IsIgnored(result.Release.Version))
                {
                    ShowUpdateOffer(result.Release);
                }
            }
            else if (manual)
            {
                await ShowUpdateMessageAsync(
                    Translate("Update-Prüfung", "Update check"),
                    result.Status == UpdateCheckStatus.UpToDate
                        ? Translate("MeetingRecall ist bereits auf dem neuesten Stand.",
                                    "MeetingRecall is up to date.")
                        : result.Message ?? Translate(
                            "Zurzeit können keine Updates geprüft werden.",
                            "Updates cannot be checked right now."));
            }
        }
        finally
        {
            _updateCheckGate.Release();
        }
    }

    private void ShowUpdateOffer(UpdateRelease release)
    {
        if (_updateWindowClosed || _isDownloadingUpdate)
        {
            return;
        }

        _offeredUpdate = release;
        UpdateCardTitle.Text = Translate("Update verfügbar", "Update available");
        UpdateCardDescription.Text = Translate(
            $"Version {release.Version.ToString(3)} ist bereit. Installiert: {_updateService.InstalledVersion.ToString(3)}.",
            $"Version {release.Version.ToString(3)} is ready. Installed: {_updateService.InstalledVersion.ToString(3)}.");
        UpdateCardSizeLabel.Text = Translate("Downloadgröße: ", "Download size: ")
            + FormatUpdateSize(release.Size);
        UpdateCardSizeLabel.Visibility = Visibility.Visible;
        UpdateDownloadPanel.Visibility = Visibility.Collapsed;
        UpdateCancelDownloadButton.Visibility = Visibility.Collapsed;
        UpdateCardActions.Visibility = Visibility.Visible;
        UpdateCardCloseButton.Visibility = Visibility.Visible;
        UpdateDeclineButton.Content = Translate("Ablehnen", "Decline");
        UpdateInstallButton.Content = Translate("Jetzt installieren", "Install now");
        UpdateCard.Visibility = Visibility.Visible;
        _ = RefreshCachedUpdateOfferAsync(release);
    }

    private async Task RefreshCachedUpdateOfferAsync(UpdateRelease release)
    {
        try
        {
            string? installer = await _updateService.GetCachedInstallerAsync(release);
            if (installer is null || _updateWindowClosed || _isDownloadingUpdate
                || _offeredUpdate != release || UpdateCard.Visibility != Visibility.Visible)
                return;

            UpdateCardTitle.Text = Translate("Update bereits heruntergeladen", "Update already downloaded");
            UpdateCardDescription.Text = Translate(
                $"Version {release.Version.ToString(3)} wurde bereits heruntergeladen und sicher geprüft.",
                $"Version {release.Version.ToString(3)} is already downloaded and verified.");
            UpdateCardSizeLabel.Text = Translate("Bereits gespeichert: ", "Already saved: ")
                + FormatUpdateSize(release.Size);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"MeetingRecall cached update check failed: {exception}");
        }
    }

    private static string FormatUpdateSize(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        const double gigabyte = 1024d * megabyte;
        return bytes >= gigabyte
            ? $"{bytes / gigabyte:0.##} GB"
            : $"{bytes / megabyte:0.##} MB";
    }

    // Fixed-width numeric fields prevent the following text from moving as digits change.
    // The progress label uses Consolas in XAML, so padding spaces have a stable width.
    private static string FormatDownloadProgress(long receivedBytes, long totalBytes, double percent)
    {
        const double megabyte = 1024d * 1024d;
        double receivedMb = receivedBytes / megabyte;
        double totalMb = totalBytes / megabyte;
        return $"{receivedMb,8:0.00} MB / {totalMb:0.00} MB ({Math.Clamp(percent, 0d, 100d),3:0} %)";
    }

    private void UpdateDeclineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is not { } release || _isDownloadingUpdate)
        {
            return;
        }

        if (!_updateService.IgnoreVersion(release.Version))
        {
            ShowUpdateMessage(
                Translate("Update-Prüfung", "Update check"),
                Translate("Die Ablehnung konnte nicht gespeichert werden.",
                          "The declined version could not be saved."));
            return;
        }

        _offeredUpdate = null;
        UpdateCard.Visibility = Visibility.Collapsed;
    }

    private void UpdateCardCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingUpdate)
        {
            return;
        }

        _offeredUpdate = null;
        UpdateCard.Visibility = Visibility.Collapsed;
    }

    private async void UpdateInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is not { } release || _isDownloadingUpdate)
        {
            return;
        }

        await DownloadAndStartUpdateAsync(release);
    }

    private void UpdateCancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _activeUpdateDownloadCancellation?.Cancel();
        UpdateCancelDownloadButton.IsEnabled = false;
    }

    private async Task DownloadAndStartUpdateAsync(UpdateRelease release)
    {
        if (_isDownloadingUpdate)
        {
            return;
        }

        _isDownloadingUpdate = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _updateMonitorCancellation?.Token ?? CancellationToken.None);
        _activeUpdateDownloadCancellation = cancellation;

        UpdateCardTitle.Text = Translate("Update wird heruntergeladen", "Downloading update");
        UpdateCardDescription.Text = Translate(
            $"Version {release.Version.ToString(3)} wird sicher heruntergeladen und geprüft. Du kannst MeetingRecall weiter benutzen.",
            $"Version {release.Version.ToString(3)} is being downloaded and verified. You can keep using MeetingRecall.");
        UpdateCardSizeLabel.Text = Translate("Gesamtgröße: ", "Total size: ")
            + FormatUpdateSize(release.Size);
        UpdateCardActions.Visibility = Visibility.Collapsed;
        UpdateCardCloseButton.Visibility = Visibility.Collapsed;
        UpdateDownloadPanel.Visibility = Visibility.Visible;
        UpdateDownloadProgressBar.Value = 0;
        UpdateDownloadProgressText.Text = FormatDownloadProgress(0, release.Size, 0);
        UpdateCancelDownloadButton.IsEnabled = true;
        UpdateCancelDownloadButton.Content = Translate("Download abbrechen", "Cancel download");
        UpdateCancelDownloadButton.Visibility = Visibility.Visible;
        UpdateCard.Visibility = Visibility.Visible;

        var progress = new Progress<double>(percent =>
        {
            if (_updateWindowClosed || !_isDownloadingUpdate)
            {
                return;
            }

            UpdateDownloadProgressBar.Value = Math.Clamp(percent, 0d, 100d);
            long receivedBytes = (long)(release.Size * Math.Clamp(percent, 0d, 100d) / 100d);
            UpdateDownloadProgressText.Text = FormatDownloadProgress(
                receivedBytes, release.Size, percent);
        });

        string? installer = null;
        string? error = null;
        bool cancelled = false;
        try
        {
            installer = await _updateService.DownloadInstallerAsync(
                release, progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            _activeUpdateDownloadCancellation = null;
            _isDownloadingUpdate = false;
            if (!_updateWindowClosed)
            {
                UpdateCancelDownloadButton.Visibility = Visibility.Collapsed;
                UpdateDownloadPanel.Visibility = Visibility.Collapsed;
            }
        }

        if (_updateWindowClosed)
        {
            return;
        }

        if (cancelled)
        {
            ShowUpdateOffer(release);
            UpdateCardDescription.Text = Translate(
                "Download abgebrochen.",
                "Download cancelled.");
            return;
        }

        if (error is not null || installer is null)
        {
            ShowUpdateOffer(release);
            UpdateCardDescription.Text = Translate(
                "Download fehlgeschlagen: ", "Download failed: ") + (error ?? "Unbekannter Fehler");
            return;
        }

        try
        {
            if (Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    UseShellExecute = true
                }) is null)
            {
                throw new InvalidOperationException("Der Installer konnte nicht gestartet werden.");
            }

            // Inno Setup can replace the application files once MeetingRecall exits.
            Application.Current.Exit();
        }
        catch (Exception exception)
        {
            ShowUpdateOffer(release);
            UpdateCardDescription.Text = Translate(
                "Installation konnte nicht gestartet werden: ",
                "Installer could not be started: ") + exception.Message;
        }
    }

    private void ShowUpdateMessage(string title, string message)
    {
        if (_updateWindowClosed || _isDownloadingUpdate)
        {
            return;
        }

        _offeredUpdate = null;
        UpdateCardTitle.Text = title;
        UpdateCardDescription.Text = message;
        UpdateCardSizeLabel.Visibility = Visibility.Collapsed;
        UpdateCardActions.Visibility = Visibility.Collapsed;
        UpdateDownloadPanel.Visibility = Visibility.Collapsed;
        UpdateCancelDownloadButton.Visibility = Visibility.Collapsed;
        UpdateCardCloseButton.Visibility = Visibility.Visible;
        UpdateCard.Visibility = Visibility.Visible;
    }

    private Task ShowUpdateMessageAsync(string title, string message)
    {
        ShowUpdateMessage(title, message);
        return Task.CompletedTask;
    }
}
