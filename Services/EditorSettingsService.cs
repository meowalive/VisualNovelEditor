using System;
using System.IO;
using System.Text.Json;

namespace VNEditor.Services;

public class EditorSettings
{
    public string LastOpenedProjectPath { get; set; } = string.Empty;
    public string EditorBackgroundPath { get; set; } = string.Empty;
    public double GlobalFontSize { get; set; } = 14;
    public string EditorBackgroundTintColor { get; set; } = "#000000";
    public double EditorBackgroundTintOpacity { get; set; } = 0.25;
    public string ThemeMode { get; set; } = "黑夜";
    /// <summary>窗口整体透明度，0.2～1</summary>
    public double WindowOpacity { get; set; } = 1.0;
    /// <summary>窗口模糊/毛玻璃：0=无，1=模糊，2=亚克力</summary>
    public int WindowBlurLevel { get; set; } = 0;
}

public static class EditorSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "vneditor.settings.json");

    public static EditorSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new EditorSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<EditorSettings>(json);
            return settings ?? new EditorSettings();
        }
        catch
        {
            return new EditorSettings();
        }
    }

    public static void Save(EditorSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save failures to avoid blocking editor usage.
        }
    }
}
