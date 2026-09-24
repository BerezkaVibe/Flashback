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
    }
}
