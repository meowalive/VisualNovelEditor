using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNEditor.Models;
using VNEditor.Services;

namespace VNEditor.ViewModels;

public partial class ScenePreviewViewModel : ViewModelBase
{
    private readonly DialogueScene _scene;
    private readonly string _resourcesRoot;
    private readonly string _gameResourcesRoot;
    private readonly string _projectRoot;
    private readonly Dictionary<string, string> _roleCharacterImageMap;
    private readonly Dictionary<string, string> _roleNameMap;
    private readonly Dictionary<string, double> _roleDefaultYMap;
    private readonly Dictionary<string, double> _roleDefaultScaleMap;
    private readonly PortraitVisualState[] _portraitStates = [new(), new()];
    private readonly object _portraitStateSync = new();
    private int _playingIndex;
    private string _activeBackgroundPath = string.Empty;
    private CancellationTokenSource? _visualScriptCts;
    private CancellationTokenSource? _previewTypewriterCts;

    [ObservableProperty] private string windowTitle = "场景预览";
    [ObservableProperty] private Bitmap? previewBackground;
    [ObservableProperty] private Bitmap? previewPortrait1;
    [ObservableProperty] private Bitmap? previewPortrait2;
    [ObservableProperty] private bool previewPortrait1Visible;
    [ObservableProperty] private bool previewPortrait2Visible;
    [ObservableProperty] private bool previewPortrait1Dim;
    [ObservableProperty] private bool previewPortrait2Dim;
    [ObservableProperty] private bool previewUseSinglePortrait;
    [ObservableProperty] private bool previewUseDualPortrait;
    [ObservableProperty] private Bitmap? previewSinglePortrait;
    [ObservableProperty] private bool previewSinglePortraitDim;
    [ObservableProperty] private double previewPortrait1OffsetX;
    [ObservableProperty] private double previewPortrait1OffsetY;
    [ObservableProperty] private double previewPortrait1Scale = 1.0;
    [ObservableProperty] private double previewPortrait2OffsetX;
    [ObservableProperty] private double previewPortrait2OffsetY;
    [ObservableProperty] private double previewPortrait2Scale = 1.0;
    [ObservableProperty] private double previewSinglePortraitOffsetX;
    [ObservableProperty] private double previewSinglePortraitOffsetY;
    [ObservableProperty] private double previewSinglePortraitScale = 1.0;
    [ObservableProperty] private string previewSpeaker = "旁白";
    [ObservableProperty] private string previewText = string.Empty;
    [ObservableProperty] private int previewVisibleCharacterCount = -1;
    [ObservableProperty] private bool isPreviewTyping;
    [ObservableProperty] private string previewHint = "鼠标左键下一句";
    [ObservableProperty] private bool previewChoice1Visible;
    [ObservableProperty] private bool previewChoice2Visible;
    [ObservableProperty] private bool previewChoice3Visible;
    [ObservableProperty] private bool previewChoice4Visible;
    [ObservableProperty] private string previewChoice1Text = "选项1";
    [ObservableProperty] private string previewChoice2Text = "选项2";
    [ObservableProperty] private string previewChoice3Text = "选项3";
    [ObservableProperty] private string previewChoice4Text = "选项4";
    [ObservableProperty] private bool previewDialogueBoxVisible = true;
    [ObservableProperty] private bool isFinished;

    public ScenePreviewViewModel(
        DialogueScene scene,
        string resourcesRoot,
        string gameResourcesRoot,
        string projectRoot,
        Dictionary<string, string> roleCharacterImageMap,
        Dictionary<string, string> roleNameMap,
        Dictionary<string, double> roleDefaultYMap,
        Dictionary<string, double> roleDefaultScaleMap)
    {
        _scene = scene;
        _resourcesRoot = resourcesRoot;
        _gameResourcesRoot = gameResourcesRoot;
        _projectRoot = projectRoot;
        _roleCharacterImageMap = new Dictionary<string, string>(roleCharacterImageMap, StringComparer.OrdinalIgnoreCase);
        _roleNameMap = new Dictionary<string, string>(roleNameMap, StringComparer.OrdinalIgnoreCase);
        _roleDefaultYMap = new Dictionary<string, double>(roleDefaultYMap, StringComparer.OrdinalIgnoreCase);
        _roleDefaultScaleMap = new Dictionary<string, double>(roleDefaultScaleMap, StringComparer.OrdinalIgnoreCase);
        _playingIndex = 0;
        WindowTitle = $"场景预览 - {_scene.Name}";
        ApplyCurrentLine();
    }

