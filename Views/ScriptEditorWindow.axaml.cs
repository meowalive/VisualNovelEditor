using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using VNEditor.Models;
using VNEditor.Services;
using VNEditor.ViewModels;

namespace VNEditor.Views;

public partial class ScriptEditorWindow : Window
{
    private MainWindowViewModel? _themeSource;
    private readonly TextEditor _editor;
    private readonly TextBlock _syntaxStatusText;
    private readonly ItemsControl _snippetItems;
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private readonly TextMate.Installation _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private static readonly IReadOnlyList<LuaCompletionData> CompletionItems = new[]
    {
        new LuaCompletionData(
            "ShowDialogue",
            "ShowDialogue(id)",
            "参数: id (string) — 目标对话/场景 ID",
            "作用: 跳转到指定对话并继续播放"
        ),
        new LuaCompletionData(
            "EndDialogue",
            "EndDialogue()",
            "参数: 无",
            "作用: 结束当前对话并关闭对话"
        ),
        new LuaCompletionData(
            "跳转",
            "跳转(id)",
            "参数: id (string) — 目标对话/场景 ID",
            "作用: 跳转到指定对话并继续播放（同 ShowDialogue）"
        ),
        new LuaCompletionData(
            "结束",
            "结束()",
            "参数: 无",
            "作用: 结束当前对话并关闭对话（同 EndDialogue）"
        ),
    };

    public string ScriptText { get; private set; } = string.Empty;

    /// <summary>设置后应用主题配色、背景图/色调、窗口透明度与模糊。</summary>
    public MainWindowViewModel? ThemeSource
    {
        get => _themeSource;
        set
        {
            if (_themeSource == value) return;
            if (_themeSource != null)
                _themeSource.PropertyChanged -= OnThemeSourcePropertyChanged;
            _themeSource = value;
            if (_themeSource != null)
            {
                _themeSource.PropertyChanged += OnThemeSourcePropertyChanged;
                DataContext = _themeSource;
                Opacity = _themeSource.WindowOpacity;
                ApplyTransparencyLevel();
            }
            else
            {
                DataContext = null;
                Opacity = 1.0;
                TransparencyLevelHint = new[] { Avalonia.Controls.WindowTransparencyLevel.None };
                Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            }
        }
    }

