using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flashback;

// --update-check-test: asks the real GitHub release feed as an older and a newer version,
// and renders the header with the update icon showing.
internal static class UpdateDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "update-results.txt"), "PASS " + message + "\n");
        }
        Check(Updater.VersionIn("patch") == null && Updater.VersionIn("v0.7.4") == new Version(0, 7, 4) && Updater.VersionIn("Flashback v0.7.4") == new Version(0, 7, 4)
            && Updater.VersionIn("Flashback-0.7.12-Setup.exe") == new Version(0, 7, 12), "Versions are read from tags, titles and installer names, and a word like \"patch\" is ignored");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var older = await Updater.CheckAsync(timeout.Token, new Version(0, 1, 0));
        Check(older != null && older.DownloadUrl.StartsWith("https://github.com/" + Updater.Repository + "/releases/download/") && older.DownloadUrl.EndsWith("-Setup.exe") && older.Size > 1_000_000,
            $"An older version is offered the latest release ({older?.Version}, {older?.Size / 1048576.0:0} MB)");
        Check(await Updater.CheckAsync(timeout.Token, new Version(99, 0, 0)) == null, "A newer version is not offered anything");
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var window = new MainWindow(true);
        window.UpdateButton.Visibility = Visibility.Visible;
        var content = (FrameworkElement)window.Content; window.Content = null;
        var canvas = new Border { Child = content, Background = (Brush)new BrushConverter().ConvertFromString("#111419")! };
        EditorDiagnostics.Render(canvas, 1100, 160, "update-header.png");
        Check(window.UpdateButton.ActualWidth > 0, "The update icon shows in the header");

        // Bug reports: a zip with the PC's details, settings and messages, and an issue link that fits a URL.
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        string? clip = null;
        if (File.Exists(ffmpeg))
        {
            clip = Path.Combine(Storage.Root, "report-clip.mp4");
            var make = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i testsrc2=s=320x180:r=30:d=2 -f lavfi -i sine=d=2 -c:v mpeg4 -c:a aac -shortest \"{clip}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            await make.WaitForExitAsync();
        }
        var zip = await BugReport.CreateAsync(new[] { "12:00:00 ERROR Something broke" }, clip, ffmpeg, Storage.Root);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            string Entry(string name) { var e = archive.GetEntry(name); if (e == null) return ""; using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }
            Check(Entry("system.txt").Contains("Windows") && Entry("system.txt").Contains("Graphics:") && Entry("settings.json").Contains("OutputFolder") && Entry("messages.txt").Contains("Something broke"),
                "A bug report holds the PC's details, the settings and recent messages");
            Check(clip == null || Entry("newest-clip.txt").Contains("First picture at") && !Entry("newest-clip.txt").Contains(Storage.Root),
                "A bug report describes the newest clip without its folder");
        }
        var link = BugReport.IssueLink(Path.GetFileName(zip));
        Check(link.StartsWith(BugReport.IssueUrl) && link.Length < 8000, $"The issue link is filled in and short enough for a browser ({link.Length} characters)");
    }
}
