namespace VNEditor.Models;

public class ScriptSnippetItem
{
    public string Title { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Preview => Code;
}
