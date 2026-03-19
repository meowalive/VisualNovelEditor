using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using VNEditor.Models;

namespace VNEditor.Services;

public static class DialogueProjectService
{
    private const string BgMetaPrefix = "//VNEditor:BG=";

    public static (string dataDir, string textDir, string projectRoot)? ResolveProjectDirs(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
        {
            return null;
        }

        var directData = Path.Combine(selectedPath, "Data", "Dialogue");
        var directText = Path.Combine(selectedPath, "Text", "Dialogue");
        if (Directory.Exists(directData) && Directory.Exists(directText))
        {
            return (directData, directText, selectedPath);
        }

        if (Path.GetFileName(selectedPath).Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(selectedPath);
            if (parent != null && parent.Name.Equals("Data", StringComparison.OrdinalIgnoreCase))
            {
                var root = parent.Parent?.FullName;
                if (!string.IsNullOrEmpty(root))
                {
                    var textDir = Path.Combine(root, "Text", "Dialogue");
                    if (Directory.Exists(textDir))
                    {
                        return (selectedPath, textDir, root);
                    }
                }
            }

            if (parent != null && parent.Name.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                var root = parent.Parent?.FullName;
                if (!string.IsNullOrEmpty(root))
                {
                    var dataDir = Path.Combine(root, "Data", "Dialogue");
                    if (Directory.Exists(dataDir))
                    {
                        return (dataDir, selectedPath, root);
                    }
                }
            }
        }

        return null;
    }

    public static ObservableCollection<DialogueScene> LoadScenes(string dataDir, string textDir)
    {
        var scenes = new ObservableCollection<DialogueScene>();

        var dataFiles = BuildCsvMap(dataDir);
        var textFiles = BuildCsvMap(textDir);

        var sceneNames = dataFiles.Keys.Union(textFiles.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        foreach (var sceneName in sceneNames)
        {
            dataFiles.TryGetValue(sceneName, out var dataPath);
            textFiles.TryGetValue(sceneName, out var textPath);
            scenes.Add(LoadScene(sceneName, dataPath, textPath));
        }

        return scenes;
    }

    public static void ExportScenes(IEnumerable<DialogueScene> scenes, string outputRoot)
    {
        var dataDir = Path.Combine(outputRoot, "Data", "Dialogue");
        var textDir = Path.Combine(outputRoot, "Text", "Dialogue");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(textDir);

        foreach (var scene in scenes)
        {
            ExportScene(scene, dataDir, textDir);
        }
    }

    public static void ExportScene(
        DialogueScene scene,
        string dataDir,
        string textDir,
        ISet<string>? validRoleIds = null)
    {
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(textDir);

        var maxChoice = Math.Clamp(scene.Lines.Count == 0 ? 0 : scene.Lines.Max(x => x.ChoiceCount), 0, 4);
        var dataRows = new List<string[]> { BuildDataHeader(maxChoice), BuildDataDesc(maxChoice) };
        var textRows = new List<string[]> { BuildTextHeader(maxChoice), BuildTextDesc(maxChoice) };

        foreach (var line in scene.Lines)
        {
            var baseScript = BuildScriptWithMetadata(line.BaseScript, line.BackgroundPath);
            var dataRow = new List<string>
            {
                line.CsvId,
                baseScript,
                line.EndScript,
                NormalizeRolesForExport(line.Roles, validRoleIds),
                line.IsNarrator ? "TRUE" : "FALSE",
                line.EventName,
                line.ChoiceCount.ToString()
            };
            for (var i = 1; i <= maxChoice; i++)
            {
                dataRow.Add(GetChoiceScript(line, i));
            }
            dataRows.Add(dataRow.ToArray());

            var textRow = new List<string>
            {
                line.CsvId,
                line.Text,
                line.TextEn,
                line.TextJa
            };
            for (var i = 1; i <= maxChoice; i++)
            {
                textRow.Add(GetChoiceText(line, i, "zh"));
                textRow.Add(GetChoiceText(line, i, "en"));
                textRow.Add(GetChoiceText(line, i, "ja"));
            }
            textRow.Add(string.Empty);
            textRow.Add(string.Empty);
            textRow.Add(string.Empty);
            textRows.Add(textRow.ToArray());
        }

        CsvUtility.WriteAllRows(Path.Combine(dataDir, $"{scene.Name}.csv"), dataRows);
        CsvUtility.WriteAllRows(Path.Combine(textDir, $"{scene.Name}.csv"), textRows);
    }

    public static Dictionary<string, string> LoadRoleCharacterMap(string projectRoot)
    {
        var roleDir = Path.Combine(projectRoot, "Data", "RoleData");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(roleDir))
        {
            return map;
        }

        foreach (var file in Directory.GetFiles(roleDir, "*.csv"))
        {
            var rows = CsvUtility.ReadAllRows(file);
            if (rows.Count < 3)
            {
                continue;
            }

            var header = rows[0];
            var idIdx = FindColumn(header, "Id");
            var charIdx = FindColumn(header, "CharacterImage");
            if (idIdx < 0 || charIdx < 0)
            {
                continue;
            }

            for (var i = 2; i < rows.Count; i++)
            {
                var row = rows[i];
                var id = GetCell(row, idIdx);
                var img = GetCell(row, charIdx);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(img))
                {
                    continue;
                }

                map[id] = img;
                map[$"role_{id}"] = img;
            }
        }

