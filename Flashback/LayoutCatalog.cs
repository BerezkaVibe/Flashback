using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Flashback;

// A window layout. "Flashback (Default)" is the layout the app has always had and carries no settings; the
// console layouts are the command-window look: a frameless window with its pages as tabs in the titlebar,
// a monospaced font and square controls. A custom layout is a small .json file that sets a console layout's
// size, font, spacing, tabs and colors (see LayoutParser). It holds no code, only values, and every value is
// checked and kept within sensible limits.
internal sealed record LayoutDefinition(
    string Name, string Author, bool Console, double Width, double Height, double MinWidth, double MinHeight,
    string FontFamily, double FontSize, double Margin, double LogHeight, string[] Tabs,
    string Palette, string Accent, IReadOnlyDictionary<string, string> Colors, string? Path)
{
    internal bool BuiltIn => Path == null;
    internal bool HasColors => Colors.Count > 0;
}

internal static class LayoutCatalog
{
    internal const string DefaultName = "Flashback (Default)";
    internal const string CompactName = "Console (Compact)";
    internal const string SpaciousName = "Console (Spacious)";
    internal static readonly string[] AllTabs = { "status", "clips", "settings", "editor" };

    internal static LayoutDefinition Default { get; } = new(DefaultName, "Flashback", false, 960, 760, 760, 640, "Segoe UI", 14, 0, 0, AllTabs, "", "", new Dictionary<string, string>(), null);
    internal static LayoutDefinition Compact { get; } = new(CompactName, "Flashback", true, 480, 530, 440, 480, "Consolas", 11, 6, 65, AllTabs, "Monochrome", "White", new Dictionary<string, string>(), null);
    internal static LayoutDefinition Spacious { get; } = new(SpaciousName, "Flashback", true, 580, 640, 500, 560, "Consolas", 12, 10, 120, AllTabs, "Monochrome", "White", new Dictionary<string, string>(), null);
    internal static LayoutDefinition[] BuiltIns => new[] { Default, Compact, Spacious };

    internal static string Folder => System.IO.Path.Combine(Storage.Root, "Layouts");

