using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;

namespace VNEditor.Models;

public partial class DialogueScene : ObservableObject
{
    [ObservableProperty] private string name = "NewScene";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string previewText = string.Empty;
    [ObservableProperty] private Bitmap? galleryBackground;
    [ObservableProperty] private Bitmap? galleryPortrait1;
    [ObservableProperty] private Bitmap? galleryPortrait2;
    [ObservableProperty] private Bitmap? gallerySinglePortrait;
    [ObservableProperty] private bool galleryPortrait1Visible;
    [ObservableProperty] private bool galleryPortrait2Visible;
    [ObservableProperty] private bool galleryPortrait1Dim;
    [ObservableProperty] private bool galleryPortrait2Dim;
    [ObservableProperty] private bool galleryUseSinglePortrait;
    [ObservableProperty] private bool galleryUseDualPortrait;
    [ObservableProperty] private bool gallerySinglePortraitDim;
    [ObservableProperty] private double galleryPortrait1OffsetY;
    [ObservableProperty] private double galleryPortrait2OffsetY;
    [ObservableProperty] private double gallerySinglePortraitOffsetY;
    [ObservableProperty] private double galleryPortrait1Scale = 1.0;
    [ObservableProperty] private double galleryPortrait2Scale = 1.0;
    [ObservableProperty] private double gallerySinglePortraitScale = 1.0;

    public ObservableCollection<DialogueLine> Lines { get; } = new();

    public string DisplayName => IsDirty ? $"{Name}*" : Name;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}
