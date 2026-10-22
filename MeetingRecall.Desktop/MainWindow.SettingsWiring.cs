using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingRecall.Core.Backup;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private bool _settingsMenuWiringInitialized;

    public void InitializeSettingsMenuWiring()
    {
        if (_settingsMenuWiringInitialized)
        {
            return;
        }

        _settingsMenuWiringInitialized = true;
        _dataPortabilityService = App.Services.GetRequiredService<IApplicationDataPortabilityService>();
        _settingsButton = AppTitleBar.Children.OfType<Button>().LastOrDefault();
        if (_settingsButton is null)
        {
            throw new InvalidOperationException("Die Schaltfläche für die Einstellungen wurde nicht gefunden.");
        }

        _settingsButton.UseSystemFocusVisuals = false;
        _settingsButton.Click += SettingsMenuButton_Click;
        Closed += SettingsMenuWiringWindow_Closed;
    }

    private async void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataPortabilityOperationRunning)
        {
            return;
        }

        await ShowApplicationSettingsPageAsync();
    }

    private void SettingsMenuWiringWindow_Closed(object sender, WindowEventArgs args)
    {
        CloseApplicationSettingsPage();

        if (_settingsButton is not null)
        {
            _settingsButton.Click -= SettingsMenuButton_Click;
        }
    }
}