    // A saved layout name that is usable; anything else (empty, mangled, an old value) means the default.
    internal static string Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) ? DefaultName : name.Trim();

    // The built-in layouts, then the layout files in the Layouts folder that read cleanly (by name). Files that
    // don't are listed in problems with what is wrong, so a person making one can see why it isn't in the list.
    internal static List<LayoutDefinition> Load(List<string>? problems = null)
    {
        var list = BuiltIns.ToList();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(100))
            {
                var result = LayoutParser.ReadFile(file);
                if (result.Layout == null) { problems?.Add($"{System.IO.Path.GetFileName(file)}: {result.Error}"); continue; }
                if (list.Any(l => string.Equals(l.Name, result.Layout.Name, StringComparison.OrdinalIgnoreCase))) { problems?.Add($"{System.IO.Path.GetFileName(file)}: another layout is already called \"{result.Layout.Name}\"."); continue; }
                list.Add(result.Layout);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    internal static LayoutDefinition Find(string? name, List<LayoutDefinition>? known = null) =>
        (known ?? Load()).FirstOrDefault(l => string.Equals(l.Name, Clean(name), StringComparison.OrdinalIgnoreCase)) ?? Default;

    // Keeps a validated layout as a file in the Layouts folder, named for the layout, and returns its path.
    internal static string Save(LayoutDefinition layout, string json)
    {
        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, Storage.SafeName(layout.Name) + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    // The JSON for a layout, written out as the file format reads it, to start a custom layout from.
    internal static string ToJson(LayoutDefinition layout, string? name = null)
    {
        var theme = new Dictionary<string, object> { ["palette"] = layout.Palette, ["accent"] = layout.Accent };
        if (layout.HasColors) theme["colors"] = layout.Colors;
        var doc = new Dictionary<string, object>
        {
            ["name"] = name ?? layout.Name, ["author"] = layout.Author, ["baseStyle"] = "console",
            ["window"] = new Dictionary<string, object> { ["width"] = layout.Width, ["height"] = layout.Height, ["minWidth"] = layout.MinWidth, ["minHeight"] = layout.MinHeight },
            ["typography"] = new Dictionary<string, object> { ["fontFamily"] = layout.FontFamily, ["fontSize"] = layout.FontSize },
            ["spacing"] = new Dictionary<string, object> { ["margin"] = layout.Margin, ["logHeight"] = layout.LogHeight },
            ["navigation"] = new Dictionary<string, object> { ["tabs"] = layout.Tabs },
            ["theme"] = theme
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}

internal sealed record LayoutResult(LayoutDefinition? Layout, string? Error, List<string> Notes);

internal static class LayoutParser
{
    internal const int MaxBytes = 32 * 1024;
    internal static readonly string[] ColorKeys = { "background", "surface", "control", "outline", "accent", "text", "muted", "dim" };

    internal static LayoutResult ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return Fail($"the file is larger than {MaxBytes / 1024} KB.");
            return Parse(File.ReadAllText(path, Encoding.UTF8), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail("the file could not be read (" + ex.Message + ")."); }
    }

    private static LayoutResult Fail(string error) => new(null, error, new());

    // Reads a layout from JSON text. Nothing in it is run: each value is read, checked and clamped, and a value
    // that is wrong gives an error naming it. Unknown keys are ignored, so layouts can carry notes.
    internal static LayoutResult Parse(string json, string? path = null)
    {
        var notes = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 }); }
        catch (JsonException ex) { return Fail("this isn't valid JSON (" + ex.Message.Split('.')[0] + ")."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("the file should hold one JSON object.");

            string? Text(JsonElement parent, string key, int max)
            {
                if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
                var s = v.GetString()?.Trim();
                return string.IsNullOrEmpty(s) ? null : s!.Length > max ? s[..max] : s;
            }
            double? Number(JsonElement parent, string key)
            {
                if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out var v)) return null;
                return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : double.NaN;
            }
            JsonElement Section(string key) => root.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

            string? name = Text(root, "name", 40);
            if (name == null) return Fail("\"name\" is missing.");
            if (name.Any(char.IsControl)) return Fail("\"name\" has characters that can't be used.");
            if (LayoutCatalog.BuiltIns.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))) return Fail($"\"{name}\" is the name of a built-in layout; choose another.");
            string author = Text(root, "author", 40) ?? "";
            string style = (Text(root, "baseStyle", 20) ?? "console").ToLowerInvariant();
            if (style is not ("console" or "clipper")) return Fail("\"baseStyle\" should be \"console\": custom layouts adjust the console look.");

            var b = LayoutCatalog.Compact;
            var window = Section("window"); var type = Section("typography"); var space = Section("spacing"); var nav = Section("navigation"); var theme = Section("theme");

            // A number that is there must be a number, and is held within [min, max].
            string? bad = null;
            double Pick(JsonElement parent, string key, double fallback, double min, double max, string label)
            {
                var n = Number(parent, key);
                if (n == null) return fallback;
                if (double.IsNaN(n.Value)) { bad ??= $"\"{label}\" should be a number."; return fallback; }
                if (n < min || n > max) notes.Add($"{label} was kept between {min:0} and {max:0}.");
                return Math.Clamp(Math.Round(n.Value, 1), min, max);
            }
            double width = Pick(window, "width", b.Width, 380, 1400, "width"), height = Pick(window, "height", b.Height, 380, 1100, "height");
            double minWidth = Pick(window, "minWidth", Math.Min(b.MinWidth, width), 320, 1400, "minWidth"), minHeight = Pick(window, "minHeight", Math.Min(b.MinHeight, height), 320, 1100, "minHeight");
            double fontSize = Pick(type, "fontSize", b.FontSize, 9, 18, "fontSize");
            double margin = Pick(space, "margin", b.Margin, 0, 32, "margin"), logHeight = Pick(space, "logHeight", b.LogHeight, 40, 400, "logHeight");
            if (bad != null) return Fail(bad);
            if (window.ValueKind == JsonValueKind.Object && window.TryGetProperty("frameless", out var frameless) && frameless.ValueKind == JsonValueKind.False)
                notes.Add("\"frameless\": false isn't supported; console layouts always draw their own titlebar.");
            minWidth = Math.Min(minWidth, width); minHeight = Math.Min(minHeight, height);

            string font = Text(type, "fontFamily", 60) ?? b.FontFamily;
            if (font.Any(char.IsControl) || font.Contains(',') || font.Contains('{') || font.Contains('/') || font.Contains('\\')) return Fail("\"fontFamily\" should be the name of one installed font.");
            if (!Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, font, StringComparison.OrdinalIgnoreCase))) { notes.Add($"The font \"{font}\" isn't installed here; Consolas is used instead."); font = b.FontFamily; }

            var tabs = b.Tabs;
            if (nav.ValueKind == JsonValueKind.Object && nav.TryGetProperty("tabs", out var tabList))
            {
                if (tabList.ValueKind != JsonValueKind.Array) return Fail("\"tabs\" should be a list like [\"status\", \"clips\", \"settings\", \"editor\"].");
                var picked = new List<string>();
                foreach (var t in tabList.EnumerateArray())
                {
                    var s = t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim().ToLowerInvariant() : null;
                    if (s == null || !LayoutCatalog.AllTabs.Contains(s)) return Fail($"\"tabs\" can hold only: {string.Join(", ", LayoutCatalog.AllTabs)}.");
                    if (!picked.Contains(s)) picked.Add(s);
                }
                if (!picked.Contains("settings")) return Fail("\"tabs\" must include \"settings\", or the layout couldn't be changed back.");
                tabs = picked.ToArray();
            }

            string palette = Text(theme, "palette", 20) ?? b.Palette, accent = Text(theme, "accent", 20) ?? b.Accent;
            palette = Appearance.ConsolePalettes.FirstOrDefault(p => string.Equals(p, palette, StringComparison.OrdinalIgnoreCase)) ?? "";
            accent = Appearance.ConsoleAccents.FirstOrDefault(a => string.Equals(a, accent, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (palette == "") return Fail($"\"palette\" should be one of: {string.Join(", ", Appearance.ConsolePalettes)}.");
            if (accent == "") return Fail($"\"accent\" should be one of: {string.Join(", ", Appearance.ConsoleAccents)}.");

            var colors = new Dictionary<string, string>();
            if (theme.ValueKind == JsonValueKind.Object && theme.TryGetProperty("colors", out var colorList))
            {
                if (colorList.ValueKind != JsonValueKind.Object) return Fail("\"colors\" should list names and values like \"background\": \"#000000\".");
                foreach (var p in colorList.EnumerateObject())
                {
                    string key = p.Name.ToLowerInvariant();
                    if (!ColorKeys.Contains(key)) { notes.Add($"The color \"{p.Name}\" isn't one this app uses and was ignored."); continue; }
                    var hex = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    if (!IsHexColor(hex)) return Fail($"the color \"{p.Name}\" should be like \"#1A2B3C\".");
                    colors[key] = hex!.ToUpperInvariant();
                }
            }
            var layout = new LayoutDefinition(name, author, true, width, height, minWidth, minHeight, font, fontSize, margin, logHeight, tabs, palette, accent, colors, path);
            return new(layout, null, notes);
        }
    }

    internal static bool IsHexColor(string? hex)
    {
        if (hex == null || (hex.Length != 7 && hex.Length != 9) || hex[0] != '#') return false;
        return hex.Skip(1).All(Uri.IsHexDigit);
    }
}

