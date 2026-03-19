namespace VNEditor.Services;

public enum DialogueScriptActionType
{
    None = 0,
    Jump = 1,
    EndDialogue = 2
}

public readonly record struct DialogueScriptAction(DialogueScriptActionType Type, string TargetId)
{
    public static readonly DialogueScriptAction None = new(DialogueScriptActionType.None, string.Empty);
}

public static class VisualNovelScriptExecutorParser
{
    public static DialogueScriptAction ParseFirstAction(string script)
    {
        return LuaScriptRuntimeService.ExecuteAndExtractFirstAction(script);
    }

    public static bool TryParseFirstAction(string script, out DialogueScriptAction action, out string? error)
    {
        return LuaScriptRuntimeService.TryExecuteAndExtractFirstAction(script, out action, out error);
    }
}
