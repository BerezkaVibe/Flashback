using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Flashback;

public partial class MainWindow
{
    // The app's recent messages (errors marked), for bug reports.
    private readonly Queue<string> messageLog = new();
    private void LogMessage(string message, bool error)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        messageLog.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(error ? "ERROR " : "")}{message}");
        while (messageLog.Count > 80) messageLog.Dequeue();
    }
    private bool reporting;
    private async void BugReport_Click(object sender, RoutedEventArgs e)
    {
        if (reporting) return;
        if (!ThemedDialog.Confirm(this, "Report a bug",
            "Flashback saves a zip to your desktop with your PC's details (Windows, processor, graphics card), your settings, its recent messages and logs, and the newest clip's details. No clips, pictures or sound are included.\n\n"
            + "Then a GitHub page opens with the details filled in: say what happened, drag the zip in and submit. It needs a free GitHub account.",
            "Make the report", "Cancel")) return;
        reporting = true; BugReportButton.IsEnabled = false;
        try
        {
            string folder = settings.OutputFolder;
            string? newest = lastClip ?? await Task.Run(() => NewestClip(folder));
            var zip = await BugReport.CreateAsync(messageLog.ToArray(), newest, recorder.FfmpegPath);
            // Show the zip, then open the issue page beside it.
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", zip } });
            Process.Start(new ProcessStartInfo(BugReport.IssueLink(Path.GetFileName(zip))) { UseShellExecute = true });
            Tell($"Bug report saved to your desktop as {Path.GetFileName(zip)}. Drag it into the GitHub page to attach it.");
        }
        catch (Exception ex) { Tell("Couldn't make the bug report. " + ex.Message, true); }
        finally { reporting = false; BugReportButton.IsEnabled = true; }
    }
    private static string? NewestClip(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            return new DirectoryInfo(folder).EnumerateFiles("*.mp4", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }
}
