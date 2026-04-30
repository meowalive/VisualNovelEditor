using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    private const string ReleaseSha256Name = ReleaseExeName + ".sha256";
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
                    ErrorMessage = "无法读取构建时写入的更新源。"
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
            var releaseLookup = await GetTaggedReleaseExeAssetAsync(http, source);
            if (releaseLookup.RequiresLogin && TryRefreshGitCredentials(source.RepositoryUrl))
            {
                using var retryHttp = new HttpClient();
                retryHttp.Timeout = TimeSpan.FromSeconds(25);
                retryHttp.DefaultRequestHeaders.UserAgent.ParseAdd("VNEditor-AutoUpdater/1.0");
                retryHttp.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                ApplyGitCredentials(retryHttp, source);
                releaseLookup = await GetTaggedReleaseExeAssetAsync(retryHttp, source);
            }

            var releaseAsset = releaseLookup.Asset;
            if (releaseAsset == null || string.IsNullOrWhiteSpace(releaseAsset.BrowserDownloadUrl))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.NoReleaseAsset,
                    ReleasePageUrl = source.ReleasePageUrl,
                    ErrorMessage = releaseLookup.ErrorMessage ?? "未找到可下载的 VNEditor.exe 资产。"
                };
            }

            if (string.IsNullOrWhiteSpace(releaseAsset.Sha256Digest))
            {
                releaseAsset.Sha256Digest = await GetReleaseAssetSha256Async(http, releaseAsset);
                if (string.IsNullOrWhiteSpace(releaseAsset.Sha256Digest))
                {
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.NoReleaseAsset,
                        CurrentExePath = currentExe,
                        DownloadUrl = releaseAsset.BrowserDownloadUrl,
                        DownloadedTempPath = tempExe,
                        ReleasePageUrl = source.ReleasePageUrl,
                        LocalSha256 = localSha,
                        ErrorMessage = "远端发布资产未提供 SHA256，且未找到 VNEditor.exe.sha256。"
                    };
                }
            }

            if (string.Equals(localSha, releaseAsset.Sha256Digest, StringComparison.OrdinalIgnoreCase))
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
        var remoteUrl = GetBuildRepositoryRemoteUrl();
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        return TryBuildReleaseSource(remoteUrl);
    }

    private static string? GetBuildRepositoryRemoteUrl()
    {
        var assembly = typeof(AutoUpdateService).Assembly;
        foreach (var meta in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (meta.Key.Equals("EditorRepositoryRemoteUrl", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(meta.Value))
            {
                return meta.Value;
            }
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
        return TryGetGitCredentials(repoUrl, includePath: true)
               ?? TryGetGitCredentials(repoUrl, includePath: false);
    }

    private static GitHttpCredentials? TryGetGitCredentials(Uri repoUrl, bool includePath)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "credential fill",
                    WorkingDirectory = AppContext.BaseDirectory,
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
            if (includePath && !string.IsNullOrWhiteSpace(repoUrl.AbsolutePath.Trim('/')))
            {
                p.StandardInput.WriteLine($"path={repoUrl.AbsolutePath.TrimStart('/')}");
            }

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

    private static bool TryRefreshGitCredentials(Uri repoUrl)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                 {
                    FileName = "git",
                    Arguments = $"ls-remote {QuoteGitArgument(repoUrl.ToString())} HEAD",
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            p.Start();
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore kill failure
                }

                return false;
            }

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteGitArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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

    private static async Task<ReleaseLookupResult> GetTaggedReleaseExeAssetAsync(HttpClient http, ReleaseSourceInfo source)
    {
        using var resp = await http.GetAsync(source.TaggedReleaseApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            return new ReleaseLookupResult
            {
                ErrorMessage = $"Release API 请求失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}。地址：{source.TaggedReleaseApiUrl}",
                RequiresLogin = (int)resp.StatusCode is 401 or 403
            };
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return new ReleaseLookupResult
            {
                ErrorMessage = $"Release API 返回中没有 assets 数组。地址：{source.TaggedReleaseApiUrl}"
            };
        }

        ReleaseAssetInfo? exeAsset = null;
        string? sha256DownloadUrl = null;
        var assetNames = new List<string>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl))
            {
                continue;
            }

            var name = nameEl.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                assetNames.Add(name);
            }

            if (asset.TryGetProperty("browser_download_url", out var urlEl))
            {
                var downloadUrl = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                if (name.Equals(ReleaseSha256Name, StringComparison.OrdinalIgnoreCase))
                {
                    sha256DownloadUrl = downloadUrl;
                    continue;
                }

                var isTargetExe = name.Equals(ReleaseExeName, StringComparison.OrdinalIgnoreCase)
                                  || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (!isTargetExe)
                {
                    continue;
                }

                exeAsset ??= new ReleaseAssetInfo
                {
                    Name = name,
                    BrowserDownloadUrl = downloadUrl,
                    Sha256Digest = ReadSha256Digest(asset)
                };
            }
        }

        if (exeAsset != null)
        {
            exeAsset.Sha256DownloadUrl = sha256DownloadUrl;
        }

        return new ReleaseLookupResult
        {
            Asset = exeAsset,
            ErrorMessage = exeAsset == null
                ? assetNames.Count == 0
                    ? $"Release 中没有上传资产。地址：{source.ReleasePageUrl}"
                    : $"Release 中没有找到 {ReleaseExeName}。已有资产：{string.Join(", ", assetNames)}"
                : null
        };
    }

    private static async Task<string> GetReleaseAssetSha256Async(HttpClient http, ReleaseAssetInfo releaseAsset)
    {
        if (string.IsNullOrWhiteSpace(releaseAsset.Sha256DownloadUrl))
        {
            return string.Empty;
        }

        using var response = await http.GetAsync(releaseAsset.Sha256DownloadUrl);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        var text = await response.Content.ReadAsStringAsync();
        return ParseSha256Text(text);
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

    private static string ReadSha256Digest(JsonElement asset)
    {
        foreach (var propertyName in new[] { "digest", "sha256", "sha256_digest", "sha256sum" })
        {
            if (asset.TryGetProperty(propertyName, out var digestEl))
            {
                var digest = NormalizeDigest(digestEl.GetString());
                if (!string.IsNullOrWhiteSpace(digest))
                {
                    return digest;
                }
            }
        }

        return string.Empty;
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

    private static string ParseSha256Text(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var firstToken = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return NormalizeDigest(firstToken);
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
        public string Sha256Digest { get; set; } = string.Empty;
        public string? Sha256DownloadUrl { get; set; }
    }

    private sealed class ReleaseLookupResult
    {
        public ReleaseAssetInfo? Asset { get; init; }
        public string? ErrorMessage { get; init; }
        public bool RequiresLogin { get; init; }
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