    [RelayCommand]
    private void LeftClick()
    {
        if (PreviewDialogueBoxVisible && TryCompletePreviewTypewriter())
        {
            return;
        }

        if (IsFinished)
        {
            return;
        }

        if (PreviewDialogueBoxVisible
            && (PreviewChoice1Visible || PreviewChoice2Visible || PreviewChoice3Visible || PreviewChoice4Visible))
        {
            return;
        }

        var line = GetCurrentLine();
        if (line == null)
        {
            EndPreview("场景播放完成。");
            return;
        }

        if (string.IsNullOrWhiteSpace(line.EndScript))
        {
            MoveToDefaultNextOrFinish();
            return;
        }

        _ = VisualNovelScriptExecutorParser.TryParseFirstAction(line.EndScript, out var endAction, out var endError);
        if (TryResolveJumpFromAction(endAction, out var target))
        {
            MoveTo(target);
            return;
        }

        if (endAction.Type == DialogueScriptActionType.EndDialogue)
        {
            EndPreview("预览结束（EndDialogue）。");
            return;
        }

        PreviewHint = string.IsNullOrWhiteSpace(endError) ? "该 EndScript 无法模拟（无详细错误）" : $"EndScript 模拟失败: {endError}";
    }

    [RelayCommand] private void SelectChoice1() => ApplyChoice(1);
    [RelayCommand] private void SelectChoice2() => ApplyChoice(2);
    [RelayCommand] private void SelectChoice3() => ApplyChoice(3);
    [RelayCommand] private void SelectChoice4() => ApplyChoice(4);

    private void ApplyChoice(int idx)
    {
        if (IsFinished)
        {
            return;
        }

        var line = GetCurrentLine();
        if (line == null)
        {
            return;
        }

        HideChoices();
        var script = GetChoiceScriptByIndex(line, idx);
        if (string.IsNullOrWhiteSpace(script))
        {
            MoveToDefaultNextOrFinish();
            return;
        }

        _ = VisualNovelScriptExecutorParser.TryParseFirstAction(script, out var choiceAction, out var choiceError);
        if (choiceAction.Type == DialogueScriptActionType.EndDialogue)
        {
            ExecuteEndScriptAfterChoice(line);
            return;
        }

        if (TryResolveJumpFromAction(choiceAction, out var target))
        {
            MoveTo(target);
            return;
        }

        PreviewHint = string.IsNullOrWhiteSpace(choiceError) ? "该 ChoiceScript 无法模拟（无详细错误）" : $"ChoiceScript 模拟失败: {choiceError}";
    }

