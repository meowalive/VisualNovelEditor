using System.Collections.Generic;

namespace VNEditor.Services;

public static class ScriptSyntaxValidator
{
    public static IReadOnlyList<string> Validate(string? script)
    {
        return LuaScriptRuntimeService.ValidateSyntax(script);
    }
}
