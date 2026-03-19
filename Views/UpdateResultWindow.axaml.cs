using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VNEditor.Views;

public partial class UpdateResultWindow : Window
{
    public string MessageText { get; }

    public UpdateResultWindow()
    {
        MessageText = string.Empty;
        InitializeComponent();
        DataContext = this;
    }

    public UpdateResultWindow(string message)
    {
        MessageText = message;
        InitializeComponent();
        DataContext = this;
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
