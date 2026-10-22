using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MeetingRecall.Desktop.Services;
using MeetingRecall.Infrastructure.DependencyInjection;
using MeetingRecall.Infrastructure.Persistence;
using WinRT.Interop;

namespace MeetingRecall.Desktop;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private Window? _window;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddMeetingRecallInfrastructure();
        services.AddSingleton<AudioImportService>();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public static IServiceProvider Services => ((App)Current)._services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services.GetRequiredService<IDatabaseInitializer>().Initialize();

        var mainWindow = new MainWindow();
        mainWindow.InitializeApplicationSettings();
        mainWindow.InitializeUiPolish();
        mainWindow.InitializeNativeRecording();
        mainWindow.InitializeTranscription();
        mainWindow.InitializeMeetingIntelligence();
        mainWindow.InitializeConversationQuestions();
        mainWindow.InitializeSettingsMenuWiring();
        mainWindow.InitializeFocusVisualPolicy();
        _window = mainWindow;
        _window.Activate();

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        mainWindow.StartUpdateChecking();
    }
}