        return map;
    }

    public static Dictionary<string, string> LoadRoleNameMap(string projectRoot)
    {
        var roleDir = Path.Combine(projectRoot, "Text", "RoleData");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(roleDir))
        {
            return map;
        }

        foreach (var file in Directory.GetFiles(roleDir, "*.csv"))
        {
            var rows = CsvUtility.ReadAllRows(file);
            if (rows.Count < 3)
            {
                continue;
            }

            var header = rows[0];
            var idIdx = FindColumn(header, "Id");
            var nameIdx = FindColumn(header, "Name");
            if (idIdx < 0 || nameIdx < 0)
            {
                continue;
            }

            for (var i = 2; i < rows.Count; i++)
            {
                var row = rows[i];
                var id = GetCell(row, idIdx);
                var name = GetCell(row, nameIdx);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                map[id] = name;
                map[$"role_{id}"] = name;
            }
        }

        return map;
    }

    public static List<RoleEntry> LoadRoleEntries(string projectRoot)
    {
        var dataDir = Path.Combine(projectRoot, "Data", "RoleData");
        var textDir = Path.Combine(projectRoot, "Text", "RoleData");
        var dataFiles = BuildCsvMap(dataDir);
        var textFiles = BuildCsvMap(textDir);
        var categories = dataFiles.Keys.Union(textFiles.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var result = new List<RoleEntry>();

        foreach (var category in categories)
        {
            dataFiles.TryGetValue(category, out var dataPath);
            textFiles.TryGetValue(category, out var textPath);
            var roleMap = new Dictionary<string, RoleEntry>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(dataPath) && File.Exists(dataPath))
            {
                var rows = CsvUtility.ReadAllRows(dataPath);
                if (rows.Count >= 3)
                {
                    var header = rows[0];
                    for (var i = 2; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var id = GetCellByColumn(row, header, "Id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        if (!roleMap.TryGetValue(id, out var role))
                        {
                            role = new RoleEntry
                            {
                                Category = category,
                                Id = id
                            };
                            roleMap[id] = role;
                        }

                        role.Avatar = GetCellByColumn(row, header, "Avatar");
                        role.CharacterImage = GetCellByColumn(row, header, "CharacterImage");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(textPath) && File.Exists(textPath))
            {
                var rows = CsvUtility.ReadAllRows(textPath);
                if (rows.Count >= 3)
                {
                    var header = rows[0];
                    for (var i = 2; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var id = GetCellByColumn(row, header, "Id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        if (!roleMap.TryGetValue(id, out var role))
                        {
                            role = new RoleEntry
                            {
                                Category = category,
                                Id = id
                            };
                            roleMap[id] = role;
                        }

                        role.Name = GetCellByColumn(row, header, "Name");
                        role.NameEn = GetCellByColumn(row, header, "Name_en");
                        role.NameZhHant = GetCellByColumn(row, header, "Name_zh-Hant");
                        role.NameJa = GetCellByColumn(row, header, "Name_ja");
                    }
                }
            }

            result.AddRange(roleMap.Values.OrderBy(x => x.Id));
        }

        return result;
    }

    public static void SaveRoleEntries(string projectRoot, IEnumerable<RoleEntry> roles)
    {
        var dataDir = Path.Combine(projectRoot, "Data", "RoleData");
        var textDir = Path.Combine(projectRoot, "Text", "RoleData");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(textDir);

        foreach (var file in Directory.GetFiles(dataDir, "*.csv"))
        {
            File.Delete(file);
        }
        foreach (var file in Directory.GetFiles(textDir, "*.csv"))
        {
            File.Delete(file);
        }

        var grouped = roles
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? InferCategoryFromId(r.Id) : r.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var category = NormalizeCategoryName(group.Key);
            var ordered = group.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();

            var dataRows = new List<string[]>
            {
                new[] { "Id", "Avatar", "CharacterImage" },
                new[] { "唯一标识", "头像路径", "立绘路径" }
            };
            foreach (var role in ordered)
            {
                dataRows.Add(new[] { role.Id, role.Avatar, role.CharacterImage });
            }

            var textRows = new List<string[]>
            {
                new[] { "Id", "Name", "Name_en", "Name_zh-Hant", "Name_ja" },
                new[] { "唯一标识", "", "", "", "" }
            };
            foreach (var role in ordered)
            {
                textRows.Add(new[] { role.Id, role.Name, role.NameEn, role.NameZhHant, role.NameJa });
            }

            CsvUtility.WriteAllRows(Path.Combine(dataDir, $"{category}.csv"), dataRows);
            CsvUtility.WriteAllRows(Path.Combine(textDir, $"{category}.csv"), textRows);
        }
    }

    private static string InferCategoryFromId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return "role";
        }

        var idx = roleId.IndexOf('_');
        return idx > 0 ? roleId[..idx] : "role";
    }

    private static string NormalizeCategoryName(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "role";
        }

        var trimmed = category.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(ch, '_');
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "role" : trimmed;
    }

    private static DialogueScene LoadScene(string sceneName, string? dataPath, string? textPath)
    {
        var scene = new DialogueScene { Name = sceneName };
        var lines = new Dictionary<string, DialogueLine>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        if (!string.IsNullOrEmpty(dataPath) && File.Exists(dataPath))
        {
            var rows = CsvUtility.ReadAllRows(dataPath);
            if (rows.Count >= 3)
            {
                var header = rows[0];
                for (var i = 2; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var originalId = GetCell(row, 0);
                    if (string.IsNullOrWhiteSpace(originalId))
                    {
                        continue;
                    }

                    var key = NormalizeRawId(sceneName, originalId);
                    var line = GetOrCreateLine(lines, key, order);
                    line.IdPart = key;

                    var rawBaseScript = GetCell(row, 1);
                    var (pureBaseScript, bg) = ExtractMetadataFromScript(rawBaseScript);
                    line.BaseScript = pureBaseScript;
                    line.BackgroundPath = bg;
                    line.EndScript = GetCellByColumn(row, header, "EndScript");
                    line.Roles = GetCellByColumn(row, header, "Roles");
                    line.IsNarrator = ToBool(GetCellByColumn(row, header, "IsNarrator"));
                    line.EventName = GetCellByColumn(row, header, "EventName");
                    line.ChoiceCount = ToInt(GetCellByColumn(row, header, "ChoiceCount"));
                    line.ChoiceScript1 = GetCellByColumn(row, header, "ChoiceScript1");
                    line.ChoiceScript2 = GetCellByColumn(row, header, "ChoiceScript2");
                    line.ChoiceScript3 = GetCellByColumn(row, header, "ChoiceScript3");
                    line.ChoiceScript4 = GetCellByColumn(row, header, "ChoiceScript4");
                }
            }
        }

        if (!string.IsNullOrEmpty(textPath) && File.Exists(textPath))
        {
            var rows = CsvUtility.ReadAllRows(textPath);
            if (rows.Count >= 3)
            {
                var header = rows[0];
                for (var i = 2; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var originalId = GetCell(row, 0);
                    if (string.IsNullOrWhiteSpace(originalId))
                    {
                        continue;
                    }

                    var key = NormalizeRawId(sceneName, originalId);
                    var line = GetOrCreateLine(lines, key, order);
                    line.IdPart = key;
                    line.Text = GetCell(row, 1);
                    line.TextEn = GetCell(row, 2);
                    line.TextJa = GetCell(row, 3);
                    line.ChoiceText1 = GetCellByColumn(row, header, "ChoiceText1");
                    line.ChoiceText1En = GetCellByColumn(row, header, "ChoiceText1_en");
                    line.ChoiceText1Ja = GetCellByColumn(row, header, "ChoiceText1_ja");
                    line.ChoiceText2 = GetCellByColumn(row, header, "ChoiceText2");
                    line.ChoiceText2En = GetCellByColumn(row, header, "ChoiceText2_en");
                    line.ChoiceText2Ja = GetCellByColumn(row, header, "ChoiceText2_ja");
                    line.ChoiceText3 = GetCellByColumn(row, header, "ChoiceText3");
                    line.ChoiceText3En = GetCellByColumn(row, header, "ChoiceText3_en");
                    line.ChoiceText3Ja = GetCellByColumn(row, header, "ChoiceText3_ja");
                    line.ChoiceText4 = GetCellByColumn(row, header, "ChoiceText4");
                    line.ChoiceText4En = GetCellByColumn(row, header, "ChoiceText4_en");
                    line.ChoiceText4Ja = GetCellByColumn(row, header, "ChoiceText4_ja");
                }
            }
        }

        foreach (var id in order)
        {
            scene.Lines.Add(lines[id]);
        }

        return scene;
    }

    private static DialogueLine GetOrCreateLine(
        IDictionary<string, DialogueLine> lines,
        string id,
        ICollection<string> order)
    {
        if (lines.TryGetValue(id, out var line))
        {
            return line;
        }

        line = new DialogueLine
        {
            IdPart = id,
            Roles = "role_narrator"
        };
        lines[id] = line;
        order.Add(id);
        return line;
    }

    private static Dictionary<string, string> BuildCsvMap(string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return map;
        }

        foreach (var file in Directory.GetFiles(folder, "*.csv"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[name] = file;
            }
        }

        return map;
    }

    private static string NormalizeRawId(string sceneName, string originalId)
    {
        var temp = originalId.Trim();
        if (temp.StartsWith('*'))
        {
            temp = temp[1..];
        }

        var prefix = sceneName + "_";
        if (temp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            temp = temp[prefix.Length..];
        }

        return temp;
    }

    private static (string pureScript, string bg) ExtractMetadataFromScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return (string.Empty, string.Empty);
        }

        var bg = string.Empty;
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var pure = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(BgMetaPrefix, StringComparison.Ordinal))
            {
                bg = trimmed[BgMetaPrefix.Length..].Trim();
                continue;
            }

            pure.Add(line);
        }

        return (string.Join(Environment.NewLine, pure).Trim(), bg);
    }

    private static string BuildScriptWithMetadata(string baseScript, string bg)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bg))
        {
            builder.AppendLine(BgMetaPrefix + bg.Trim());
        }

        if (!string.IsNullOrWhiteSpace(baseScript))
        {
            builder.Append(baseScript.Trim());
        }

        return builder.ToString();
    }

    private static int FindColumn(string[] header, string name)
    {
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetCell(string[] row, int index)
    {
        return index >= 0 && index < row.Length ? row[index] : string.Empty;
    }

    private static string GetCellByColumn(string[] row, string[] header, string column)
    {
        var idx = FindColumn(header, column);
        return GetCell(row, idx);
    }

    private static int ToInt(string value)
    {
        return int.TryParse(value, out var result) ? result : 0;
    }

    private static bool ToBool(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildDataHeader(int maxChoice)
    {
        var cols = new List<string> { "Id", "BaseScript", "EndScript", "Roles", "IsNarrator", "EventName", "ChoiceCount" };
        for (var i = 1; i <= maxChoice; i++)
        {
            cols.Add($"ChoiceScript{i}");
        }
        return cols.ToArray();
    }

    private static string[] BuildDataDesc(int maxChoice)
    {
        var cols = new List<string>
        {
            "对话Id", "初始脚本", "对话结束时执行", "出现的角色Id，说话者用<>包括，用,分割", "是否旁白(TRUE/FALSE)", "何时执行对话（事件名）", "选项数量"
        };
        for (var i = 1; i <= maxChoice; i++)
        {
            cols.Add("选项脚本");
        }
        return cols.ToArray();
    }

    private static string[] BuildTextHeader(int maxChoice)
    {
        var cols = new List<string> { "Id", "Text", "Text_en", "Text_ja" };
        for (var i = 1; i <= maxChoice; i++)
        {
            cols.Add($"ChoiceText{i}");
            cols.Add($"ChoiceText{i}_en");
            cols.Add($"ChoiceText{i}_ja");
        }
        cols.Add("Notification");
        cols.Add("Notification_en");
        cols.Add("Notification_ja");
        return cols.ToArray();
    }

    private static string[] BuildTextDesc(int maxChoice)
    {
        var cols = new List<string> { "对话Id", "对话正文", "", "" };
        for (var i = 1; i <= maxChoice; i++)
        {
            cols.Add($"选项{i}文本");
            cols.Add("");
            cols.Add("");
        }
        cols.Add("");
        cols.Add("");
        cols.Add("");
        return cols.ToArray();
    }

    private static string GetChoiceScript(DialogueLine line, int index)
    {
        return index switch
        {
            1 => line.ChoiceScript1,
            2 => line.ChoiceScript2,
            3 => line.ChoiceScript3,
            4 => line.ChoiceScript4,
            _ => string.Empty
        };
    }

    private static string GetChoiceText(DialogueLine line, int index, string lang)
    {
        return (index, lang) switch
        {
            (1, "zh") => line.ChoiceText1,
            (1, "en") => line.ChoiceText1En,
            (1, "ja") => line.ChoiceText1Ja,
            (2, "zh") => line.ChoiceText2,
            (2, "en") => line.ChoiceText2En,
            (2, "ja") => line.ChoiceText2Ja,
            (3, "zh") => line.ChoiceText3,
            (3, "en") => line.ChoiceText3En,
            (3, "ja") => line.ChoiceText3Ja,
            (4, "zh") => line.ChoiceText4,
            (4, "en") => line.ChoiceText4En,
            (4, "ja") => line.ChoiceText4Ja,
            _ => string.Empty
        };
    }

    private static string NormalizeRolesForExport(string rolesRaw, ISet<string>? validRoleIds)
    {
        if (string.IsNullOrWhiteSpace(rolesRaw))
        {
            return string.Empty;
        }

        if (validRoleIds == null)
        {
            return rolesRaw;
        }

        var result = new List<string>();
        var tokens = rolesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tokenRaw in tokens)
        {
            var token = tokenRaw.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var muted = token.StartsWith('*');
            if (muted)
            {
                token = token[1..].Trim();
            }

            if (string.IsNullOrWhiteSpace(token) || !IsRoleValid(token, validRoleIds))
            {
                continue;
            }

            result.Add((muted ? "*" : "") + token);
        }

        return string.Join(",", result);
    }

    private static bool IsRoleValid(string roleId, ISet<string> validRoleIds)
    {
        if (validRoleIds.Contains(roleId))
        {
            return true;
        }

        if (roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase))
        {
            return validRoleIds.Contains(roleId[5..]);
        }

        return validRoleIds.Contains("role_" + roleId);
    }
}
