using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TimecodeBridge;

/// <summary>
/// Checks GitHub Releases for a newer version and swaps the running single-file exe
/// in place (rename running exe to .old, move new one in, restart).
/// </summary>
public static class UpdateService
{
    public const string Owner = "DavidPujals";
    public const string Repo = "timecode-bridge";
    public const string RepoUrl = $"https://github.com/{Owner}/{Repo}";
    private const string AssetName = "TimecodeBridge.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Timecode-Bridge");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        // Covers the API call and download headers only — the download body is
        // streamed after GetAsync returns, so big downloads aren't cut off.
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Major.Minor.Build with unset parts as 0, so "1.1" == "1.1.0" == assembly 1.1.0.0.</summary>
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public record UpdateInfo(Version Version, string DownloadUrl);

    /// <summary>Returns the newer release, or null if this is the latest (or no release exists yet).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Normalize(latest);
        if (latest <= CurrentVersion) return null;

        string? url = null, anyExe = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var dl = a.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) url = dl;
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) anyExe ??= dl;
        }
        url ??= anyExe;
        return url is null ? null : new UpdateInfo(latest, url);
    }

    /// <summary>
    /// Downloads the new exe next to the current one and swaps it in. A running exe
    /// can be renamed but not overwritten, so: current → .old, download → current.
    /// The app keeps running from the old image until restarted.
    /// </summary>
    public static async Task DownloadAndInstallAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Can't locate the running exe.");
        var tmp = Path.Combine(Path.GetDirectoryName(exePath)!, "update.tmp");

        using (var resp = await Http.GetAsync(info.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // Sanity: a real self-contained build is tens of MB; an error page is not.
        if (new FileInfo(tmp).Length < 1_000_000)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Downloaded file doesn't look like the app — update aborted.");
        }

        var old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try { File.Move(tmp, exePath); }
        catch
        {
            File.Move(old, exePath); // roll back so the app still launches next time
            throw;
        }
    }

    /// <summary>Removes leftovers from a previous update. Call once at startup.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null) return;
            var old = exePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            var tmp = Path.Combine(Path.GetDirectoryName(exePath)!, "update.tmp");
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch
        {
            // The previous exe may still be shutting down — it'll be cleaned next launch.
        }
    }
}
