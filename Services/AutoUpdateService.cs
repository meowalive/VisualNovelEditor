using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LibGit2Sharp;

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
    public string? ReleasePageUrl { get; init; }
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
    private const string ReleaseTag = "Release";
    private const string ReleaseExeName = "VNEditor.exe";
    private const string DownloadedExeTempName = "VNEditor.update.download";

    public static string GetReleasePageUrl()
    {
        return TryGetReleaseSource()?.ReleasePageUrl ?? string.Empty;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VNEditor-AutoUpdater/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            var source = TryGetReleaseSource();
            if (source == null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    ErrorMessage = "无法从当前 Git 仓库推断更新源。"
                };
            }
            ApplyGitCredentials(http, source);

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
            var releaseAsset = await GetTaggedReleaseExeAssetAsync(http, source);
            if (releaseAsset == null || string.IsNullOrWhiteSpace(releaseAsset.BrowserDownloadUrl))
            {
                releaseAsset = new ReleaseAssetInfo
                {
                    Name = ReleaseExeName,
                    BrowserDownloadUrl = source.DownloadUrl
                };
            }

            if (!string.IsNullOrWhiteSpace(releaseAsset.Sha256Digest)
                && string.Equals(localSha, releaseAsset.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentExePath = currentExe,
                    DownloadUrl = releaseAsset.BrowserDownloadUrl,
                    DownloadedTempPath = tempExe,
                    ReleasePageUrl = source.ReleasePageUrl,
                    LocalSha256 = localSha,
                    RemoteSha256 = releaseAsset.Sha256Digest
                };
            }

            if (string.IsNullOrWhiteSpace(releaseAsset.Sha256Digest))
            {
                var remoteSha = await DownloadAndHashRemoteExeAsync(http, releaseAsset.BrowserDownloadUrl, tempExe);
                if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(tempExe);
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.UpToDate,
                        CurrentExePath = currentExe,
                        DownloadUrl = releaseAsset.BrowserDownloadUrl,
                        DownloadedTempPath = tempExe,
                        ReleasePageUrl = source.ReleasePageUrl,
                        LocalSha256 = localSha,
                        RemoteSha256 = remoteSha
                    };
                }

                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    CurrentExePath = currentExe,
                    DownloadUrl = releaseAsset.BrowserDownloadUrl,
                    DownloadedTempPath = tempExe,
                    ReleasePageUrl = source.ReleasePageUrl,
                    LocalSha256 = localSha,
                    RemoteSha256 = remoteSha
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentExePath = currentExe,
                DownloadUrl = releaseAsset.BrowserDownloadUrl,
                DownloadedTempPath = tempExe,
                ReleasePageUrl = source.ReleasePageUrl,
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
            if (TryGetReleaseSource() is { } source)
            {
                ApplyGitCredentials(http, source);
            }

            if (!CanReuseDownloadedTemp(result))
            {
                await DownloadFileAsync(http, result.DownloadUrl, result.DownloadedTempPath, progress);
            }

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

    private static ReleaseSourceInfo? TryGetReleaseSource()
    {
        var remoteUrl = TryGetCurrentRepositoryRemoteUrl();
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        return TryBuildReleaseSource(remoteUrl);
    }

    private static string? TryGetCurrentRepositoryRemoteUrl()
    {
        try
        {
            var root = Repository.Discover(AppContext.BaseDirectory) ?? Repository.Discover(Environment.CurrentDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            using var repo = new Repository(root);
            var remoteName = repo.Head.RemoteName;
            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                var trackingRemote = repo.Network.Remotes[remoteName];
                if (!string.IsNullOrWhiteSpace(trackingRemote?.Url))
                {
                    return trackingRemote.Url;
                }
            }

            var origin = repo.Network.Remotes["origin"];
            if (!string.IsNullOrWhiteSpace(origin?.Url))
            {
                return origin.Url;
            }

            foreach (var remote in repo.Network.Remotes)
            {
                if (!string.IsNullOrWhiteSpace(remote.Url))
                {
                    return remote.Url;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static ReleaseSourceInfo? TryBuildReleaseSource(string remoteUrl)
    {
        var repoUrl = TryNormalizeRemoteUrl(remoteUrl);
        if (repoUrl == null)
        {
            return null;
        }

        var pathParts = repoUrl.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2)
        {
            return null;
        }

        var publicRepoUrl = StripUserInfo(repoUrl);
        var owner = Uri.EscapeDataString(pathParts[^2]);
        var repo = Uri.EscapeDataString(TrimGitSuffix(pathParts[^1]));
        var repoPath = string.Join("/", pathParts.Select(part => Uri.EscapeDataString(TrimGitSuffix(part))));
        var repoPageUrl = $"{publicRepoUrl.Scheme}://{publicRepoUrl.Authority}/{repoPath}";
        var apiUrl = IsGitHubHost(publicRepoUrl.Host)
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(ReleaseTag)}"
            : $"{publicRepoUrl.Scheme}://{publicRepoUrl.Authority}/api/v1/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(ReleaseTag)}";

        return new ReleaseSourceInfo
        {
            TaggedReleaseApiUrl = apiUrl,
            ReleasePageUrl = $"{repoPageUrl}/releases/tag/{Uri.EscapeDataString(ReleaseTag)}",
            DownloadUrl = $"{repoPageUrl}/releases/download/{Uri.EscapeDataString(ReleaseTag)}/{Uri.EscapeDataString(ReleaseExeName)}",
            RepositoryUrl = publicRepoUrl,
            EmbeddedCredentials = TryGetEmbeddedCredentials(repoUrl)
        };
    }

    private static Uri? TryNormalizeRemoteUrl(string remoteUrl)
    {
        var raw = remoteUrl.Trim();
        if (raw.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^4];
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return uri;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out uri)
            && uri.Scheme.Equals(Uri.UriSchemeSsh, StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return new Uri($"https://{authority}/{path}");
        }

        var colonIndex = raw.IndexOf(':');
        var atIndex = raw.IndexOf('@');
        if (atIndex >= 0 && colonIndex > atIndex)
        {
            var host = raw[(atIndex + 1)..colonIndex];
            var path = raw[(colonIndex + 1)..].Trim('/');
            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path))
            {
                return new Uri($"https://{host}/{path}");
            }
        }

        return null;
    }

    private static void ApplyGitCredentials(HttpClient http, ReleaseSourceInfo source)
    {
        var credentials = source.EmbeddedCredentials ?? TryGetGitCredentials(source.RepositoryUrl);
        if (credentials == null)
        {
            return;
        }

        var raw = $"{credentials.UserName}:{credentials.Password}";
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }

    private static GitHttpCredentials? TryGetEmbeddedCredentials(Uri repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl.UserInfo))
        {
            return null;
        }

        var parts = repoUrl.UserInfo.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        return new GitHttpCredentials
        {
            UserName = Uri.UnescapeDataString(parts[0]),
            Password = Uri.UnescapeDataString(parts[1])
        };
    }

    private static GitHttpCredentials? TryGetGitCredentials(Uri repoUrl)
    {
        try
        {
            var root = Repository.Discover(AppContext.BaseDirectory) ?? Repository.Discover(Environment.CurrentDirectory);
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "credential fill",
                    WorkingDirectory = string.IsNullOrEmpty(root) ? AppContext.BaseDirectory : root,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            p.Start();
            p.StandardInput.WriteLine($"protocol={repoUrl.Scheme}");
            p.StandardInput.WriteLine($"host={repoUrl.Authority}");
            p.StandardInput.WriteLine($"path={repoUrl.AbsolutePath.TrimStart('/')}");
            p.StandardInput.WriteLine();
            p.StandardInput.Close();

            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore kill failure
                }

                return null;
            }

            _ = errorTask;
            var output = outputTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0)
            {
                return null;
            }

            var userName = string.Empty;
            var password = string.Empty;
            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line[..idx];
                var value = line[(idx + 1)..];
                if (key.Equals("username", StringComparison.OrdinalIgnoreCase))
                {
                    userName = value;
                }
                else if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    password = value;
                }
            }

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            return new GitHttpCredentials
            {
                UserName = userName,
                Password = password
            };
        }
        catch
        {
            return null;
        }
    }

    private static Uri StripUserInfo(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri;
    }

    private static async Task<ReleaseAssetInfo?> GetTaggedReleaseExeAssetAsync(HttpClient http, ReleaseSourceInfo source)
    {
        using var resp = await http.GetAsync(source.TaggedReleaseApiUrl);
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

    private static async Task<string> DownloadAndHashRemoteExeAsync(HttpClient http, string downloadUrl, string tempExe)
    {
        await DownloadFileAsync(http, downloadUrl, tempExe);
        return ComputeSha256(tempExe);
    }

    private static bool CanReuseDownloadedTemp(UpdateCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadedTempPath) || !File.Exists(result.DownloadedTempPath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.RemoteSha256))
        {
            return false;
        }

        var downloadedSha = ComputeSha256(result.DownloadedTempPath);
        return string.Equals(downloadedSha, result.RemoteSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // ignore cleanup failure
        }
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

    private static bool IsGitHubHost(string host)
    {
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimGitSuffix(string value)
    {
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
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

    private sealed class ReleaseSourceInfo
    {
        public string TaggedReleaseApiUrl { get; init; } = string.Empty;
        public string ReleasePageUrl { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public Uri RepositoryUrl { get; init; } = new("https://localhost/");
        public GitHttpCredentials? EmbeddedCredentials { get; init; }
    }

    private sealed class GitHttpCredentials
    {
        public string UserName { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
    }
}
