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

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public string? DialogueText
    {
        get => GetValue(DialogueTextProperty);
        set => SetValue(DialogueTextProperty, value);
    }

    static RichDialogueTextBlock()
    {
        DialogueTextProperty.Changed.AddClassHandler<RichDialogueTextBlock>((x, _) => x.RebuildInlines());
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
                AppendText(text[last..match.Index], state);
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
            AppendText(text[last..], state);
        }
    }

    private void AppendText(string content, StyleState state)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var parts = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                var run = new Run(parts[i])
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

                var inlines = Inlines;
                if (inlines != null) inlines.Add(run);
            }

            if (i < parts.Length - 1)
            {
                var inlines = Inlines;
                if (inlines != null) inlines.Add(new LineBreak());
            }
        }
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
