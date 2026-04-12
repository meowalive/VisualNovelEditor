using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VNEditor.Controls;

public class RichDialogueTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> DialogueTextProperty =
        AvaloniaProperty.Register<RichDialogueTextBlock, string?>(nameof(DialogueText), string.Empty);
    public static readonly StyledProperty<int> VisibleCharacterCountProperty =
        AvaloniaProperty.Register<RichDialogueTextBlock, int>(nameof(VisibleCharacterCount), -1);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public string? DialogueText
    {
        get => GetValue(DialogueTextProperty);
        set => SetValue(DialogueTextProperty, value);
    }

    public int VisibleCharacterCount
    {
        get => GetValue(VisibleCharacterCountProperty);
        set => SetValue(VisibleCharacterCountProperty, value);
    }

    static RichDialogueTextBlock()
    {
        DialogueTextProperty.Changed.AddClassHandler<RichDialogueTextBlock>((x, _) => x.RebuildInlines());
        VisibleCharacterCountProperty.Changed.AddClassHandler<RichDialogueTextBlock>((x, _) => x.RebuildInlines());
    }

    private sealed class StyleState
    {
        public IBrush? Foreground { get; init; }
        public FontWeight Weight { get; init; }
        public FontStyle Style { get; init; }
        public double? Size { get; init; }
    }

    private void RebuildInlines()
    {
        var inlines = Inlines;
        if (inlines == null)
        {
            return;
        }

        inlines.Clear();
        var text = DialogueText ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        var remainingVisibleCharacters = VisibleCharacterCount < 0
            ? int.MaxValue
            : VisibleCharacterCount;

        var stateStack = new Stack<StyleState>();
        var state = new StyleState
        {
            Foreground = null,
            Weight = FontWeight.Normal,
            Style = FontStyle.Normal,
            Size = null
        };

        var last = 0;
        foreach (Match match in TagRegex.Matches(text))
        {
            if (match.Index > last)
            {
                remainingVisibleCharacters -= AppendText(text[last..match.Index], state, remainingVisibleCharacters);
                if (remainingVisibleCharacters <= 0)
                {
                    return;
                }
            }

            var rawTag = match.Value.Trim();
            var lowerTag = rawTag.ToLowerInvariant();

            if (lowerTag.StartsWith("<color="))
            {
                stateStack.Push(state);
                state = new StyleState
                {
                    Weight = state.Weight,
                    Style = state.Style,
                    Size = state.Size,
                    Foreground = ParseColorBrush(rawTag[7..^1])
                };
            }
            else if (lowerTag == "</color>")
            {
                if (stateStack.Count > 0) state = stateStack.Pop();
            }
            else if (lowerTag == "<b>")
            {
                stateStack.Push(state);
                state = new StyleState
                {
                    Foreground = state.Foreground,
                    Style = state.Style,
                    Size = state.Size,
                    Weight = FontWeight.Bold
                };
            }
            else if (lowerTag == "</b>")
            {
                if (stateStack.Count > 0) state = stateStack.Pop();
            }
            else if (lowerTag == "<i>")
            {
                stateStack.Push(state);
                state = new StyleState
                {
                    Foreground = state.Foreground,
                    Weight = state.Weight,
                    Size = state.Size,
                    Style = FontStyle.Italic
                };
            }
            else if (lowerTag == "</i>")
            {
                if (stateStack.Count > 0) state = stateStack.Pop();
            }
            else if (lowerTag.StartsWith("<size="))
            {
                stateStack.Push(state);
                state = new StyleState
                {
                    Foreground = state.Foreground,
                    Weight = state.Weight,
                    Style = state.Style,
                    Size = ParseSize(rawTag[6..^1])
                };
            }
            else if (lowerTag == "</size>")
            {
                if (stateStack.Count > 0) state = stateStack.Pop();
            }

            last = match.Index + match.Length;
        }

        if (last < text.Length)
        {
            _ = AppendText(text[last..], state, remainingVisibleCharacters);
        }
    }

    private int AppendText(string content, StyleState state, int remainingVisibleCharacters)
    {
        if (string.IsNullOrEmpty(content) || remainingVisibleCharacters <= 0)
        {
            return 0;
        }

        var normalized = content.Replace("\r\n", "\n");
        var consumed = 0;
        var start = 0;
        for (var i = 0; i < normalized.Length && remainingVisibleCharacters > 0; i++)
        {
            if (normalized[i] == '\n')
            {
                AppendRun(normalized[start..i], state);
                Inlines?.Add(new LineBreak());
                start = i + 1;
                consumed++;
                remainingVisibleCharacters--;
                continue;
            }

            consumed++;
            remainingVisibleCharacters--;
            if (remainingVisibleCharacters == 0)
            {
                AppendRun(normalized[start..(i + 1)], state);
                return consumed;
            }
        }

        if (start < normalized.Length && remainingVisibleCharacters > 0)
        {
            AppendRun(normalized[start..], state);
        }

        return consumed;
    }

    private void AppendRun(string content, StyleState state)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var run = new Run(content)
        {
            FontWeight = state.Weight,
            FontStyle = state.Style
        };
        if (state.Foreground != null)
        {
            run.Foreground = state.Foreground;
        }

        if (state.Size.HasValue && state.Size.Value > 0)
        {
            run.FontSize = state.Size.Value;
        }

        Inlines?.Add(run);
    }

    private static IBrush? ParseColorBrush(string raw)
    {
        var value = raw.Trim().Trim('"', '\'');
        if (value.Length == 0)
        {
            return null;
        }

        if (Color.TryParse(value, out var color))
        {
            return new SolidColorBrush(color);
        }

        return null;
    }

    private static double? ParseSize(string raw)
    {
        var value = raw.Trim().Trim('"', '\'');
        if (double.TryParse(value, out var size) && size > 0)
        {
            return size;
        }

        return null;
    }
}