// Fetching a layout file from a link. Only GitHub's own hosts are used, over https, and a file larger than the
// limit, or one that is slow, is dropped. The text is then read by LayoutParser like any other file.
internal static class LayoutDownloader
{
    private static readonly string[] Hosts = { "raw.githubusercontent.com", "gist.githubusercontent.com", "github.com" };

    // A github.com link to a file page becomes its raw link; links that are already raw are kept. Null when the
    // link isn't one this will fetch.
    internal static Uri? Resolve(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (!Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // /owner/repo/blob/ref/path... → raw.githubusercontent.com/owner/repo/ref/path...
            if (parts.Length >= 5 && parts[2] == "blob") return new Uri("https://raw.githubusercontent.com/" + string.Join('/', new[] { parts[0], parts[1] }.Concat(parts.Skip(3))));
            if (parts.Length >= 5 && parts[2] == "raw") return new Uri("https://github.com/" + string.Join('/', parts));
            return null;
        }
        return uri;
    }

    internal static async Task<string> FetchAsync(Uri uri, CancellationToken token = default)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flashback-layout-import");
        for (int hop = 0; hop < 4; hop++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                var target = next.IsAbsoluteUri ? next : new Uri(uri, next);
                if (Resolve(target.ToString()) is not { } allowed) throw new IOException("The link redirected somewhere that isn't allowed.");
                uri = allowed; continue;
            }
            if (!response.IsSuccessStatusCode) throw new IOException($"The link answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > LayoutParser.MaxBytes) throw new IOException($"The file is larger than {LayoutParser.MaxBytes / 1024} KB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[LayoutParser.MaxBytes + 1]; int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false)) > 0) total += read;
            if (total > LayoutParser.MaxBytes) throw new IOException($"The file is larger than {LayoutParser.MaxBytes / 1024} KB.");
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        throw new IOException("The link redirected too many times.");
    }
}
