using CommunityToolkit.Mvvm.ComponentModel;

namespace VNEditor.Models;

public partial class RoleEntry : ObservableObject
{
    [ObservableProperty] private string category = "role";
    [ObservableProperty] private string id = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string nameEn = string.Empty;
    [ObservableProperty] private string nameZhHant = string.Empty;
    [ObservableProperty] private string nameJa = string.Empty;
    [ObservableProperty] private string avatar = string.Empty;
    [ObservableProperty] private string characterImage = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
