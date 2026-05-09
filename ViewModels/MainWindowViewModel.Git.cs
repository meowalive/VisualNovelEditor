using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VNEditor.Models;
using VNEditor.Services;

namespace VNEditor.ViewModels;

/// <summary>工程目录 Git：打开时拉取、保存提交、检出、推送（与 UI 通过 <see cref="IGitUserNotify"/> 解耦）。</summary>
public partial class MainWindowViewModel
{
    public IGitUserNotify? GitNotify { get; set; }
    private string _gitUserDisplayName = string.Empty;

    public string GitUserDisplayName
    {
        get => _gitUserDisplayName;
        private set
        {
            if (string.Equals(_gitUserDisplayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _gitUserDisplayName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GitSyncUserText));
        }
    }

    public string GitSyncUserText =>
        string.IsNullOrWhiteSpace(GitUserDisplayName) ? string.Empty : $"同步者：{GitUserDisplayName}";

    private void RefreshGitUserDisplayName()
    {
        GitUserDisplayName = GitPanelEnabled && !string.IsNullOrEmpty(_projectRoot)
            ? ProjectGitService.GetGitUserDisplayName(_projectRoot)
            : string.Empty;
    }

    private void RefreshPendingProjectImagePaths()
    {
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        var before = _pendingGitCommitPaths.Count;
        foreach (var path in ProjectGitService.CollectChangedProjectImageFiles(_projectRoot, _projectRoot))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _pendingGitCommitPaths.Add(Path.GetFullPath(path));
            }
        }

        if (_pendingGitCommitPaths.Count != before)
        {
            OnPropertyChanged(nameof(HasUnpushedCommits));
            GitPushCommand.NotifyCanExecuteChanged();
        }
    }

    private void AfterProjectOpenedForGit()
    {
        if (string.IsNullOrEmpty(_projectRoot))
        {
            GitPanelEnabled = false;
            GitUserDisplayName = string.Empty;
            GitAheadBy = 0;
            GitStatusHint = string.Empty;
            return;
        }

        GitPanelEnabled = ProjectGitService.IsGitRepository(_projectRoot);
        RefreshGitUserDisplayName();
        RefreshPendingProjectImagePaths();
        RefreshGitAhead();
        if (GitPanelEnabled)
        {
            UpdateGitStatusHintForPendingAndAhead();
        }
        else
        {
            GitStatusHint = string.Empty;
        }
        _ = RunGitPullOnOpenAsync();
    }

    private async Task RunGitPullOnOpenAsync()
    {
        if (!GitPanelEnabled || GitNotify == null || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        GitBusy = true;
        try
        {
            if (!TryFlushPendingGitCommit())
            {
                return;
            }

            var result = await Task.Run(() => ProjectGitService.PullMergePreferRemote(_projectRoot))
                .ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyGitPullResult(result));
        }
        finally
        {
            GitBusy = false;
        }
    }

    private void ApplyGitPullResult(GitPullMergeResult result)
    {
        if (!result.Success)
        {
            GitPanelEnabled = ProjectGitService.IsGitRepository(_projectRoot);
            _ = GitNotify?.ShowMessageAsync("Git 拉取失败", result.ErrorMessage ?? "未知错误");
            return;
        }

        if (result.Kind == GitPullKind.NotRepository)
        {
            GitPanelEnabled = false;
            GitAheadBy = 0;
            return;
        }

        GitPanelEnabled = true;

        if (result.Kind == GitPullKind.NoUpstream)
        {
            RefreshGitAhead();
            GitStatusHint = "未配置上游分支，无法拉取/推送。";
            return;
        }

        if (result.Kind == GitPullKind.UpToDate)
        {
            RefreshGitAhead();
            UpdateGitStatusHintForPendingAndAhead();
            return;
        }

        var warnings = result.Kind == GitPullKind.MergedResolvedRemote
            ? result.ConflictLineWarnings
            : Array.Empty<(string SceneName, string IdPart)>();

        ReloadDialogueScenesFromDisk(warnings);
        LoadRoleEntries(_projectRoot);
        RefreshRoleMapsAndOptions();
        RefreshAllScenePreviews();
        RefreshGitAhead();
        UpdateGitStatusHintForPendingAndAhead();

        if (result.Kind == GitPullKind.MergedResolvedRemote && warnings.Count > 0)
        {
            _ = GitNotify?.ShowMessageAsync(
                "合并完成",
                "已与远端合并；冲突部分已采用远端版本。对话行列表中带 ⚠ 的行请人工核对。");
        }
    }

    /// <summary>上传前拉取成功后的 UI。</summary>
    private void ApplyGitPullResultAfterPrePush(GitPullMergeResult result)
    {
        GitPanelEnabled = ProjectGitService.IsGitRepository(_projectRoot);
        if (result.Kind == GitPullKind.NoUpstream)
        {
            RefreshGitAhead();
            GitStatusHint = "未配置上游分支，无法拉取/推送。";
            return;
        }

        if (result.Kind == GitPullKind.UpToDate)
        {
            RefreshGitAhead();
            UpdateGitStatusHintForPendingAndAhead();
            return;
        }

        ReloadDialogueScenesFromDisk(null);
        LoadRoleEntries(_projectRoot);
        RefreshRoleMapsAndOptions();
        RefreshAllScenePreviews();
        RefreshGitAhead();
        UpdateGitStatusHintForPendingAndAhead();
    }

    private void ReloadDialogueScenesFromDisk(IReadOnlyList<(string SceneName, string IdPart)>? conflictWarnings)
    {
        if (string.IsNullOrEmpty(_openedDataDialogueDir) || string.IsNullOrEmpty(_openedTextDialogueDir))
        {
            return;
        }

        var warnSet = conflictWarnings?.ToHashSet() ?? new HashSet<(string, string)>();
        var selSceneName = SelectedScene?.Name;
        var selLineId = SelectedLine?.IdPart;

        var loaded = DialogueProjectService.LoadScenes(_openedDataDialogueDir, _openedTextDialogueDir);
        Scenes.Clear();
        foreach (var scene in loaded)
        {
            scene.IsDirty = false;
            RefreshScenePreview(scene);
            foreach (var line in scene.Lines)
            {
                line.HasGitSyncWarning = warnSet.Contains((scene.Name, line.IdPart));
            }

            Scenes.Add(scene);
        }

        if (!string.IsNullOrEmpty(selSceneName))
        {
            SelectedScene = Scenes.FirstOrDefault(s => s.Name == selSceneName) ?? Scenes.FirstOrDefault();
        }
        else
        {
            SelectedScene = Scenes.FirstOrDefault();
        }

        if (SelectedScene != null && !string.IsNullOrEmpty(selLineId))
        {
            SelectedLine = SelectedScene.Lines.FirstOrDefault(l => l.IdPart == selLineId);
        }
    }

    private void RefreshGitAhead()
    {
        if (string.IsNullOrEmpty(_projectRoot) || !ProjectGitService.IsGitRepository(_projectRoot))
        {
            GitAheadBy = 0;
            return;
        }

        GitAheadBy = ProjectGitService.GetAheadBy(_projectRoot);
    }

    private void QueuePendingGitPaths(IEnumerable<string> absolutePaths)
    {
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        foreach (var p in absolutePaths)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                continue;
            }

            var full = Path.GetFullPath(p);
            _pendingGitCommitPaths.Add(full);
        }

        UpdateGitStatusHintForPendingAndAhead();
        OnPropertyChanged(nameof(HasUnpushedCommits));
        GitPushCommand.NotifyCanExecuteChanged();
    }

    /// <returns>是否成功提交或无需提交。</returns>
    private bool TryFlushPendingGitCommit()
    {
        RefreshPendingProjectImagePaths();
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot) || _pendingGitCommitPaths.Count == 0)
        {
            return true;
        }

        RefreshGitUserDisplayName();
        var paths = _pendingGitCommitPaths.ToList();
        var (ok, err) = ProjectGitService.CommitTrackedFiles(
            _projectRoot,
            paths,
            $"{GitUserDisplayName}: 本地编辑");
        if (!ok)
        {
            if (GitNotify != null)
            {
                _ = GitNotify.ShowMessageAsync("Git 提交失败", err ?? string.Empty);
            }

            return false;
        }

        _pendingGitCommitPaths.Clear();
        RefreshGitAhead();
        UpdateGitStatusHintForPendingAndAhead();
        OnPropertyChanged(nameof(HasUnpushedCommits));
        GitPushCommand.NotifyCanExecuteChanged();
        return true;
    }

    private void UpdateGitStatusHintForPendingAndAhead()
    {
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        RefreshPendingProjectImagePaths();

        if (GitAheadBy > 0 && _pendingGitCommitPaths.Count > 0)
        {
            GitStatusHint = $"未推送提交：{GitAheadBy}；另有已保存编辑/工程内图片将合并提交";
        }
        else if (_pendingGitCommitPaths.Count > 0)
        {
            GitStatusHint = "已保存的编辑与工程内图片改动将在上传时合并为一次提交。";
        }
        else
        {
            GitStatusHint = GitAheadBy > 0 ? $"未推送提交：{GitAheadBy}" : "与远端同步。";
        }
    }

    private void TryGitCommitAfterSaveScene(DialogueScene scene)
    {
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        var dataPath = Path.Combine(_openedDataDialogueDir, $"{scene.Name}.csv");
        var textPath = Path.Combine(_openedTextDialogueDir, $"{scene.Name}.csv");
        QueuePendingGitPaths(new[] { dataPath, textPath });

        foreach (var line in scene.Lines)
        {
            line.HasGitSyncWarning = false;
        }
    }

    private void TryGitCommitAfterSaveRoles(IEnumerable<string>? extraPaths = null)
    {
        if (!GitPanelEnabled || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        var paths = CollectRoleCsvAbsolutePaths(_projectRoot);
        if (extraPaths != null)
        {
            paths.AddRange(extraPaths.Where(static p => !string.IsNullOrWhiteSpace(p)));
        }

        paths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            return;
        }

        QueuePendingGitPaths(paths);
    }

    private static List<string> CollectRoleCsvAbsolutePaths(string projectRoot)
    {
        var paths = new List<string>();
        var dataDir = DialogueProjectService.GetRoleDataDataDir(projectRoot);
        var textDir = DialogueProjectService.GetRoleDataTextDir(projectRoot);
        if (Directory.Exists(dataDir))
        {
            paths.AddRange(Directory.GetFiles(dataDir, "*.csv"));
        }

        if (Directory.Exists(textDir))
        {
            paths.AddRange(Directory.GetFiles(textDir, "*.csv"));
        }

        return paths;
    }

    [RelayCommand(CanExecute = nameof(CanRunGitCheckoutScene))]
    private async Task GitCheckoutSceneFilesAsync()
    {
        if (SelectedScene == null || string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        GitBusy = true;
        try
        {
            if (!TryFlushPendingGitCommit())
            {
                return;
            }

            RefreshGitAhead();

            var dataPath = Path.Combine(_openedDataDialogueDir, $"{SelectedScene.Name}.csv");
            var textPath = Path.Combine(_openedTextDialogueDir, $"{SelectedScene.Name}.csv");
            var paths = new[] { dataPath, textPath };
            var (ok, err) = await Task.Run(() =>
                ProjectGitService.CheckoutFilesFromTrackedRemote(_projectRoot, paths)).ConfigureAwait(true);

            if (!ok)
            {
                if (GitNotify != null)
                {
                    await GitNotify.ShowMessageAsync("签出失败", err ?? string.Empty);
                }

                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var name = SelectedScene.Name;
                var idx = Scenes.IndexOf(SelectedScene);
                var reloaded = DialogueProjectService.LoadSceneFromDisk(name, dataPath, textPath);
                reloaded.IsDirty = false;
                RefreshScenePreview(reloaded);
                foreach (var line in reloaded.Lines)
                {
                    line.HasGitSyncWarning = false;
                }

                if (idx >= 0)
                {
                    Scenes.RemoveAt(idx);
                    Scenes.Insert(idx, reloaded);
                }

                SelectedScene = reloaded;
                SelectedLine = reloaded.Lines.FirstOrDefault();
                StatusText = $"已从远端拉取场景文件：{name}";
                _gitCheckedOutSceneNames.Add(reloaded.Name);
                RefreshDialogueEditingState();
            });
        }
        finally
        {
            GitBusy = false;
        }
    }

    private bool CanRunGitCheckoutScene() => GitPanelEnabled && !GitBusy && SelectedScene != null;

    [RelayCommand(CanExecute = nameof(CanRunGitPullAll))]
    private async Task GitPullAllAsync()
    {
        if (string.IsNullOrEmpty(_projectRoot) || GitNotify == null)
        {
            return;
        }

        GitBusy = true;
        try
        {
            if (!TryFlushPendingGitCommit())
            {
                return;
            }

            RefreshGitAhead();

            var result = await Task.Run(() =>
                ProjectGitService.PullMergePreferRemoteWithLfs(_projectRoot)).ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyGitPullResult(result));
        }
        finally
        {
            GitBusy = false;
        }
    }

    private bool CanRunGitPullAll() => GitPanelEnabled && !GitBusy;

    [RelayCommand(CanExecute = nameof(CanRunGitCheckoutRole))]
    private async Task GitCheckoutRoleFilesAsync()
    {
        if (string.IsNullOrEmpty(_projectRoot) || GitNotify == null)
        {
            return;
        }

        GitBusy = true;
        try
        {
            if (!TryFlushPendingGitCommit())
            {
                return;
            }

            RefreshGitAhead();

            var (ok, err) = await Task.Run(() =>
                ProjectGitService.TryCheckoutRoleDataFromTrackedRemote(_projectRoot, _projectRoot)).ConfigureAwait(true);

            if (!ok)
            {
                await GitNotify.ShowMessageAsync("签出失败", err ?? string.Empty);
                return;
            }

            var (lfsOk, lfsErr) = await Task.Run(() => ProjectGitService.TryRunGitLfsPull(_projectRoot)).ConfigureAwait(true);
            if (!lfsOk)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await GitNotify.ShowMessageAsync("Git LFS", lfsErr ?? "git lfs pull/checkout 失败。"));
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadRoleEntries(_projectRoot);
                RefreshRoleMapsAndOptions();
                _gitRolesCheckedOut = true;
                StatusText = "已从远端拉取角色数据。";
                RefreshRoleEditingState();
            });
        }
        finally
        {
            GitBusy = false;
        }
    }

    private bool CanRunGitCheckoutRole() => GitPanelEnabled && !GitBusy;

    [RelayCommand(CanExecute = nameof(CanRunGitPush))]
    private async Task GitPushAsync()
    {
        if (string.IsNullOrEmpty(_projectRoot))
        {
            return;
        }

        GitBusy = true;
        try
        {
            if (!TryFlushPendingGitCommit())
            {
                return;
            }

            RefreshGitAhead();

            var pullResult = await Task.Run(() => ProjectGitService.PullMergeBeforePush(_projectRoot)).ConfigureAwait(true);
            if (!pullResult.Success)
            {
                if (GitNotify != null)
                {
                    await GitNotify.ShowMessageAsync("无法上传", pullResult.ErrorMessage ?? "拉取失败。");
                }

                RefreshGitAhead();
                UpdateGitStatusHintForPendingAndAhead();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyGitPullResultAfterPrePush(pullResult));

            var (ok, err) = await Task.Run(() => ProjectGitService.Push(_projectRoot)).ConfigureAwait(true);

            if (GitNotify != null)
            {
                if (ok)
                {
                    await GitNotify.ShowMessageAsync("上传完成", "已推送到远端。");
                }
                else
                {
                    await GitNotify.ShowMessageAsync("上传失败", err ?? string.Empty);
                }
            }

            RefreshGitAhead();
            UpdateGitStatusHintForPendingAndAhead();
        }
        finally
        {
            GitBusy = false;
        }
    }

    private bool CanRunGitPush() =>
        GitPanelEnabled && !GitBusy && (GitAheadBy > 0 || _pendingGitCommitPaths.Count > 0);

    partial void OnGitPanelEnabledChanged(bool value)
    {
        RefreshGitUserDisplayName();
        GitCheckoutSceneFilesCommand.NotifyCanExecuteChanged();
        GitCheckoutRoleFilesCommand.NotifyCanExecuteChanged();
        GitPullAllCommand.NotifyCanExecuteChanged();
        GitPushCommand.NotifyCanExecuteChanged();
        RefreshDialogueEditingState();
        RefreshRoleEditingState();
    }

    partial void OnGitBusyChanged(bool value)
    {
        GitCheckoutSceneFilesCommand.NotifyCanExecuteChanged();
        GitCheckoutRoleFilesCommand.NotifyCanExecuteChanged();
        GitPullAllCommand.NotifyCanExecuteChanged();
        GitPushCommand.NotifyCanExecuteChanged();
    }

    partial void OnGitAheadByChanged(int value)
    {
        GitPushCommand.NotifyCanExecuteChanged();
    }

    /// <summary>关闭窗口前调用：选「是」则尝试推送；选「否」则将分支硬重置到上游并重新加载对话数据，再允许关闭。</summary>
    public async Task<bool> ConfirmCloseWithOptionalPushAsync()
    {
        if (!TryFlushPendingGitCommit())
        {
            return false;
        }

        RefreshGitAhead();
        if (GitAheadBy == 0 || GitNotify == null)
        {
            return true;
        }

        var push = await GitNotify.ShowYesNoAsync(
            "未上传的提交",
            $"本地有 {GitAheadBy} 个提交尚未推送到远端。\n\n"
            + "「是」：上传后关闭。\n"
            + "「否」：撤销这些未推送的提交，并将工作区对齐远端跟踪分支后关闭（未推送提交将丢失；下次编辑需重新签出场景与角色数据）。");
        if (!push)
        {
            if (string.IsNullOrEmpty(_projectRoot))
            {
                return true;
            }

            var (resetOk, resetErr) = await Task.Run(() => ProjectGitService.ResetHardToUpstream(_projectRoot)).ConfigureAwait(true);
            if (!resetOk)
            {
                await GitNotify.ShowMessageAsync("Git", resetErr ?? "无法对齐远端，提交未撤销。");
                return true;
            }

            ReloadDialogueScenesFromDisk(null);
            LoadRoleEntries(_projectRoot);
            RefreshRoleMapsAndOptions();
            RefreshAllScenePreviews();
            _gitCheckedOutSceneNames.Clear();
            _gitRolesCheckedOut = false;
            _pendingGitCommitPaths.Clear();
            RefreshDialogueEditingState();
            RefreshRoleEditingState();
            RefreshGitAhead();
            GitStatusHint = "与远端同步。";
            OnPropertyChanged(nameof(HasUnpushedCommits));
            return true;
        }

        GitBusy = true;
        try
        {
            var pullResult = await Task.Run(() => ProjectGitService.PullMergeBeforePush(_projectRoot)).ConfigureAwait(true);
            if (!pullResult.Success)
            {
                await GitNotify.ShowMessageAsync("无法上传", pullResult.ErrorMessage ?? "拉取失败。");
                RefreshGitAhead();
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyGitPullResultAfterPrePush(pullResult));

            var (ok, err) = await Task.Run(() => ProjectGitService.Push(_projectRoot)).ConfigureAwait(true);
            if (!ok)
            {
                await GitNotify.ShowMessageAsync("上传失败", err ?? string.Empty);
            }
        }
        finally
        {
            GitBusy = false;
        }

        RefreshGitAhead();
        UpdateGitStatusHintForPendingAndAhead();
        return true;
    }
}
