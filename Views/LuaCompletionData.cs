using System;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace VNEditor.Views;

public sealed class LuaCompletionData : ICompletionData
{
    public LuaCompletionData(string insertText, string signature, string parameters, string purpose)
    {
        Text = insertText;
        Signature = signature;
        Parameters = parameters;
        Purpose = purpose;
    }

    public Avalonia.Media.IImage? Image => null;
    public string Text { get; }
    public string Signature { get; }
    public string Parameters { get; }
    public string Purpose { get; }

    /// <summary>列表项显示：签名 — 作用</summary>
    public object Content => $"{Signature} — {Purpose}";

    /// <summary>悬停/详情：参数 + 作用</summary>
    public object? Description => $"{Parameters}\n\n{Purpose}";

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs _)
    {
        textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, Text);
    }
}
