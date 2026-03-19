using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace VNEditor.Services;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleaseAsset,
    Failed
}

public class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string? CurrentExePath { get; init; }
    public string? DownloadUrl { get; init; }
    public string? DownloadedTempPath { get; init; }
    public string? LocalSha256 { get; init; }
    public string? RemoteSha256 { get; init; }
    public string? ErrorMessage { get; init; }
}

public class UpdateDownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double Percentage { get; init; }
}

public static class AutoUpdateService
{
    private const string TaggedReleaseApi = "https://api.github.com/repos/meowalive/VisualNovelEditor/releases/tags/Release";
    private const string ReleasePageUrl = "https://github.com/meowalive/VisualNovelEditor/releases/tag/Release";
    private const string ReleaseExeName = "VNEditor.exe";
    private const string DownloadedExeTempName = "VNEditor.update.download";

    public static string GetReleasePageUrl()
    {
        return ReleasePageUrl;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VNEditor-AutoUpdater/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var releaseAsset = await GetTaggedReleaseExeAssetAsync(http);
            if (releaseAsset == null || string.IsNullOrWhiteSpace(releaseAsset.BrowserDownloadUrl))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.NoReleaseAsset
                };
            }

            var baseDir = AppContext.BaseDirectory;
            var currentExe = Path.Combine(baseDir, ReleaseExeName);
            if (!File.Exists(currentExe))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    ErrorMessage = "未找到当前可执行文件。"
                };
            }

            var tempExe = Path.Combine(baseDir, DownloadedExeTempName);
            var localSha = ComputeSha256(currentExe);
            if (!string.IsNullOrWhiteSpace(releaseAsset.Sha256Digest)
                && string.Equals(localSha, releaseAsset.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentExePath = currentExe,
                    DownloadUrl = releaseAsset.BrowserDownloadUrl,
                    DownloadedTempPath = tempExe,
                    LocalSha256 = localSha,
                    RemoteSha256 = releaseAsset.Sha256Digest
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentExePath = currentExe,
                DownloadUrl = releaseAsset.BrowserDownloadUrl,
                DownloadedTempPath = tempExe,
                LocalSha256 = localSha,
                RemoteSha256 = releaseAsset.Sha256Digest
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    public static void CleanupDownloadedTemp(UpdateCheckResult result)
    {
        if (result.Status != UpdateCheckStatus.UpdateAvailable || string.IsNullOrWhiteSpace(result.DownloadedTempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(result.DownloadedTempPath))
            {
                File.Delete(result.DownloadedTempPath);
            }
        }
        catch
        {
            // ignore cleanup failure
        }
    }

    public static async Task<bool> ApplyUpdateAndRestartAsync(UpdateCheckResult result, IProgress<UpdateDownloadProgress>? progress = null)
    {
        if (result.Status != UpdateCheckStatus.UpdateAvailable
            || string.IsNullOrWhiteSpace(result.CurrentExePath)
            || string.IsNullOrWhiteSpace(result.DownloadUrl)
            || string.IsNullOrWhiteSpace(result.DownloadedTempPath))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VNEditor-AutoUpdater/1.0");
            await DownloadFileAsync(http, result.DownloadUrl, result.DownloadedTempPath, progress);

            if (!string.IsNullOrWhiteSpace(result.RemoteSha256))
            {
                var downloadedSha = ComputeSha256(result.DownloadedTempPath);
                if (!string.Equals(downloadedSha, result.RemoteSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(result.DownloadedTempPath);
                    return false;
                }
            }

            var baseDir = Path.GetDirectoryName(result.CurrentExePath) ?? AppContext.BaseDirectory;
            StartUpdaterByCmd(result.CurrentExePath, result.DownloadedTempPath, baseDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ReleaseAssetInfo?> GetTaggedReleaseExeAssetAsync(HttpClient http)
    {
        using var resp = await http.GetAsync(TaggedReleaseApi);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl))
            {
                continue;
            }

            var name = nameEl.GetString() ?? string.Empty;
            var isTargetExe = name.Equals(ReleaseExeName, StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (!isTargetExe)
            {
                continue;
            }

            if (asset.TryGetProperty("browser_download_url", out var urlEl))
            {
                var downloadUrl = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var digest = string.Empty;
                if (asset.TryGetProperty("digest", out var digestEl))
                {
                    digest = NormalizeDigest(digestEl.GetString());
                }

                return new ReleaseAssetInfo
                {
                    Name = name,
                    BrowserDownloadUrl = downloadUrl,
                    Sha256Digest = digest
                };
            }
        }

        return null;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string outputPath, IProgress<UpdateDownloadProgress>? progress = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(outputPath);

        var total = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;

            var pct = total.HasValue && total.Value > 0
                ? (double)readTotal * 100d / total.Value
                : 0d;
            progress?.Report(new UpdateDownloadProgress
            {
                BytesReceived = readTotal,
                TotalBytes = total,
                Percentage = Math.Clamp(pct, 0, 100)
            });
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return string.Empty;
        }

        var v = digest.Trim();
        const string prefix = "sha256:";
        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            v = v[prefix.Length..];
        }

        return v.ToUpperInvariant();
    }

    private static void StartUpdaterByCmd(string currentExePath, string downloadedTempPath, string workingDirectory)
    {
        var targetEscaped = currentExePath.Replace("'", "''");
        var sourceEscaped = downloadedTempPath.Replace("'", "''");
        var script =
            $"$target='{targetEscaped}';" +
            $"$source='{sourceEscaped}';" +
            "for($i=0;$i -lt 40;$i++){" +
            "try{" +
            "if(Test-Path $target){Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue};" +
            "Move-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop;" +
            "Start-Process -FilePath $target;break" +
            "}catch{Start-Sleep -Seconds 1}" +
            "}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        });
    }

    private sealed class ReleaseAssetInfo
    {
        public string Name { get; init; } = string.Empty;
        public string BrowserDownloadUrl { get; init; } = string.Empty;
        public string Sha256Digest { get; init; } = string.Empty;
    }
}
