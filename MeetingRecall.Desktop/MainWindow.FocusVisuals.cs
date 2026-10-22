using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeetingRecall.Desktop;

public sealed partial class MainWindow
{
    private bool _focusVisualPolicyInitialized;

    public void InitializeFocusVisualPolicy()
    {
        if (_focusVisualPolicyInitialized)
        {
            return;
        }

        _focusVisualPolicyInitialized = true;
        RootGrid.Loaded += FocusVisualRootGrid_Loaded;
        ConversationsList.ContainerContentChanging += ConversationsList_ContainerContentChanging;
        Closed += FocusVisualWindow_Closed;
    }

    private void FocusVisualRootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        DisableSystemFocusVisuals(RootGrid);

        // Some controls, such as the transcript panel, are installed by another Loaded handler.
        DispatcherQueue.TryEnqueue(() => DisableSystemFocusVisuals(RootGrid));
    }

    private static void ConversationsList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.UseSystemFocusVisuals = false;
    }

    private static void DisableSystemFocusVisuals(DependencyObject root)
    {
        if (root is Control control)
        {
            control.UseSystemFocusVisuals = false;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DisableSystemFocusVisuals(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void FocusVisualWindow_Closed(object sender, WindowEventArgs args)
    {
        RootGrid.Loaded -= FocusVisualRootGrid_Loaded;
        ConversationsList.ContainerContentChanging -= ConversationsList_ContainerContentChanging;
    }
}
