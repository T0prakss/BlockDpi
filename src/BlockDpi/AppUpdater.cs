using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BlockDpi;

public sealed record UpdateReleaseInfo(string VersionText, string DownloadUrl, bool IsZip);

public static class AppUpdater
{
    public const string CurrentVersion = "1.1.0";
    private const string LatestReleaseApi = "https://api.github.com/repos/T0prakss/BlockDpi/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlockDpi", CurrentVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static void CleanupOldFiles()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string? dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.old*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public static async Task<UpdateReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.GetAsync(LatestReleaseApi, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagProp)) return null;
            string tag = tagProp.GetString() ?? "";
            string cleanRemote = tag.TrimStart('v', 'V').Split('-')[0];
            if (!Version.TryParse(cleanRemote, out var remoteVer)) return null;
            if (!Version.TryParse(CurrentVersion, out var localVer)) return null;
            if (remoteVer <= localVer) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            string? url = null;
            bool isZip = false;

            if (OperatingSystem.IsWindows())
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.Equals("BlockDpi.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = dl;
                        isZip = false;
                        break;
                    }
                    if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        url = dl;
                        isZip = true;
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (arm && (name.Contains("AppleSilicon", StringComparison.OrdinalIgnoreCase) || name.Contains("arm64", StringComparison.OrdinalIgnoreCase)))
                    {
                        url = dl;
                        isZip = true;
                        break;
                    }
                    if (!arm && (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("osx-x64", StringComparison.OrdinalIgnoreCase)))
                    {
                        url = dl;
                        isZip = true;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(url)) return null;
            return new UpdateReleaseInfo(tag.StartsWith('v') ? tag : "v" + tag, url, isZip);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> DownloadAndApplyAsync(UpdateReleaseInfo info, Action? onBeforeExit, CancellationToken ct = default)
    {
        try
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return false;

            if (OperatingSystem.IsWindows())
            {
                string dir = Path.GetDirectoryName(currentExe)!;
                string tmpDownload = Path.Combine(dir, $"BlockDpi.download.{Environment.TickCount}.tmp");
                using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!res.IsSuccessStatusCode) return false;
                    await using var fs = File.Create(tmpDownload);
                    await res.Content.CopyToAsync(fs, ct);
                }

                string newExePath = tmpDownload;
                if (info.IsZip)
                {
                    string extractDir = Path.Combine(dir, $"_upd_{Environment.TickCount}");
                    ZipFile.ExtractToDirectory(tmpDownload, extractDir, overwriteFiles: true);
                    File.Delete(tmpDownload);
                    string extractedExe = Path.Combine(extractDir, "BlockDpi.exe");
                    if (!File.Exists(extractedExe))
                    {
                        Directory.Delete(extractDir, true);
                        return false;
                    }
                    newExePath = Path.Combine(dir, $"BlockDpi.new.{Environment.TickCount}.exe");
                    File.Move(extractedExe, newExePath, overwrite: true);
                    try { Directory.Delete(extractDir, true); } catch { }
                }

                string oldExePath = Path.Combine(dir, $"BlockDpi.old.{Environment.TickCount}.exe");
                File.Move(currentExe, oldExePath, overwrite: true);
                File.Move(newExePath, currentExe, overwrite: true);

                onBeforeExit?.Invoke();
                Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = true, WorkingDirectory = dir });
                Environment.Exit(0);
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                string tmpZip = "/tmp/blockdpi-update.zip";
                string tmpExtract = "/tmp/blockdpi-update-dir";
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, true);

                using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!res.IsSuccessStatusCode) return false;
                    await using var fs = File.Create(tmpZip);
                    await res.Content.CopyToAsync(fs, ct);
                }

                ZipFile.ExtractToDirectory(tmpZip, tmpExtract, overwriteFiles: true);
                string newApp = Path.Combine(tmpExtract, "BlockDpi.app");
                if (!Directory.Exists(newApp)) return false;

                string targetApp = "/Applications/BlockDpi.app";
                int idx = currentExe.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
                if (idx > 0) targetApp = currentExe[..(idx + 4)];

                onBeforeExit?.Invoke();
                string sh = $"sleep 1 && rm -rf '{targetApp}' && cp -R '{newApp}' '{targetApp}' && xattr -cr '{targetApp}' && chmod +x '{targetApp}/Contents/MacOS/BlockDpi' && open '{targetApp}'";
                Process.Start(new ProcessStartInfo("/bin/sh", ["-c", sh]) { CreateNoWindow = true, UseShellExecute = false });
                Environment.Exit(0);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