    private void OnThemeSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_themeSource == null) return;
        if (e.PropertyName is nameof(MainWindowViewModel.WindowBlurLevel))
            ApplyTransparencyLevel();
        else if (e.PropertyName is nameof(MainWindowViewModel.WindowOpacity))
            Opacity = _themeSource.WindowOpacity;
    }

    private void ApplyTransparencyLevel()
    {
        try
        {
            if (_themeSource == null) return;
            var level = _themeSource.GetWindowTransparencyLevel();
            TransparencyLevelHint = new[] { level };
            ClearValue(BackgroundProperty); // 清除 XAML 绑定，避免覆盖亚克力/模糊
            if (level != Avalonia.Controls.WindowTransparencyLevel.None)
                Background = new SolidColorBrush(Color.FromArgb(217, 0x1E, 0x1E, 0x1E));
            else
                this.Bind(Window.BackgroundProperty, new Avalonia.Data.Binding(nameof(MainWindowViewModel.ThemeWindowBackground)) { Source = _themeSource });
        }
        catch { /* 透明/模糊不可用时跳过 */ }
    }

    public ScriptEditorWindow() : this("脚本编辑器", string.Empty)
    {
    }

    public ScriptEditorWindow(string title, string initialScript)
    {
        InitializeComponent();
        Title = title;

        _editor = this.FindControl<TextEditor>("ScriptEditor")!;
        _syntaxStatusText = this.FindControl<TextBlock>("SyntaxStatusText")!;
        _snippetItems = this.FindControl<ItemsControl>("SnippetItems")!;

        _editor.Text = initialScript ?? string.Empty;
        _textMateInstallation = _editor.InstallTextMate(_registryOptions);
        var luaLanguage = _registryOptions.GetLanguageByExtension(".lua");
        if (luaLanguage != null)
        {
            _textMateInstallation.SetGrammar(_registryOptions.GetScopeByLanguageId(luaLanguage.Id));
        }
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.KeyDown += OnTextAreaKeyDown;
        _editor.TextChanged += (_, _) => Validate();
        Closed += (_, _) => _textMateInstallation.Dispose();

        _snippetItems.ItemsSource = BuildSnippets();
        Opened += (_, _) => { try { ApplyTransparencyLevel(); } catch { } }; // 显示时再次应用，确保亚克力生效
        Validate();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        Validate();
        if (string.IsNullOrEmpty(e.Text) || _completionWindow != null)
            return;
        if (!IsIdentifierStart(e.Text![0]))
            return;
        TryShowCompletion();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow == null)
            return;
        if (e.Text == "\u001b" || e.Text == "\b") // Escape or Backspace
            _completionWindow.Close();
    }

    private void OnTextAreaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        e.Handled = true;
        if (_completionWindow != null)
            return;
        TryShowCompletion();
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c) || c == '_' || (c >= 0x4E00 && c < 0xA000);
    }

    private static bool IsIdentifierPart(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || (c >= 0x4E00 && c < 0xA000);
    }

    private void TryShowCompletion()
    {
        var doc = _editor.Document;
        var offset = _editor.CaretOffset;
        if (offset < 0)
            return;
        var start = offset > 0 ? offset - 1 : 0;
        while (start >= 0 && IsIdentifierPart(doc.GetCharAt(start)))
            start--;
        start++;
        var prefix = doc.GetText(start, offset - start);
        var filtered = string.IsNullOrEmpty(prefix)
            ? CompletionItems
            : CompletionItems.Where(x => x.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count == 0)
            return;
        _completionWindow = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = start,
            EndOffset = offset
        };
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var item in filtered)
            data.Add(item);
        _completionWindow.Show();
    }

    private void OnSnippetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ScriptSnippetItem snippet })
        {
            return;
        }

        var current = _editor.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current) && !current.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            current += Environment.NewLine;
        }

        _editor.Text = current + snippet.Code + Environment.NewLine;
        _editor.CaretOffset = _editor.Text.Length;
        _editor.Focus();
    }

    private async void OnSaveAndCloseClicked(object? sender, RoutedEventArgs e)
    {
        var errors = ScriptSyntaxValidator.Validate(_editor.Text);
        if (errors.Count > 0)
        {
            var msg = string.Join(Environment.NewLine, errors.Take(8));
            await ShowWarning("脚本语法不通过，无法保存：", msg);
            return;
        }

        ScriptText = _editor.Text ?? string.Empty;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Validate()
    {
        var errors = ScriptSyntaxValidator.Validate(_editor.Text);
        if (errors.Count == 0)
        {
            _syntaxStatusText.Foreground = Brushes.LightGreen;
            _syntaxStatusText.Text = "语法检查通过。";
            return;
        }

        _syntaxStatusText.Foreground = Brushes.IndianRed;
        _syntaxStatusText.Text = string.Join(Environment.NewLine, errors.Take(6));
    }

    private async System.Threading.Tasks.Task ShowWarning(string title, string detail)
    {
        var ok = new Button
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Content = "确定"
        };
        var win = new Window
        {
            Width = 620,
            Height = 360,
            MinWidth = 520,
            MinHeight = 240,
            Title = "语法错误",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold },
                    new TextBox
                    {
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MinHeight = 220,
                        Text = detail
                    },
                    ok
                }
            }
        };

        ok.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
    }

    private static IReadOnlyList<ScriptSnippetItem> BuildSnippets()
    {
        return new List<ScriptSnippetItem>
        {
            new() { Title = "对话跳转", Code = "跳转(\"scene_1_1\");" },
            new() { Title = "结束对话", Code = "结束();" },
        };
    }
}
