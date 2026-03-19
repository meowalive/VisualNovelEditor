using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using VNEditor.ViewModels;

namespace VNEditor.Views;

public partial class ScenePreviewWindow : Window
{
    public ScenePreviewWindow()
    {
        InitializeComponent();
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ScenePreviewViewModel vm)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            vm.LeftClickCommand.Execute(null);
        }
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInteractiveElement(e.Source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaxRestoreClicked(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static bool IsInteractiveElement(object? source)
    {
        var element = source as StyledElement;
        while (element != null)
        {
            if (element is Button or Menu or MenuItem)
            {
                return true;
            }

            element = element.Parent as StyledElement;
        }

        return false;
    }
}
