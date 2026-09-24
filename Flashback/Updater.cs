using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal sealed record UpdateInfo(Version Version, string DownloadUrl, long Size, string? Sha256, string Notes);

// Checks GitHub Releases for a newer installer. Only this repository's own release
// downloads are accepted, and the file is checked against GitHub's size and digest.
internal static class Updater
{
    internal const string Repository = "BerezkaVibe/Flashback";
    internal static Version Current { get; } = Trim(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    // Declared after Current: static fields initialize in order, and the User-Agent needs the version.
    private static readonly HttpClient http = CreateClient();
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flashback/" + Current);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    // Returns null when this version is current. Throws when GitHub cannot be reached
    // or the repository is private (its releases are then not visible).
    internal static Task<UpdateInfo?> CheckAsync(CancellationToken token) => CheckAsync(token, Current);
    // The version to compare against can be supplied for tests.
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken token, Version current)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("No public release was found on GitHub.");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var latest)) return null;
        latest = Trim(latest);
        if (latest <= current) return null;
        string prefix = $"https://github.com/{Repository}/releases/download/";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "", url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) || !url.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string? digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : null;
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(latest, url, asset.GetProperty("size").GetInt64(), digest, notes);
        }
        throw new InvalidOperationException($"Flashback {latest} is on GitHub, but its release has no Setup.exe attached yet.");
    }
    internal static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken token)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Flashback-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Flashback-{update.Version}-Setup.exe");
        var partial = path + ".partial";
        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                total += read; progress.Report(update.Size > 0 ? (double)total / update.Size : 0);
            }
        }
        if (new FileInfo(partial).Length != update.Size) { File.Delete(partial); throw new IOException("The update download was incomplete. Try again."); }
        if (update.Sha256 != null)
        {
            string actual;
            await using (var file = File.OpenRead(partial)) actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(partial); throw new IOException("The update failed its integrity check and was deleted. Try again later."); }
        }
        File.Move(partial, path, true);
        return path;
    }
    // A short, plain-text excerpt of the release notes for the confirmation dialog.
    internal static string Summary(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ')).Where(l => l.Length > 0).Take(4).ToArray();
        return lines.Length == 0 ? "" : string.Join("\n", lines.Select(l => "• " + (l.Length > 110 ? l[..110] + "…" : l)));
    }
}
