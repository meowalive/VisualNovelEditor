using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Lua;
using Lua.Standard;

namespace VNEditor.Services;

public sealed record ScriptMethodDef(string Name, string? Alias, string Params, string LuaBody);

public static class LuaScriptRuntimeService
{
    public static readonly ScriptMethodDef[] MethodDefs =
    {
        new("ShowDialogue", "跳转",    "id",   """__vn_set_action("Jump", id)"""),
        new("EndDialogue",  "结束",    "",     """__vn_set_action("EndDialogue", "")"""),
        new("HideDialogue", "隐藏对话框", "flag", "if flag == nil then flag = true end __vn_hide_dialogue = flag"),
    };

    private static readonly Regex InvocationRegex = new(
        @"([A-Za-z_\u0080-\uFFFF][A-Za-z0-9_\u0080-\uFFFF]*(?:\.[A-Za-z_\u0080-\uFFFF][A-Za-z0-9_\u0080-\uFFFF]*)?)\s*\(",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedInvocations = new(
        MethodDefs.SelectMany(d => d.Alias != null ? new[] { d.Name, d.Alias } : new[] { d.Name }),
        StringComparer.Ordinal);

    private static readonly string Prelude = BuildPrelude();

    private static string BuildPrelude()
    {
        var sb = new StringBuilder();
        sb.AppendLine("__vn_action_type = nil");
        sb.AppendLine("__vn_action_target = nil");
        sb.AppendLine("__vn_hide_dialogue = nil");
        sb.AppendLine("local __vn_self = {}");
        sb.AppendLine("self = __vn_self");
        sb.AppendLine("VisualNovelScriptExecutor = __vn_self");
        sb.AppendLine("PlayerInfo = setmetatable({}, { __index = function(_, _) return function(...) return nil end end })");
        sb.AppendLine("local function __vn_set_action(t, target) if __vn_action_type == nil then __vn_action_type = t __vn_action_target = target end end");
        foreach (var def in MethodDefs)
        {
            sb.AppendLine($"function {def.Name}({def.Params}) {def.LuaBody} end");
            sb.AppendLine($"__vn_self.{def.Name} = {def.Name}");
            if (!string.IsNullOrEmpty(def.Alias))
            {
                sb.AppendLine($"__vn_self[\"{def.Alias}\"] = {def.Name}");
            }
        }
        return sb.ToString();
    }

    /// <summary>将脚本中的中文别名替换为对应英文方法名，使 Lua 解析器能正确处理。</summary>
    public static string NormalizeAliases(string script)
    {
        foreach (var def in MethodDefs)
        {
            if (!string.IsNullOrEmpty(def.Alias))
            {
                script = script.Replace(def.Alias, def.Name);
            }
        }
        return script;
    }

    public static IReadOnlyList<string> ValidateSyntax(string? script)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(script))
        {
            return errors;
        }

        try
        {
            using var state = LuaState.Create();
            state.OpenStandardLibraries();
            // 仅做 Lua 语法编译检查，不执行用户脚本，避免 ShowDialogue 等运行时依赖报“未定义”。
            var normalizedScript = NormalizeAliases(VNLuaFormatter.Format(script));
            var wrapped = "function __vn_syntax_check__()\n" + normalizedScript + "\nend";
            _ = state.DoStringAsync(wrapped).GetAwaiter().GetResult();
        }
        catch (LuaCompileException ex)
        {
            errors.Add(ex.Message);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        if (errors.Count == 0)
        {
            var normalizedScript = NormalizeAliases(VNLuaFormatter.Format(script));
            var callError = ValidateAllowedInvocation(normalizedScript);
            if (!string.IsNullOrWhiteSpace(callError))
            {
                errors.Add(callError);
            }
        }

        return errors;
    }

    public static DialogueScriptAction ExecuteAndExtractFirstAction(string? script)
    {
        _ = TryExecuteAndExtractFirstAction(script, out var action, out _);
        return action;
    }

    public static bool TryExecuteAndExtractFirstAction(string? script, out DialogueScriptAction action, out string? error)
    {
        action = DialogueScriptAction.None;
        error = null;
        if (string.IsNullOrWhiteSpace(script))
        {
            return true;
        }

        var normalizedScript = NormalizeAliases(VNLuaFormatter.Format(script));
        var callError = ValidateAllowedInvocation(normalizedScript);
        if (!string.IsNullOrWhiteSpace(callError))
        {
            error = callError;
            return false;
        }

        try
        {
            using var state = LuaState.Create();
            state.OpenStandardLibraries();
            _ = state.DoStringAsync(Prelude + Environment.NewLine + normalizedScript).GetAwaiter().GetResult();

            var actionTypeValue = state.Environment["__vn_action_type"];
            if (!actionTypeValue.TryRead<string>(out var actionType) || string.IsNullOrWhiteSpace(actionType))
            {
                error = "脚本未调用 ShowDialogue/跳转 或 EndDialogue/结束，无法模拟";
                return false;
            }

            var targetValue = state.Environment["__vn_action_target"];
            targetValue.TryRead<string>(out var targetId);

            action = actionType.Equals("Jump", StringComparison.OrdinalIgnoreCase)
                ? new DialogueScriptAction(DialogueScriptActionType.Jump, targetId ?? string.Empty)
                : actionType.Equals("EndDialogue", StringComparison.OrdinalIgnoreCase)
                    ? new DialogueScriptAction(DialogueScriptActionType.EndDialogue, string.Empty)
                    : DialogueScriptAction.None;
            return true;
        }
        catch (Exception ex)
        {
            error = GetExceptionMessage(ex);
            return false;
        }
    }

    public static bool? ExtractHideDialogue(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var normalizedScript = NormalizeAliases(VNLuaFormatter.Format(script));
        try
        {
            using var state = LuaState.Create();
            state.OpenStandardLibraries();
            _ = state.DoStringAsync(Prelude + Environment.NewLine + normalizedScript).GetAwaiter().GetResult();

            var hideValue = state.Environment["__vn_hide_dialogue"];
            if (hideValue.TryRead<bool>(out var hide))
            {
                return hide;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetExceptionMessage(Exception ex)
    {
        var msg = ex?.Message;
        if (!string.IsNullOrWhiteSpace(msg))
            return msg.Trim();
        var inner = ex?.InnerException?.Message;
        if (!string.IsNullOrWhiteSpace(inner))
            return inner.Trim();
        var full = ex?.ToString();
        if (!string.IsNullOrWhiteSpace(full))
            return full.Trim();
        return "未知错误";
    }

    private static string? ValidateAllowedInvocation(string script)
    {
        foreach (Match match in InvocationRegex.Matches(script))
        {
            var name = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var start = match.Index;
            var prev = start > 0 ? script[start - 1] : '\0';
            if (prev == ':')
            {
                continue;
            }

            var before = script[..start].TrimEnd();
            if (before.EndsWith("function", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.StartsWith("PlayerInfo.", StringComparison.Ordinal))
            {
                continue;
            }

            if (AllowedInvocations.Contains(name))
            {
                continue;
            }

            return $"未注册函数调用: {name}";
        }

        return null;
    }
}
