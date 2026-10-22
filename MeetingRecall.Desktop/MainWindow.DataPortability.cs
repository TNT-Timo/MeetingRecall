using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingRecall.Core.Backup;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private const string FactoryResetConfirmationPhrase = "DELETE ALL";

    private IApplicationDataPortabilityService? _dataPortabilityService;
    private Button? _settingsButton;
    private bool _dataPortabilityUiInitialized;
    private bool _dataPortabilityOperationRunning;
    private CancellationTokenSource? _dataPortabilityCancellation;

    public void InitializeDataPortability()
    {
        if (_dataPortabilityUiInitialized)
        {
            return;
        }

        _dataPortabilityUiInitialized = true;
        _dataPortabilityService = App.Services.GetRequiredService<IApplicationDataPortabilityService>();
        _settingsButton = AppTitleBar.Children.OfType<Button>().LastOrDefault();
        if (_settingsButton is null)
        {
            throw new InvalidOperationException("Die Schaltfläche für die Einstellungen wurde nicht gefunden.");
        }

        _settingsButton.Click += SettingsButton_Click;
        Closed += DataPortabilityWindow_Closed;
    }

    private IApplicationDataPortabilityService DataPortabilityService =>
        _dataPortabilityService
        ?? throw new InvalidOperationException("Die Datenportabilität wurde nicht initialisiert.");

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataPortabilityOperationRunning)
        {
            return;
        }

        bool resetRequested = false;
        var resetButton = new Button
        {
            Content = "MeetingRecall vollständig zurücksetzen",
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var content = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 520
        };
        content.Children.Add(new TextBlock
        {
            Text = "Eine vollständige Sicherung enthält Gespräche, Audiodateien, Transkripte, Modelle, Sprecherprofile, Einstellungen, Exporte und Logs. Temporäre Arbeitsdateien werden nicht übernommen.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Verschlüsselte Sicherungen sind für private oder geschäftliche Gesprächsdaten ausdrücklich empfohlen.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        content.Children.Add(resetButton);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Daten und Sicherung",
            Content = content,
            PrimaryButtonText = "Sicherung exportieren",
            SecondaryButtonText = "Sicherung importieren",
            CloseButtonText = "Schließen",
            DefaultButton = ContentDialogButton.Primary
        };
        resetButton.Click += (_, _) =>
        {
            resetRequested = true;
            dialog.Hide();
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (resetRequested)
        {
            await ResetAllDataAsync();
        }
        else if (result == ContentDialogResult.Primary)
        {
            await ExportCompleteBackupAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ImportCompleteBackupAsync();
        }
    }

    private async Task ExportCompleteBackupAsync()
    {
        if (!await EnsureDataPortabilityOperationCanStartAsync("Sicherung exportieren"))
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"MeetingRecall-Sicherung-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add(
            "MeetingRecall-Sicherung",
            new List<string> { ".mrbackup" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null || string.IsNullOrWhiteSpace(destination.Path))
        {
            return;
        }

        ExportSecurityChoice security = await ChooseExportSecurityAsync();
        if (!security.Confirmed)
        {
            return;
        }

        var progress = new Progress<DataPortabilityProgress>(UpdateDataPortabilityProgress);
        _dataPortabilityCancellation = new CancellationTokenSource();
        SetDataPortabilityBusy(
            true,
            "Vollständige Sicherung wird vorbereitet …",
            _dataPortabilityCancellation);
        try
        {
            BackupOperationResult result = await DataPortabilityService.ExportAsync(
                destination.Path,
                security.Password,
                progress,
                _dataPortabilityCancellation.Token);
            await ShowMessageAsync(
                "Sicherung abgeschlossen",
                $"{result.FileCount.ToString("N0", CultureInfo.CurrentCulture)} Dateien mit {FormatFileSize(result.TotalBytes)} wurden in einer {(result.Encrypted ? "verschlüsselten" : "unverschlüsselten")} MeetingRecall-Sicherung gespeichert.",
                "Schließen");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Sicherung wurde abgebrochen";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Sicherung fehlgeschlagen",
                exception.Message,
                "Schließen");
        }
        finally
        {
            _dataPortabilityCancellation.Dispose();
            _dataPortabilityCancellation = null;
            SetDataPortabilityBusy(false, StatusText.Text);
        }
    }

    private async Task ImportCompleteBackupAsync()
    {
        if (!await EnsureDataPortabilityOperationCanStartAsync("Sicherung importieren"))
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".mrbackup");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? source = await picker.PickSingleFileAsync();
        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return;
        }

        BackupInspection inspection;
        try
        {
            inspection = await DataPortabilityService.InspectAsync(source.Path);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Sicherung ungültig",
                exception.Message,
                "Schließen");
            return;
        }

        string? password = null;
        if (inspection.RequiresPassword)
        {
            PasswordPromptResult passwordResult = await AskForBackupPasswordAsync();
            if (!passwordResult.Confirmed)
            {
                return;
            }

            password = passwordResult.Password;
        }

        if (await DataPortabilityService.HasUserDataAsync()
            && !await ConfirmImportReplacementAsync(inspection))
        {
            return;
        }

        string effectiveSourcePath = source.Path;
        string? temporarySourcePath = null;
        bool sourceWasInsideDataRoot = IsInsideMeetingRecallDataRoot(source.Path);
        if (sourceWasInsideDataRoot)
        {
            temporarySourcePath = Path.Combine(
                Path.GetTempPath(),
                $"MeetingRecall-import-{Guid.NewGuid():N}.mrbackup");
            File.Copy(source.Path, temporarySourcePath, overwrite: true);
            effectiveSourcePath = temporarySourcePath;
        }

        var progress = new Progress<DataPortabilityProgress>(UpdateDataPortabilityProgress);
        _dataPortabilityCancellation = new CancellationTokenSource();
        SetDataPortabilityBusy(
            true,
            "Sicherung wird geprüft …",
            _dataPortabilityCancellation);
        try
        {
            ReleaseMediaForDataReplacement();
            BackupOperationResult result = await DataPortabilityService.ImportAsync(
                effectiveSourcePath,
                password,
                progress,
                _dataPortabilityCancellation.Token);

            if (sourceWasInsideDataRoot && temporarySourcePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source.Path)!);
                File.Copy(temporarySourcePath, source.Path, overwrite: true);
            }

            _conversationQuestionSessions.Clear();
            _conversationQuestionLoadFailures.Clear();
            await LoadConversationsAsync();
            RenderSelectedTranscript();
            await ShowMessageAsync(
                "Sicherung wiederhergestellt",
                $"{result.FileCount.ToString("N0", CultureInfo.CurrentCulture)} Dateien mit {FormatFileSize(result.TotalBytes)} wurden vollständig wiederhergestellt. Der vorherige lokale Zustand wurde ersetzt.",
                "Schließen");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Wiederherstellung wurde abgebrochen";
        }
        catch (UnauthorizedAccessException exception)
        {
            await ShowMessageAsync(
                "Sicherung konnte nicht entsperrt werden",
                exception.Message,
                "Schließen");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Wiederherstellung fehlgeschlagen",
                $"{exception.Message}\n\nDer vorherige lokale Zustand wurde nicht absichtlich verändert; ein begonnener Austausch wird automatisch zurückgerollt.",
                "Schließen");
        }
        finally
        {
            TryDeleteTemporaryBackup(temporarySourcePath);
            _dataPortabilityCancellation.Dispose();
            _dataPortabilityCancellation = null;
            SetDataPortabilityBusy(false, StatusText.Text);
        }
    }

    private async Task ResetAllDataAsync()
    {
        if (!await EnsureDataPortabilityOperationCanStartAsync("MeetingRecall zurücksetzen"))
        {
            return;
        }

        if (!await DataPortabilityService.HasUserDataAsync())
        {
            await ShowMessageAsync(
                "Bereits im Grundzustand",
                "MeetingRecall enthält noch keine persönlichen Daten, Modelle oder geänderten lokalen Inhalte.",
                "Schließen");
            return;
        }

        if (!await ConfirmFactoryResetAsync())
        {
            return;
        }

        var progress = new Progress<DataPortabilityProgress>(UpdateDataPortabilityProgress);
        _dataPortabilityCancellation = new CancellationTokenSource();
        SetDataPortabilityBusy(
            true,
            "MeetingRecall wird vollständig zurückgesetzt …",
            _dataPortabilityCancellation);
        try
        {
            ReleaseMediaForDataReplacement();
            await DataPortabilityService.ResetAllAsync(
                progress,
                _dataPortabilityCancellation.Token);
            _conversationQuestionSessions.Clear();
            _conversationQuestionLoadFailures.Clear();
            await LoadConversationsAsync();
            RenderSelectedTranscript();
            await ShowMessageAsync(
                "MeetingRecall wurde zurückgesetzt",
                "Alle Gespräche, Audiodateien, Transkripte, Modelle, Sprecherprofile, Einstellungen, Exporte, Logs und sonstigen lokalen Daten wurden entfernt. Die leere Datenbank wurde neu angelegt.",
                "Schließen");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Zurücksetzen wurde abgebrochen";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Zurücksetzen fehlgeschlagen",
                $"{exception.Message}\n\nDer vorherige Zustand wurde nach Möglichkeit automatisch wiederhergestellt.",
                "Schließen");
        }
        finally
        {
            _dataPortabilityCancellation.Dispose();
            _dataPortabilityCancellation = null;
            SetDataPortabilityBusy(false, StatusText.Text);
        }
    }

    private async Task<bool> EnsureDataPortabilityOperationCanStartAsync(string operation)
    {
        if (_dataPortabilityOperationRunning || _isBusy)
        {
            await ShowMessageAsync(
                operation,
                "Bitte warte, bis der aktuelle Vorgang abgeschlossen ist.",
                "Schließen");
            return false;
        }

        if (_nativeRecordingService?.IsRecording == true)
        {
            await ShowMessageAsync(
                operation,
                "Beende oder verwirf zuerst die laufende Aufnahme.",
                "Schließen");
            return false;
        }

        if (HasProcessingJobs)
        {
            await ShowMessageAsync(
                operation,
                Translate(
                    "Beende oder brich zuerst alle laufenden und wartenden Verarbeitungen über die Warteschlange ab.",
                    "Finish or cancel all active and queued processing jobs first."),
                Translate("Schließen", "Close"));
            return false;
        }

        return true;
    }

    private async Task<ExportSecurityChoice> ChooseExportSecurityAsync()
    {
        var encryptCheckBox = new CheckBox
        {
            Content = "Mit Passwort verschlüsseln (empfohlen)",
            IsChecked = true
        };
        var passwordBox = new PasswordBox
        {
            Header = "Passwort",
            PlaceholderText = "Mindestens 8 Zeichen"
        };
        var confirmationBox = new PasswordBox
        {
            Header = "Passwort bestätigen"
        };
        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "Die Sicherung kann private Gespräche, Stimmen, Namen und geschäftliche Informationen enthalten.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(encryptCheckBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(confirmationBox);
        panel.Children.Add(validationText);

        void UpdatePasswordFields()
        {
            bool enabled = encryptCheckBox.IsChecked == true;
            passwordBox.IsEnabled = enabled;
            confirmationBox.IsEnabled = enabled;
            validationText.Text = string.Empty;
        }

        encryptCheckBox.Checked += (_, _) => UpdatePasswordFields();
        encryptCheckBox.Unchecked += (_, _) => UpdatePasswordFields();

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Sicherung schützen",
            Content = panel,
            PrimaryButtonText = "Weiter",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (encryptCheckBox.IsChecked != true)
            {
                return;
            }

            if (passwordBox.Password.Length < 8)
            {
                validationText.Text = "Das Passwort muss mindestens 8 Zeichen enthalten.";
                args.Cancel = true;
                return;
            }

            if (!string.Equals(passwordBox.Password, confirmationBox.Password, StringComparison.Ordinal))
            {
                validationText.Text = "Die beiden Passwörter stimmen nicht überein.";
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return new ExportSecurityChoice(false, null);
        }

        if (encryptCheckBox.IsChecked == true)
        {
            return new ExportSecurityChoice(true, passwordBox.Password);
        }

        var warning = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Unverschlüsselt exportieren?",
            Content = "Jede Person mit Zugriff auf die Datei kann alle enthaltenen Gespräche, Transkripte, Namen und Modelle lesen beziehungsweise abspielen.",
            PrimaryButtonText = "Unverschlüsselt exportieren",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close
        };
        return await warning.ShowAsync() == ContentDialogResult.Primary
            ? new ExportSecurityChoice(true, null)
            : new ExportSecurityChoice(false, null);
    }

    private async Task<PasswordPromptResult> AskForBackupPasswordAsync()
    {
        var passwordBox = new PasswordBox
        {
            Header = "Sicherungspasswort",
            MinWidth = 380
        };
        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(passwordBox);
        panel.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Verschlüsselte Sicherung",
            Content = panel,
            PrimaryButtonText = "Entsperren",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrEmpty(passwordBox.Password))
            {
                validationText.Text = "Gib das Passwort der Sicherung ein.";
                args.Cancel = true;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? new PasswordPromptResult(true, passwordBox.Password)
            : new PasswordPromptResult(false, string.Empty);
    }

    private async Task<bool> ConfirmImportReplacementAsync(BackupInspection inspection)
    {
        string details = inspection.CreatedAtUtc is DateTimeOffset createdAt
            ? $"\n\nSicherung vom {createdAt.ToLocalTime():g} · {inspection.FileCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} Dateien · {(inspection.TotalBytes is long bytes ? FormatFileSize(bytes) : "Größe unbekannt")}"
            : string.Empty;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Vorhandene Daten vollständig ersetzen?",
            Content = "Diese Sicherung ersetzt alle derzeitigen Gespräche, Audiodateien, Transkripte, Modelle, Sprecherprofile, Einstellungen, Exporte, Logs und sonstigen lokalen Daten. Es werden keine einzelnen Inhalte zusammengeführt." + details,
            PrimaryButtonText = "Alles ersetzen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmFactoryResetAsync()
    {
        var confirmationBox = new TextBox
        {
            Header = $"Zur Bestätigung {FactoryResetConfirmationPhrase} eingeben",
            PlaceholderText = FactoryResetConfirmationPhrase,
            MinWidth = 420
        };
        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Dieser Vorgang entfernt unwiderruflich alle Gespräche, Audiodateien, Transkripte, Modelle, Sprecherprofile, Einstellungen, Exporte, Logs und sonstigen lokalen Daten.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(confirmationBox);
        panel.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "MeetingRecall auf Werkseinstellungen zurücksetzen?",
            Content = panel,
            PrimaryButtonText = "Endgültig zurücksetzen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.Equals(
                    confirmationBox.Text.Trim(),
                    FactoryResetConfirmationPhrase,
                    StringComparison.Ordinal))
            {
                validationText.Text = $"Gib zur Bestätigung exakt {FactoryResetConfirmationPhrase} ein.";
                args.Cancel = true;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void UpdateDataPortabilityProgress(DataPortabilityProgress progress)
    {
        string suffix = progress.TotalFiles > 0
            ? $" · {progress.ProcessedFiles.ToString("N0", CultureInfo.CurrentCulture)}/{progress.TotalFiles.ToString("N0", CultureInfo.CurrentCulture)}"
            : string.Empty;
        StatusText.Text = progress.Stage switch
        {
            DataPortabilityStage.Inspecting => "Sicherung wird geprüft …",
            DataPortabilityStage.Snapshotting => "Lokaler Zustand wird eingefroren …" + suffix,
            DataPortabilityStage.Hashing => "Prüfsummen werden berechnet …" + suffix,
            DataPortabilityStage.Packaging => "Sicherung wird geschrieben …" + suffix,
            DataPortabilityStage.Validating => "Sicherung wird vollständig validiert …" + suffix,
            DataPortabilityStage.Restoring => "Lokaler Zustand wird ersetzt …",
            DataPortabilityStage.Resetting => "Alle lokalen Daten werden entfernt …",
            DataPortabilityStage.Completed => string.Empty,
            _ => "Daten werden verarbeitet …"
        };
    }

    private void SetDataPortabilityBusy(
        bool isBusy,
        string status,
        CancellationTokenSource? cancellation = null)
    {
        _dataPortabilityOperationRunning = isBusy;
        SetBusy(isBusy, status, cancellation);
        if (_settingsButton is not null)
        {
            _settingsButton.IsEnabled = !isBusy;
        }
    }

    private void ReleaseMediaForDataReplacement()
    {
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        ResetPlaybackUi();
    }

    private bool IsInsideMeetingRecallDataRoot(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        return IsInsideRoot(fullPath, _paths.RootDirectory)
            || IsInsideRoot(fullPath, ResolvePhysicalDataRoot());
    }

    private static bool IsInsideRoot(string fullPath, string rootPath)
    {
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteTemporaryBackup(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary cleanup is best effort.
        }
    }

    private void DataPortabilityWindow_Closed(object sender, WindowEventArgs args)
    {
        _dataPortabilityCancellation?.Cancel();
        _dataPortabilityCancellation?.Dispose();
        _dataPortabilityCancellation = null;
        if (_settingsButton is not null)
        {
            _settingsButton.Click -= SettingsButton_Click;
        }
    }

    private sealed record ExportSecurityChoice(bool Confirmed, string? Password);

    private sealed record PasswordPromptResult(bool Confirmed, string Password);
}
