using System.Text.RegularExpressions;

namespace VNEditor.Services;

public static class DialogueTextUtility
{
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public static int CountVisibleCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return TagRegex.Replace(text, string.Empty)
            .Replace("\r\n", "\n", System.StringComparison.Ordinal)
            .Length;
    }
}
