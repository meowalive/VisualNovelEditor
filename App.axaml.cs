using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VNEditor.Services;
using VNEditor.ViewModels;
using VNEditor.Views;

namespace VNEditor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            desktop.MainWindow = mainWindow;
            mainWindow.Opened += async (_, _) => await RunStartupUpdateFlowAsync(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private static async Task RunStartupUpdateFlowAsync(MainWindow mainWindow)
    {
        var vm = mainWindow.DataContext as MainWindowViewModel;
        CancellationTokenSource? spinnerCts = null;
        Task? spinnerTask = null;
        if (vm != null)
        {
            vm.StartupUpdateCheckingText = "正在检测更新……";
            vm.StartupSpinnerDashOffset = 0;
            vm.IsStartupUpdateDownloading = false;
            vm.StartupUpdateDownloadProgress = 0;
            vm.IsStartupUpdateChecking = true;
            spinnerCts = new CancellationTokenSource();
            spinnerTask = StartSpinnerAsync(vm, spinnerCts.Token);
        }

        var result = await AutoUpdateService.CheckForUpdateAsync();
        if (vm != null)
        {
            vm.IsStartupUpdateChecking = false;
        }
        if (spinnerCts != null)
        {
            spinnerCts.Cancel();
            try
            {
                if (spinnerTask != null)
                {
                    await spinnerTask;
                }
            }
            catch
            {
                // ignore spinner cancel errors
            }
            spinnerCts.Dispose();
        }

        if (result.Status == UpdateCheckStatus.UpdateAvailable)
        {
            var ask = new UpdatePromptWindow(
                result.LocalSha256 ?? string.Empty,
                result.RemoteSha256 ?? string.Empty,
                AutoUpdateService.GetReleasePageUrl());
            ask.RequestedThemeVariant = mainWindow.ActualThemeVariant;
            var shouldUpdate = await ask.ShowDialog<bool>(mainWindow);
            if (!shouldUpdate)
            {
                AutoUpdateService.CleanupDownloadedTemp(result);
                var canceled = new UpdateResultWindow("已取消更新。");
                canceled.RequestedThemeVariant = mainWindow.ActualThemeVariant;
                await canceled.ShowDialog(mainWindow);
                return;
            }

            if (vm != null)
            {
                vm.IsStartupUpdateChecking = true;
                vm.IsStartupUpdateDownloading = true;
                vm.StartupUpdateDownloadProgress = 0;
                vm.StartupUpdateCheckingText = "正在下载更新……";
            }

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (vm == null)
                {
                    return;
                }

                vm.StartupUpdateDownloadProgress = p.Percentage;
                vm.StartupUpdateCheckingText = p.TotalBytes.HasValue && p.TotalBytes.Value > 0
                    ? $"正在下载更新…… {p.Percentage:0.0}%"
                    : "正在下载更新……";
            });

            if (await AutoUpdateService.ApplyUpdateAndRestartAsync(result, progress))
            {
                Environment.Exit(0);
                return;
            }

            AutoUpdateService.CleanupDownloadedTemp(result);
            if (vm != null)
            {
                vm.IsStartupUpdateDownloading = false;
                vm.IsStartupUpdateChecking = false;
            }
            var failed = new UpdateResultWindow("更新失败：无法替换并重启。");
            failed.RequestedThemeVariant = mainWindow.ActualThemeVariant;
            await failed.ShowDialog(mainWindow);
            return;
        }

        if (result.Status == UpdateCheckStatus.UpToDate)
        {
            return;
        }

        if (result.Status == UpdateCheckStatus.NoReleaseAsset)
        {
            var noAsset = new UpdateResultWindow("检测完成：未找到可下载的 VNEditor.exe 资产。");
            noAsset.RequestedThemeVariant = mainWindow.ActualThemeVariant;
            await noAsset.ShowDialog(mainWindow);
            return;
        }

        var error = new UpdateResultWindow($"更新检测失败：{result.ErrorMessage ?? "未知错误"}");
        error.RequestedThemeVariant = mainWindow.ActualThemeVariant;
        await error.ShowDialog(mainWindow);
    }

    private static async Task StartSpinnerAsync(MainWindowViewModel vm, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.StartupSpinnerDashOffset -= 8;
            });

            try
            {
                await Task.Delay(45, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}