using Avalonia.Media.Imaging;

namespace VNEditor.Models;

public class BackgroundGalleryItem
{
    public string RelativePath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public Bitmap? PreviewImage { get; init; }
}
