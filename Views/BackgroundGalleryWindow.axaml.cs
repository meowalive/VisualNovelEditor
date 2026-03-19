using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VNEditor.Models;

namespace VNEditor.Views;

public partial class BackgroundGalleryWindow : Window
{
    public ObservableCollection<BackgroundGalleryItem> Items { get; } = new();

    public string? SelectedBackgroundPath { get; private set; }

    public BackgroundGalleryWindow()
    {
        InitializeComponent();
    }

    public BackgroundGalleryWindow(IEnumerable<BackgroundGalleryItem> items)
        : this()
    {
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    private void OnBackgroundItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BackgroundGalleryItem item })
        {
            SelectedBackgroundPath = item.RelativePath;
            Close(true);
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
        Close(false);
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
