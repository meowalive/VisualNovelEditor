using System.ComponentModel;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Linq;
using VNEditor.ViewModels;
using ACPColorPickerWindow = AvaloniaColorPicker.ColorPickerWindow;

namespace VNEditor.Views;

public partial class SettingsWindow : Window
{
    private MainWindowViewModel? _boundVm;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => { try { ApplyTransparencyLevel(); } catch { } };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm = null;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            ApplyTransparencyLevel();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.WindowBlurLevel))
            ApplyTransparencyLevel();
    }

    private static readonly IBrush BlurBaseBrush = new SolidColorBrush(Color.FromArgb(217, 0x1E, 0x1E, 0x1E));

    private void ApplyTransparencyLevel()
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            var level = vm.GetWindowTransparencyLevel();
            TransparencyLevelHint = new[] { level };
            ClearValue(BackgroundProperty); // 清除 XAML 绑定，避免覆盖亚克力/模糊
            if (level != Avalonia.Controls.WindowTransparencyLevel.None)
                Background = BlurBaseBrush;
            else
                this.Bind(Window.BackgroundProperty, new Avalonia.Data.Binding(nameof(MainWindowViewModel.ThemeWindowBackground)) { Source = vm });
        }
        catch { /* 透明/模糊不可用时跳过 */ }
    }

    private async void OnPickBackgroundClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择编辑器背景图",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.EditorBackgroundPath = path;
            vm.ApplyEditorBackgroundCommand.Execute(null);
        }
    }

    private async void OnOpenTintPickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var pickerWindow = new ACPColorPickerWindow(vm.EditorBackgroundTint);
        pickerWindow.IsPaletteVisible = true;
        pickerWindow.IsAlphaVisible = false;
        pickerWindow.IsRGBVisible = false;
        pickerWindow.IsColourBlindnessSelectorVisible = false;
        pickerWindow.IsHexVisible = false;
        pickerWindow.IsCIELABVisible = false;
        pickerWindow.IsHSBVisible = false;
        pickerWindow.IsColourSpacePreviewVisible = false;
        pickerWindow.IsColourSpaceSelectorVisible = false;
        pickerWindow.IsCIELABSelectable = false;
        pickerWindow.IsHSBSelectable = false;
        pickerWindow.IsRGBSelectable = true;

        var selectedColor = await pickerWindow.ShowDialog(this);
        if (selectedColor.HasValue)
        {
            ApplyPickedTint(vm, selectedColor.Value);
            return;
        }

        if ((object?)pickerWindow.Color is Color fallbackColor)
        {
            ApplyPickedTint(vm, fallbackColor);
        }
    }

    private static void ApplyPickedTint(MainWindowViewModel vm, Color color)
    {
        // Theme tint only uses hex RGB; opacity is controlled by separate slider.
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        vm.EditorBackgroundTintColorText = hex;
        if (Color.TryParse(hex, out var parsed))
        {
            vm.EditorBackgroundTint = parsed;
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
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