    private void ExecuteEndScriptAfterChoice(DialogueLine line)
    {
        if (string.IsNullOrWhiteSpace(line.EndScript))
        {
            MoveToDefaultNextOrFinish();
            return;
        }

        _ = VisualNovelScriptExecutorParser.TryParseFirstAction(line.EndScript, out var endAction, out var endError);
        if (TryResolveJumpFromAction(endAction, out var target))
        {
            MoveTo(target);
            return;
        }

        if (endAction.Type == DialogueScriptActionType.EndDialogue)
        {
            EndPreview("预览结束（EndScript 触发 EndDialogue）。");
            return;
        }

        EndPreview(string.IsNullOrWhiteSpace(endError) ? "EndScript 无法模拟。" : $"EndScript 模拟失败: {endError}");
    }

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _scene.Lines.Count)
        {
            EndPreview("场景播放完成。");
            return;
        }

        _playingIndex = index;
        ApplyCurrentLine();
    }

    private DialogueLine? GetCurrentLine()
    {
        if (_playingIndex < 0 || _playingIndex >= _scene.Lines.Count)
        {
            return null;
        }

        return _scene.Lines[_playingIndex];
    }

    private void ApplyCurrentLine()
    {
        var line = GetCurrentLine();
        if (line == null)
        {
            EndPreview("场景播放完成。");
            return;
        }

        var hideFlag = VisualNovelScriptExecutorParser.ParseHideDialogue(line.BaseScript);
        if (hideFlag.HasValue)
        {
            PreviewDialogueBoxVisible = !hideFlag.Value;
        }

        PreviewText = line.Text;
        StartPreviewTypewriter(line.Text, animate: PreviewDialogueBoxVisible);
        SetPreviewBackground(line.BackgroundPath, keepWhenEmpty: true);
        SetPortraits(line);
        ApplyVisualCommands(line.BaseScript);
        SetupChoices(line);
        if (!PreviewChoice1Visible && !PreviewChoice2Visible && !PreviewChoice3Visible && !PreviewChoice4Visible)
        {
            PreviewHint = "鼠标左键下一句";
        }
    }

    private void SetupChoices(DialogueLine line)
    {
        var count = Math.Clamp(line.ChoiceCount, 0, 4);
        PreviewChoice1Visible = count >= 1;
        PreviewChoice2Visible = count >= 2;
        PreviewChoice3Visible = count >= 3;
        PreviewChoice4Visible = count >= 4;
        PreviewChoice1Text = string.IsNullOrWhiteSpace(line.ChoiceText1) ? "选项1" : line.ChoiceText1;
        PreviewChoice2Text = string.IsNullOrWhiteSpace(line.ChoiceText2) ? "选项2" : line.ChoiceText2;
        PreviewChoice3Text = string.IsNullOrWhiteSpace(line.ChoiceText3) ? "选项3" : line.ChoiceText3;
        PreviewChoice4Text = string.IsNullOrWhiteSpace(line.ChoiceText4) ? "选项4" : line.ChoiceText4;
        if (count > 0)
        {
            PreviewHint = "请选择一个选项";
        }
    }

    private void HideChoices()
    {
        PreviewChoice1Visible = false;
        PreviewChoice2Visible = false;
        PreviewChoice3Visible = false;
        PreviewChoice4Visible = false;
    }

    private void MoveToDefaultNextOrFinish()
    {
        if (DialogueNavigationService.TryResolveDefaultNextIndex(_scene.Lines, _playingIndex, out var nextIndex))
        {
            MoveTo(nextIndex);
            return;
        }

        EndPreview("鍦烘櫙鎾斁瀹屾垚銆?");
    }

    private void SetPreviewBackground(string rawPath, bool keepWhenEmpty)
    {
        var resolved = ResolveResourcePath(rawPath);
        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            if (!keepWhenEmpty)
            {
                _activeBackgroundPath = string.Empty;
                var old = PreviewBackground;
                PreviewBackground = null;
                old?.Dispose();
            }
            return;
        }

        if (_activeBackgroundPath.Equals(resolved, StringComparison.OrdinalIgnoreCase) && PreviewBackground != null)
        {
            return;
        }

        _activeBackgroundPath = resolved;
        var bmp = LoadBitmapSafe(resolved);
        if (bmp == null)
        {
            return;
        }

        var oldBg = PreviewBackground;
        PreviewBackground = bmp;
        oldBg?.Dispose();
    }

    private void SetPortraits(DialogueLine line)
    {
        var roles = ParseRoles(line.Roles);
        if (roles.Count == 0)
        {
            ClearPortraits();
            PreviewSpeaker = line.IsNarrator ? string.Empty : "旁白";
            return;
        }

        var speakerId = roles.FirstOrDefault(x => x.isSpeaker).id;
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            speakerId = roles[0].id;
        }
        PreviewSpeaker = line.IsNarrator ? string.Empty : ResolveRoleName(speakerId);

        SetPortraitSlot(1, roles.ElementAtOrDefault(0), line.RoleImage1);
        SetPortraitSlot(2, roles.ElementAtOrDefault(1), line.RoleImage2);
    }

    private void SetPortraitSlot(int slot, (string id, bool isSpeaker) role, string? overrideImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(role.id))
        {
            ResetPortraitSlot(slot, null);
            SetPortrait(slot, null, false, false);
            return;
        }

        var path = ResolvePortraitPathByRoleId(role.id, overrideImagePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ResetPortraitSlot(slot, null);
            SetPortrait(slot, null, false, false);
            return;
        }

        var bmp = LoadBitmapSafe(path);
        if (bmp == null)
        {
            ResetPortraitSlot(slot, null);
            SetPortrait(slot, null, false, false);
            return;
        }

        ResetPortraitSlot(slot, role.id);
        SetPortrait(slot, bmp, true, !role.isSpeaker);
    }

    private void SetPortrait(int slot, Bitmap? bmp, bool visible, bool dim)
    {
        if (slot == 1)
        {
            var old = PreviewPortrait1;
            PreviewPortrait1 = bmp;
            PreviewPortrait1Visible = visible;
            PreviewPortrait1Dim = dim;
            old?.Dispose();
            RefreshPortraitLayout();
            return;
        }

        var old2 = PreviewPortrait2;
        PreviewPortrait2 = bmp;
        PreviewPortrait2Visible = visible;
        PreviewPortrait2Dim = dim;
        old2?.Dispose();
        RefreshPortraitLayout();
    }

    private void RefreshPortraitLayout()
    {
        var count = (PreviewPortrait1Visible ? 1 : 0) + (PreviewPortrait2Visible ? 1 : 0);
        PreviewUseSinglePortrait = count == 1;
        PreviewUseDualPortrait = count >= 2;

        if (!PreviewUseSinglePortrait)
        {
            PreviewSinglePortrait = null;
            PreviewSinglePortraitDim = false;
            PreviewSinglePortraitOffsetX = 0;
            PreviewSinglePortraitOffsetY = 0;
            PreviewSinglePortraitScale = 1.0;
            return;
        }

        if (PreviewPortrait1Visible)
        {
            PreviewSinglePortrait = PreviewPortrait1;
            PreviewSinglePortraitDim = PreviewPortrait1Dim;
            SyncSinglePortraitTransform(1);
        }
        else
        {
            PreviewSinglePortrait = PreviewPortrait2;
            PreviewSinglePortraitDim = PreviewPortrait2Dim;
            SyncSinglePortraitTransform(2);
        }
    }

    private void ClearPortraits()
    {
        CancelPendingVisualCommands();
        var old1 = PreviewPortrait1;
        var old2 = PreviewPortrait2;
        PreviewPortrait1 = null;
        PreviewPortrait2 = null;
        PreviewPortrait1Visible = false;
        PreviewPortrait2Visible = false;
        PreviewPortrait1Dim = false;
        PreviewPortrait2Dim = false;
        PreviewUseSinglePortrait = false;
        PreviewUseDualPortrait = false;
        PreviewSinglePortrait = null;
        PreviewSinglePortraitDim = false;
        PreviewPortrait1OffsetX = 0;
        PreviewPortrait1OffsetY = 0;
        PreviewPortrait1Scale = 1.0;
        PreviewPortrait2OffsetX = 0;
        PreviewPortrait2OffsetY = 0;
        PreviewPortrait2Scale = 1.0;
        PreviewSinglePortraitOffsetX = 0;
        PreviewSinglePortraitOffsetY = 0;
        PreviewSinglePortraitScale = 1.0;
        lock (_portraitStateSync)
        {
            _portraitStates[0].Clear();
            _portraitStates[1].Clear();
        }
        old1?.Dispose();
        old2?.Dispose();
    }

    private string ResolveRoleName(string roleId)
    {
        if (IsNarratorRole(roleId))
        {
            return string.Empty;
        }

        if (_roleNameMap.TryGetValue(roleId, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var key = roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase) ? roleId[5..] : roleId;
        if (_roleNameMap.TryGetValue(key, out var n2) && !string.IsNullOrWhiteSpace(n2))
        {
            return n2;
        }

        return key;
    }

    private static bool IsNarratorRole(string? roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return true;
        }

        var key = roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase) ? roleId[5..] : roleId;
        return key.Equals("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolvePortraitPathByRoleId(string roleId, string? overrideImagePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideImagePath))
        {
            var resolvedOverride = ResolveResourcePath(overrideImagePath);
            if (!string.IsNullOrWhiteSpace(resolvedOverride))
            {
                return resolvedOverride;
            }
        }

        if (_roleCharacterImageMap.TryGetValue(roleId, out var direct))
        {
            return ResolveResourcePath(direct);
        }

        var key = roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase) ? roleId[5..] : roleId;
        return _roleCharacterImageMap.TryGetValue(key, out var path)
            ? ResolveResourcePath(path)
            : string.Empty;
    }

    private void ResetPortraitSlot(int slot, string? roleId)
    {
        var normalizedRoleId = roleId?.Trim() ?? string.Empty;
        var defaultY = string.IsNullOrWhiteSpace(normalizedRoleId) ? 0 : ResolveRoleDefaultY(normalizedRoleId);
        var defaultScale = string.IsNullOrWhiteSpace(normalizedRoleId) ? 1.0 : ResolveRoleDefaultScale(normalizedRoleId);
        lock (_portraitStateSync)
        {
            if (string.IsNullOrWhiteSpace(normalizedRoleId))
            {
                _portraitStates[slot - 1].Clear();
            }
            else
            {
                _portraitStates[slot - 1].Reset(normalizedRoleId, defaultY, defaultScale);
            }
        }

        ApplySlotTransform(slot, 0, defaultY, defaultScale);
    }

    private void ApplyVisualCommands(string? script)
    {
        CancelPendingVisualCommands();
        var commands = VisualNovelScriptExecutorParser.ParsePortraitVisualCommands(script);
        if (commands.Count == 0)
        {
            return;
        }

        _visualScriptCts = new CancellationTokenSource();
        foreach (var command in commands)
        {
            _ = RunVisualCommandAsync(command, _visualScriptCts.Token);
        }
    }

    private void CancelPendingVisualCommands()
    {
        _visualScriptCts?.Cancel();
        _visualScriptCts?.Dispose();
        _visualScriptCts = null;
    }

    private async Task RunVisualCommandAsync(PortraitVisualCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (command.Delay > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(command.Delay), cancellationToken);
            }

            var slot = Math.Clamp(command.Index, 1, 2);
            if (!HasPortraitRole(slot))
            {
                return;
            }

            var startValue = GetVisualValue(slot, command.Type);
            var targetValue = GetVisualTarget(slot, command);
            if (command.Time <= 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetVisualValue(slot, command.Type, targetValue), DispatcherPriority.Render);
                return;
            }

            var duration = TimeSpan.FromSeconds(command.Time);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                var value = Lerp(startValue, targetValue, progress);
                await Dispatcher.UIThread.InvokeAsync(() => SetVisualValue(slot, command.Type, value), DispatcherPriority.Render);
                await Task.Delay(16, cancellationToken);
            }

            await Dispatcher.UIThread.InvokeAsync(() => SetVisualValue(slot, command.Type, targetValue), DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasPortraitRole(int slot)
    {
        lock (_portraitStateSync)
        {
            return !string.IsNullOrWhiteSpace(_portraitStates[slot - 1].RoleId);
        }
    }

    private double GetVisualValue(int slot, PortraitVisualCommandType type)
    {
        lock (_portraitStateSync)
        {
            var state = _portraitStates[slot - 1];
            return type switch
            {
                PortraitVisualCommandType.MoveX => state.X,
                PortraitVisualCommandType.MoveY => state.Y,
                PortraitVisualCommandType.Scale => state.Scale,
                _ => 0
            };
        }
    }

    private double GetVisualTarget(int slot, PortraitVisualCommand command)
    {
        lock (_portraitStateSync)
        {
            var state = _portraitStates[slot - 1];
            return command.Type switch
            {
                PortraitVisualCommandType.MoveX => command.Value,
                PortraitVisualCommandType.MoveY => state.DefaultY + command.Value,
                PortraitVisualCommandType.Scale => command.Value,
                _ => command.Value
            };
        }
    }

    private void SetVisualValue(int slot, PortraitVisualCommandType type, double value)
    {
        double x;
        double y;
        double scale;
        lock (_portraitStateSync)
        {
            var state = _portraitStates[slot - 1];
            switch (type)
            {
                case PortraitVisualCommandType.MoveX:
                    state.X = value;
                    break;
                case PortraitVisualCommandType.MoveY:
                    state.Y = value;
                    break;
                case PortraitVisualCommandType.Scale:
                    state.Scale = value;
                    break;
            }

            x = state.X;
            y = state.Y;
            scale = state.Scale;
        }

        ApplySlotTransform(slot, x, y, scale);
    }

    private void ApplySlotTransform(int slot, double x, double y, double scale)
    {
        if (slot == 1)
        {
            PreviewPortrait1OffsetX = x;
            PreviewPortrait1OffsetY = y;
            PreviewPortrait1Scale = scale;
        }
        else
        {
            PreviewPortrait2OffsetX = x;
            PreviewPortrait2OffsetY = y;
            PreviewPortrait2Scale = scale;
        }

        SyncSinglePortraitTransform(PreviewPortrait1Visible ? 1 : 2);
    }

    private void SyncSinglePortraitTransform(int slot)
    {
        if (!PreviewUseSinglePortrait)
        {
            return;
        }

        if (slot == 1 && PreviewPortrait1Visible)
        {
            PreviewSinglePortraitOffsetX = PreviewPortrait1OffsetX;
            PreviewSinglePortraitOffsetY = PreviewPortrait1OffsetY;
            PreviewSinglePortraitScale = PreviewPortrait1Scale;
        }
        else if (slot == 2 && PreviewPortrait2Visible)
        {
            PreviewSinglePortraitOffsetX = PreviewPortrait2OffsetX;
            PreviewSinglePortraitOffsetY = PreviewPortrait2OffsetY;
            PreviewSinglePortraitScale = PreviewPortrait2Scale;
        }
    }

    private double ResolveRoleDefaultY(string roleId)
    {
        if (_roleDefaultYMap.TryGetValue(roleId, out var direct))
        {
            return direct;
        }

        var key = roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase) ? roleId[5..] : roleId;
        return _roleDefaultYMap.TryGetValue(key, out var fallback) ? fallback : 0;
    }

    private double ResolveRoleDefaultScale(string roleId)
    {
        if (_roleDefaultScaleMap.TryGetValue(roleId, out var direct))
        {
            return direct;
        }

        var key = roleId.StartsWith("role_", StringComparison.OrdinalIgnoreCase) ? roleId[5..] : roleId;
        return _roleDefaultScaleMap.TryGetValue(key, out var fallback) ? fallback : 1.0;
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    private void StartPreviewTypewriter(string? text, bool animate = true)
    {
        CancelPendingPreviewTypewriter();
        var totalVisibleCharacters = DialogueTextUtility.CountVisibleCharacters(text);
        if (totalVisibleCharacters <= 0)
        {
            PreviewVisibleCharacterCount = -1;
            IsPreviewTyping = false;
            return;
        }

        if (!animate)
        {
            PreviewVisibleCharacterCount = totalVisibleCharacters;
            IsPreviewTyping = false;
            return;
        }

        PreviewVisibleCharacterCount = 0;
        IsPreviewTyping = true;
        _previewTypewriterCts = new CancellationTokenSource();
        _ = RunPreviewTypewriterAsync(totalVisibleCharacters, _previewTypewriterCts.Token);
    }

    private async Task RunPreviewTypewriterAsync(int totalVisibleCharacters, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 1; i <= totalVisibleCharacters; i++)
            {
                await Task.Delay(25, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() => PreviewVisibleCharacterCount = i, DispatcherPriority.Render);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PreviewVisibleCharacterCount = totalVisibleCharacters;
                IsPreviewTyping = false;
            }, DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryCompletePreviewTypewriter()
    {
        if (!IsPreviewTyping)
        {
            return false;
        }

        var totalVisibleCharacters = DialogueTextUtility.CountVisibleCharacters(PreviewText);
        CancelPendingPreviewTypewriter();
        PreviewVisibleCharacterCount = totalVisibleCharacters <= 0 ? -1 : totalVisibleCharacters;
        return true;
    }

    private void CancelPendingPreviewTypewriter()
    {
        _previewTypewriterCts?.Cancel();
        _previewTypewriterCts?.Dispose();
        _previewTypewriterCts = null;
        IsPreviewTyping = false;
    }

    private string ResolveResourcePath(string? rawPath) =>
        ResourcePathResolver.Resolve(rawPath, _projectRoot, _resourcesRoot, _gameResourcesRoot);

    private bool TryResolveJumpFromAction(DialogueScriptAction action, out int targetIndex)
    {
        targetIndex = -1;
        if (action.Type != DialogueScriptActionType.Jump)
        {
            return false;
        }

        var fullId = action.TargetId.Trim();
        if (string.IsNullOrWhiteSpace(fullId))
        {
            return false;
        }

        var part = NormalizeIdPartFromFullId(_scene.Name, fullId);
        for (var i = 0; i < _scene.Lines.Count; i++)
        {
            if (_scene.Lines[i].IdPart.Equals(part, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeIdPartFromFullId(string sceneName, string fullId)
    {
        var id = fullId.Trim();
        var prefix = sceneName + "_";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[prefix.Length..];
        }

        if (id.StartsWith('*'))
        {
            id = id[1..];
        }

        return id;
    }

    private static string GetChoiceScriptByIndex(DialogueLine line, int index)
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

    private static List<(string id, bool isSpeaker)> ParseRoles(string rolesRaw)
    {
        var result = new List<(string id, bool isSpeaker)>();
        if (string.IsNullOrWhiteSpace(rolesRaw))
        {
            return result;
        }

        var tokens = rolesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tokenRaw in tokens)
        {
            var token = tokenRaw.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var isSpeaker = true;
            if (token.StartsWith('*'))
            {
                token = token[1..].Trim();
                isSpeaker = false;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            result.Add((token, isSpeaker));
            if (result.Count >= 2)
            {
                break;
            }
        }

        return result;
    }

    private void EndPreview(string hint)
    {
        CancelPendingVisualCommands();
        CancelPendingPreviewTypewriter();
        IsFinished = true;
        HideChoices();
        PreviewHint = hint;
    }

    private static Bitmap? LoadBitmapSafe(string file)
    {
        try
        {
            return new Bitmap(file);
        }
        catch
        {
            return null;
        }
    }
}
