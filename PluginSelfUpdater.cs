using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GBot.Plugins.EpayShop;

/// <summary>
/// 从 GitHub Release 自更新：下载到 PayBot.dll.new，等 GBot 退出后覆盖正式 DLL。
/// </summary>
internal static class PluginSelfUpdater
{
    public const string DefaultOwner = "TenYearsPro";
    public const string DefaultRepo = "PayBotdll";
    public const string DefaultAsset = "PayBot.dll";

    private static readonly string[] DefaultProxies =
    [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    ];

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object ApplyGate = new();
    private static Process? _applyProcess;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PayBot-SelfUpdater/1.0");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string GetInstalledDllPath()
    {
        var loc = typeof(EpayShopPlugin).Assembly.Location;
        if (!string.IsNullOrEmpty(loc))
            return loc;
        return Path.Combine(AppContext.BaseDirectory, DefaultAsset);
    }

    public static string GetPendingDllPath() => GetInstalledDllPath() + ".new";

    public static string LocalVersion => new EpayShopPlugin().GetPluginInfo().Version;

    public sealed class CheckResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public string? RemoteTag { get; init; }
        public string? RemoteVersion { get; init; }
        public bool HasUpdate { get; init; }
        public bool Downloaded { get; init; }
        public string? AssetUrl { get; init; }
    }

    /// <summary>检查并可选下载最新正式版。</summary>
    public static async Task<CheckResult> CheckAndUpdateAsync(
        GlobalConfig cfg,
        bool downloadIfNewer,
        string localVersion,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var owner = string.IsNullOrWhiteSpace(cfg.UpdateOwner) ? DefaultOwner : cfg.UpdateOwner.Trim();
        var repo = string.IsNullOrWhiteSpace(cfg.UpdateRepo) ? DefaultRepo : cfg.UpdateRepo.Trim();
        var assetName = string.IsNullOrWhiteSpace(cfg.UpdateAsset) ? DefaultAsset : cfg.UpdateAsset.Trim();
        // 不要只用 /releases/latest：仓库里可能混有无关 tag（如 Bot3.2.7），导致「有新版本但没有 PayBot.dll」
        var listApi = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20";

        try
        {
            var (body, apiVia) = await GetStringWithProxiesAsync(listApi, cfg, ct, log);
            if (body is null)
            {
                return new CheckResult
                {
                    Ok = false,
                    Message = "GitHub API 请求失败（代理与直连均不可用）",
                };
            }

            log?.Invoke($"Release 列表来自：{apiVia}");

            var releases = JsonSerializer.Deserialize<List<GhRelease>>(body);
            if (releases is null || releases.Count == 0)
                return new CheckResult { Ok = false, Message = "无法解析 Release 信息" };

            GhRelease? release = null;
            GhAsset? asset = null;
            foreach (var cand in releases)
            {
                if (string.IsNullOrWhiteSpace(cand.TagName)) continue;
                if (cand.Draft) continue;
                if (cand.Prerelease && !cfg.UpdateIncludePrerelease) continue;
                var hit = cand.Assets?.FirstOrDefault(a =>
                    string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
                if (hit is null) continue;
                release = cand;
                asset = hit;
                break;
            }

            if (release is null || asset is null)
            {
                return new CheckResult
                {
                    Ok = false,
                    Message = $"最近 Release 中都没有附件 `{assetName}`",
                };
            }

            var remoteVer = NormalizeVersion(release.TagName);
            var localVer = NormalizeVersion(localVersion);
            var newer = IsNewer(remoteVer, localVer);

            if (!newer)
            {
                return new CheckResult
                {
                    Ok = true,
                    HasUpdate = false,
                    RemoteTag = release.TagName,
                    RemoteVersion = remoteVer,
                    Message = $"已是最新：本地 {localVer}，远程 {release.TagName}",
                };
            }

            if (!downloadIfNewer)
            {
                return new CheckResult
                {
                    Ok = true,
                    HasUpdate = true,
                    RemoteTag = release.TagName,
                    RemoteVersion = remoteVer,
                    AssetUrl = asset.BrowserDownloadUrl,
                    Message = $"发现新版本 {release.TagName}（本地 {localVer}）",
                };
            }

            var pending = GetPendingDllPath();
            var tmp = pending + ".downloading";
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);

            var (dlOk, dlVia, dlErr) = await DownloadWithProxiesAsync(
                asset.BrowserDownloadUrl, tmp, cfg, ct, log);
            if (!dlOk)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* */ }
                return new CheckResult
                {
                    Ok = false,
                    HasUpdate = true,
                    RemoteTag = release.TagName,
                    RemoteVersion = remoteVer,
                    Message = "下载失败：" + (dlErr ?? "未知错误"),
                };
            }

            if (new FileInfo(tmp).Length < 1024)
            {
                try { File.Delete(tmp); } catch { /* */ }
                return new CheckResult { Ok = false, Message = "下载文件过小，已取消" };
            }

            File.Copy(tmp, pending, overwrite: true);
            try { File.Delete(tmp); } catch { /* */ }

            StartApplyWatcher(GetInstalledDllPath(), pending, log);

            return new CheckResult
            {
                Ok = true,
                HasUpdate = true,
                Downloaded = true,
                RemoteTag = release.TagName,
                RemoteVersion = remoteVer,
                AssetUrl = asset.BrowserDownloadUrl,
                Message =
                    $"已下载 {release.TagName} → `{Path.GetFileName(pending)}`\n" +
                    $"通道：`{dlVia}`\n" +
                    "请**完全退出并重新打开 GBot**，退出后会自动替换 DLL。",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CheckResult { Ok = false, Message = "更新检查失败：" + ex.Message };
        }
    }

    /// <summary>代理优先，最后直连。</summary>
    private static IEnumerable<string> EnumerateUrls(string originalUrl, GlobalConfig cfg)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in GetProxyPrefixes(cfg))
        {
            var u = JoinProxy(raw, originalUrl);
            if (seen.Add(u)) yield return u;
        }
        if (seen.Add(originalUrl))
            yield return originalUrl;
    }

    private static IEnumerable<string> GetProxyPrefixes(GlobalConfig cfg)
    {
        if (cfg.UpdateProxies is { Count: > 0 })
        {
            foreach (var p in cfg.UpdateProxies)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    yield return p.Trim();
            }
            yield break;
        }
        foreach (var p in DefaultProxies)
            yield return p;
    }

    private static string JoinProxy(string prefix, string url)
    {
        prefix = prefix.Trim();
        if (!prefix.EndsWith('/')) prefix += "/";
        // 已是完整代理 URL 时避免重复拼接
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return url;
        return prefix + url;
    }

    private static async Task<(string? Body, string Via)> GetStringWithProxiesAsync(
        string originalUrl, GlobalConfig cfg, CancellationToken ct, Action<string>? log)
    {
        var errors = new List<string>();
        foreach (var url in EnumerateUrls(originalUrl, cfg))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(cfg.GithubToken)
                    && url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.GithubToken.Trim());

                using var resp = await Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"{ShortVia(url)} → {(int)resp.StatusCode}");
                    log?.Invoke($"更新通道失败 {ShortVia(url)}: HTTP {(int)resp.StatusCode}");
                    continue;
                }
                return (body, ShortVia(url));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{ShortVia(url)} → {ex.Message}");
                log?.Invoke($"更新通道失败 {ShortVia(url)}: {ex.Message}");
            }
        }
        log?.Invoke("全部通道失败：" + string.Join("; ", errors));
        return (null, "");
    }

    private static async Task<(bool Ok, string Via, string? Error)> DownloadWithProxiesAsync(
        string originalUrl, string savePath, GlobalConfig cfg, CancellationToken ct, Action<string>? log)
    {
        var errors = new List<string>();
        foreach (var url in EnumerateUrls(originalUrl, cfg))
        {
            try
            {
                log?.Invoke($"尝试下载：{ShortVia(url)}");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // 资源下载走代理时一般不带 github token；直连 githubusercontent 也不需要
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"{ShortVia(url)} → HTTP {(int)resp.StatusCode}");
                    continue;
                }
                await using (var fs = File.Create(savePath))
                    await resp.Content.CopyToAsync(fs, ct);
                return (true, ShortVia(url), null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{ShortVia(url)} → {ex.Message}");
                try { if (File.Exists(savePath)) File.Delete(savePath); } catch { /* */ }
            }
        }
        return (false, "", string.Join("; ", errors));
    }

    private static string ShortVia(string url)
    {
        if (url.StartsWith("https://gh-proxy.com/", StringComparison.OrdinalIgnoreCase))
            return "gh-proxy.com";
        if (url.StartsWith("https://ghproxy.net/", StringComparison.OrdinalIgnoreCase))
            return "ghproxy.net";
        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return "github.com(直连)";
        try { return new Uri(url).Host; }
        catch { return url.Length <= 40 ? url : url[..40] + "…"; }
    }

    /// <summary>若已有 .new 文件，启动后台替换进程。</summary>
    public static void TryResumePendingApply(Action<string>? log = null)
    {
        var pending = GetPendingDllPath();
        if (!File.Exists(pending)) return;
        StartApplyWatcher(GetInstalledDllPath(), pending, log);
        log?.Invoke($"发现待应用更新：{pending}");
    }

    public static void StartApplyWatcher(string targetDll, string pendingDll, Action<string>? log = null)
    {
        lock (ApplyGate)
        {
            if (!File.Exists(pendingDll)) return;
            try
            {
                if (_applyProcess is { HasExited: false })
                {
                    log?.Invoke("更新替换进程已在运行");
                    return;
                }
            }
            catch { /* */ }

            // 等目标文件可写（GBot 退出后）再覆盖，最多等 2 小时
            var ps = $$"""
$ErrorActionPreference='Continue'
$src={{PsQuote(pendingDll)}}
$dst={{PsQuote(targetDll)}}
$deadline=(Get-Date).AddHours(2)
while((Get-Date) -lt $deadline){
  if(-not (Test-Path -LiteralPath $src)){ exit 0 }
  try{
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Remove-Item -LiteralPath $src -Force
    exit 0
  } catch {
    Start-Sleep -Seconds 2
  }
}
exit 1
""";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + PsQuote(ps),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            _applyProcess = Process.Start(psi);
            log?.Invoke("已启动 DLL 替换监视（等 GBot 退出后覆盖）");
        }
    }

    private static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
        var s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        // 去掉前缀如 paybot-
        var m = Regex.Match(s, @"\d+(\.\d+){1,3}");
        return m.Success ? m.Value : s;
    }

    private static bool IsNewer(string remote, string local)
    {
        if (!Version.TryParse(Pad(remote), out var rv)) return false;
        if (!Version.TryParse(Pad(local), out var lv)) return true;
        return rv > lv;
    }

    private static string Pad(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Take(4));
    }

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string Trunc(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
