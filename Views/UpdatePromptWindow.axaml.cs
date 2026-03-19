using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VNEditor.Views;

public partial class UpdatePromptWindow : Window
{
    public string MessageText { get; }

    public UpdatePromptWindow()
    {
        MessageText = string.Empty;
        InitializeComponent();
        DataContext = this;
    }

    public UpdatePromptWindow(string localSha, string remoteSha, string releasePageUrl)
    {
        MessageText = $"当前 SHA256: {ShortSha(localSha)}\n最新 SHA256: {ShortSha(remoteSha)}\n发布地址: {releasePageUrl}";
        InitializeComponent();
        DataContext = this;
    }

    private void OnLaterClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private static string ShortSha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return "N/A";
        }

        return sha.Length > 12 ? sha[..12] + "..." : sha;
    }
}
