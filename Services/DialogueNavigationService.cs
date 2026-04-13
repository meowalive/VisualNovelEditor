using System;
using System.Collections.Generic;
using VNEditor.Models;

namespace VNEditor.Services;

public static class DialogueNavigationService
{
    public static bool TryResolveDefaultNextIndex(IReadOnlyList<DialogueLine> lines, int currentIndex, out int nextIndex)
    {
        nextIndex = -1;
        if (currentIndex < 0 || currentIndex >= lines.Count)
        {
            return false;
        }

        if (!TryGetNextIdPart(lines[currentIndex].IdPart, out var nextIdPart))
        {
            return false;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].IdPart.Equals(nextIdPart, StringComparison.OrdinalIgnoreCase))
            {
                nextIndex = i;
                return true;
            }
        }

        return false;
    }

    public static bool TryGetNextIdPart(string? currentIdPart, out string nextIdPart)
    {
        nextIdPart = string.Empty;
        if (string.IsNullOrWhiteSpace(currentIdPart))
        {
            return false;
        }

        if (!int.TryParse(currentIdPart.Trim(), out var currentId))
        {
            return false;
        }

        nextIdPart = (currentId + 1).ToString();
        return true;
    }
}
