using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WF = System.Windows.Forms;

namespace Flashback;

// The Tester (Flashback.exe --tester): pick a copy of Flashback (one that's running, this one, the installed
// one, or any Flashback.exe), tick the tests to run, and run them one after another. Each runs as that copy
// with the test's switch, in a data folder of its own under %LOCALAPPDATA%\Flashback-tests, so settings and
// clips aren't touched. Results show pass or fail, the time taken, what failed and the reports each wrote,
// and copy out in one go.
internal sealed class TesterWindow : WF.Form
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flashback-tests");
    private readonly WF.ComboBox target = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Dock = WF.DockStyle.Fill };
    private readonly WF.CheckedListBox list = new() { Dock = WF.DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly WF.Label about = new() { Dock = WF.DockStyle.Fill, AutoEllipsis = true };
    private readonly WF.TextBox clip = new() { Dock = WF.DockStyle.Fill, PlaceholderText = "Clip for tests that use one (optional)" };
    private readonly WF.Button run = new() { Text = "Run selected", AutoSize = true }, stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly WF.ListView results = new() { Dock = WF.DockStyle.Fill, View = WF.View.Details, FullRowSelect = true, HideSelection = false };
    private readonly WF.TextBox details = new() { Dock = WF.DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = WF.ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly WF.Label status = new() { Dock = WF.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private IReadOnlyList<TestInfo> tests = TestCatalog.All;
    private readonly List<Result> done = new();
    private Process? current;
    private CancellationTokenSource? cancel;

    private sealed record Target(string Label, string Path) { public override string ToString() => Label; }
    private sealed record Result(TestInfo Test, string Outcome, TimeSpan Time, string Failure, List<(string Name, string Text)> Reports, string Folder);

    internal TesterWindow()
    {
        AutoScaleMode = WF.AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Text = "Flashback Tester"; Width = 1000; Height = 760; StartPosition = WF.FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        var layout = new WF.TableLayoutPanel { Dock = WF.DockStyle.Fill, ColumnCount = 1, Padding = new WF.Padding(10) };
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.AutoSize));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.Percent, 45));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 44));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.AutoSize));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.AutoSize));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.Percent, 55));
        layout.RowStyles.Add(new WF.RowStyle(WF.SizeType.AutoSize));
        Controls.Add(layout);

        // Which Flashback
        var top = Row(new[] { 150f, -1f, 90f, 90f });
        top.Controls.Add(new WF.Label { Text = "Flashback to test:", Dock = WF.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        top.Controls.Add(target, 1, 0);
        var refresh = new WF.Button { Text = "Refresh", Dock = WF.DockStyle.Fill }; refresh.Click += (_, _) => FindTargets();
        var browse = new WF.Button { Text = "Browse…", Dock = WF.DockStyle.Fill }; browse.Click += (_, _) => BrowseTarget();
        top.Controls.Add(refresh, 2, 0); top.Controls.Add(browse, 3, 0);
        layout.Controls.Add(top);

        // Tests
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < tests.Count) about.Text = Describe(tests[list.SelectedIndex]); };
        layout.Controls.Add(list);
        layout.Controls.Add(about);

        var picks = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Fill, AutoSize = true, WrapContents = false };
        var player = new WF.Button { Text = "FFmpeg player tests", AutoSize = true }; player.Click += (_, _) => Check(t => t.Group == "FFmpeg player");
        var quick = new WF.Button { Text = "Everything but long ones", AutoSize = true }; quick.Click += (_, _) => Check(t => !t.Long && !t.NeedsClip);
        var none = new WF.Button { Text = "Clear", AutoSize = true }; none.Click += (_, _) => Check(_ => false);
        picks.Controls.AddRange(new WF.Control[] { player, quick, none });
        layout.Controls.Add(picks);

        var clipRow = Row(new[] { -1f, 90f, 120f, 70f });
        clipRow.Controls.Add(clip, 0, 0);
        var clipBrowse = new WF.Button { Text = "Clip…", Dock = WF.DockStyle.Fill };
        clipBrowse.Click += (_, _) => { using var d = new WF.OpenFileDialog { Filter = "Videos|*.mp4;*.mov;*.mkv|All files|*.*" }; if (d.ShowDialog(this) == WF.DialogResult.OK) clip.Text = d.FileName; };
        clipRow.Controls.Add(clipBrowse, 1, 0);
        run.Dock = WF.DockStyle.Fill; stop.Dock = WF.DockStyle.Fill;
        run.Click += async (_, _) => await RunSelected();
        stop.Click += (_, _) => cancel?.Cancel();
        clipRow.Controls.Add(run, 2, 0); clipRow.Controls.Add(stop, 3, 0);
        layout.Controls.Add(clipRow);

        // Results
        results.Columns.Add("Test", 330); results.Columns.Add("Result", 110); results.Columns.Add("Time", 80); results.Columns.Add("Reports", 400);
        results.SelectedIndexChanged += (_, _) => { if (results.SelectedIndices.Count > 0) details.Text = ReportText(done[results.SelectedIndices[0]]).Replace("\n", "\r\n"); };
        var split = new WF.SplitContainer { Dock = WF.DockStyle.Fill, Orientation = WF.Orientation.Horizontal };
        Shown += (_, _) => { try { split.SplitterDistance = split.Height * 2 / 5; } catch { } };
        split.Panel1.Controls.Add(results); split.Panel2.Controls.Add(details);
        layout.Controls.Add(split);

        var bottom = Row(new[] { -1f, 150f, 150f });
        bottom.Controls.Add(status, 0, 0);
        var copy = new WF.Button { Text = "Copy all results", Dock = WF.DockStyle.Fill };
        copy.Click += (_, _) => { if (done.Count > 0) { WF.Clipboard.SetText(AllText()); status.Text = "Copied. Paste it to Claude."; } };
        var open = new WF.Button { Text = "Open results folder", Dock = WF.DockStyle.Fill };
        open.Click += (_, _) => { Directory.CreateDirectory(Root); Process.Start(new ProcessStartInfo("explorer.exe", Root) { UseShellExecute = true }); };
        bottom.Controls.Add(copy, 1, 0); bottom.Controls.Add(open, 2, 0);
        layout.Controls.Add(bottom);

        target.SelectedIndexChanged += async (_, _) => await LoadTests();
        Shown += (_, _) => FindTargets();
        FormClosing += (_, _) => { cancel?.Cancel(); Kill(current); };
    }
    private static WF.TableLayoutPanel Row(float[] widths)
    {
        var row = new WF.TableLayoutPanel { Dock = WF.DockStyle.Fill, ColumnCount = widths.Length, RowCount = 1, AutoSize = true, Margin = new WF.Padding(0, 4, 0, 4) };
        foreach (var w in widths) row.ColumnStyles.Add(w < 0 ? new WF.ColumnStyle(WF.SizeType.Percent, 100) : new WF.ColumnStyle(WF.SizeType.Absolute, w));
        return row;
    }
    private static string Describe(TestInfo t) => t.About + (t.Long ? "  [long]" : "") + (t.UsesScreen ? "  [opens windows or records: leave the mouse alone]" : "") + (t.NeedsClip ? "  [needs a clip below]" : t.ClipOptional ? "  [uses the clip below if there is one]" : "");
    private void Check(Func<TestInfo, bool> which) { for (int i = 0; i < tests.Count; i++) list.SetItemChecked(i, which(tests[i])); }

    // ---- Which Flashback ----
    private void FindTargets()
    {
        string self = Environment.ProcessPath ?? "";
        var found = new List<Target>();
        foreach (var p in Process.GetProcessesByName("Flashback"))
        {
            try { if (p.Id != Environment.ProcessId && p.MainModule?.FileName is { } path) found.Add(new Target($"Running: {Version(path)} ({path})", path)); }
            catch { }
            finally { p.Dispose(); }
        }
        found.Add(new Target($"This copy: {Version(self)} ({self})", self));
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Flashback", "Flashback.exe");
        if (File.Exists(installed)) found.Add(new Target($"Installed: {Version(installed)} ({installed})", installed));
        var distinct = found.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var was = (target.SelectedItem as Target)?.Path;
        target.Items.Clear(); foreach (var t in distinct) target.Items.Add(t);
        int keep = distinct.FindIndex(t => string.Equals(t.Path, was, StringComparison.OrdinalIgnoreCase));
        target.SelectedIndex = keep >= 0 ? keep : 0;
    }
    private static string Version(string path) { try { return "Flashback " + FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0]; } catch { return "Flashback"; } }
    private void BrowseTarget()
    {
        using var d = new WF.OpenFileDialog { Filter = "Flashback|Flashback.exe|Programs|*.exe", Title = "Choose a Flashback.exe to test" };
        if (d.ShowDialog(this) != WF.DialogResult.OK) return;
        var t = new Target($"Chosen: {Version(d.FileName)} ({d.FileName})", d.FileName);
        target.Items.Add(t); target.SelectedItem = t;
    }
    // The tests the chosen copy has (it lists them itself; an older copy can't, so this copy's list is shown).
    private async Task LoadTests()
    {
        if (target.SelectedItem is not Target t) return;
        status.Text = "Asking that copy of Flashback which tests it has…";
        string folder = Path.Combine(Root, "_list"); TryClear(folder);
        IReadOnlyList<TestInfo>? listed = null;
        try
        {
            using var p = Start(t.Path, folder, "--list-tests");
            var wait = Task.Delay(TimeSpan.FromSeconds(20));
            while (!p.HasExited && !wait.IsCompleted) await Task.Delay(100);
            if (!p.HasExited) Kill(p);
            listed = TestCatalog.Read(folder);
        }
        catch { }
        var checkedSwitches = list.CheckedItems.Count > 0 ? tests.Where((_, i) => list.GetItemChecked(i)).Select(x => x.Switch).ToHashSet() : null;
        tests = listed ?? TestCatalog.All;
        list.Items.Clear();
        foreach (var x in tests) list.Items.Add($"{x.Group}:  {x.Name}" + (x.Long ? "   (long)" : "") + (x.NeedsClip ? "   (needs a clip)" : ""), checkedSwitches?.Contains(x.Switch) ?? x.Group == "FFmpeg player");
        status.Text = listed != null ? $"{tests.Count} tests in that copy. Tick the ones to run." : "That copy of Flashback is too old to list its tests; this copy's list is shown, and tests it doesn't have will fail straight away.";
    }

    // ---- Running ----
    private static Process Start(string exe, string folder, params string[] args)
    {
        Directory.CreateDirectory(folder);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(folder);
        foreach (var a in args) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new InvalidOperationException("Couldn't start " + exe);
    }
    private static void Kill(Process? p) { try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
    private static void TryClear(string folder) { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } }

    private async Task RunSelected()
    {
        if (target.SelectedItem is not Target t) return;
        var chosen = tests.Where((_, i) => list.GetItemChecked(i)).ToList();
        if (chosen.Count == 0) { status.Text = "Tick at least one test."; return; }
        string note = chosen.Any(x => x.Group == "Recording") ? "\n\nRecording tests work best with Flashback's own recorder closed (tray icon > Exit), so the two don't record at once." : "";
        if (chosen.Any(x => x.UsesScreen) && WF.MessageBox.Show(this, "Some of these open windows or record the screen and sound. Leave the mouse and keyboard alone until they finish." + note + "\n\nStart?", "Flashback Tester", WF.MessageBoxButtons.OKCancel, WF.MessageBoxIcon.Information) != WF.DialogResult.OK) return;
        run.Enabled = false; stop.Enabled = true; target.Enabled = false;
        cancel = new CancellationTokenSource();
        done.Clear(); results.Items.Clear(); details.Clear();
        for (int n = 0; n < chosen.Count && !cancel.IsCancellationRequested; n++)
        {
            var test = chosen[n];
            status.Text = $"Running {n + 1} of {chosen.Count}: {test.Name}…";
            var item = results.Items.Add(test.Name); item.SubItems.Add("running…"); item.SubItems.Add(""); item.SubItems.Add("");
            var result = await RunOne(t.Path, test, cancel.Token);
            done.Add(result);
            item.SubItems[1].Text = result.Outcome; item.SubItems[2].Text = $"{result.Time.TotalSeconds:0} s";
            item.SubItems[3].Text = string.Join(", ", result.Reports.Select(r => r.Name));
            item.ForeColor = result.Outcome == "PASSED" ? Color.DarkGreen : result.Outcome == "FAILED" ? Color.Firebrick : Color.DimGray;
        }
        int passed = done.Count(r => r.Outcome == "PASSED"), failed = done.Count(r => r.Outcome == "FAILED");
        status.Text = $"Done: {passed} passed, {failed} failed" + (done.Count < chosen.Count ? $", {chosen.Count - done.Count} not run (stopped)" : "") + ". Click a test for details, or copy all results.";
        run.Enabled = true; stop.Enabled = false; target.Enabled = true; current = null;
        if (results.Items.Count > 0) results.Items[0].Selected = true;
    }
    private async Task<Result> RunOne(string exe, TestInfo test, CancellationToken token)
    {
        string folder = Path.Combine(Root, test.Switch.TrimStart('-'));
        TryClear(folder);
        var started = DateTime.Now; var watch = Stopwatch.StartNew();
        if (test.NeedsClip && !File.Exists(clip.Text)) return new Result(test, "SKIPPED", TimeSpan.Zero, "This test needs a clip: choose one with Clip… and run it again.", new(), folder);
        var args = new List<string> { test.Switch };
        if ((test.NeedsClip || test.ClipOptional) && File.Exists(clip.Text)) args.Add(clip.Text);
        string outcome;
        try
        {
            current = Start(exe, folder, args.ToArray());
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(test.Long ? TimeSpan.FromHours(3) : TimeSpan.FromMinutes(45));
            try { await current.WaitForExitAsync(limit.Token); outcome = current.ExitCode == 0 ? "PASSED" : "FAILED"; }
            catch (OperationCanceledException) { Kill(current); outcome = token.IsCancellationRequested ? "STOPPED" : "TIMED OUT"; }
        }
        catch (Exception ex) { return new Result(test, "COULDN'T START", watch.Elapsed, ex.Message, new(), folder); }
        finally { current?.Dispose(); current = null; }
        string failure = ReadSmall(Path.Combine(folder, "test-failure.txt")) ?? "";
        var reports = new List<(string, string)>();
        if (Directory.Exists(folder))
            foreach (var f in Directory.GetFiles(folder).Where(f => (f.EndsWith(".txt") || f.EndsWith(".json") || f.EndsWith(".csv")) && !f.EndsWith("test-failure.txt") && File.GetLastWriteTime(f) >= started.AddSeconds(-2)).OrderBy(f => f))
                if (ReadSmall(f) is { } text) reports.Add((Path.GetFileName(f), text));
        return new Result(test, outcome, watch.Elapsed, failure, reports, folder);
    }
    private static string? ReadSmall(string path)
    {
        try { if (!File.Exists(path)) return null; var text = File.ReadAllText(path); return text.Length > 20000 ? text[..20000] + "\n… (cut short)" : text; }
        catch { return null; }
    }
    private static string ReportText(Result r)
    {
        var b = new StringBuilder();
        b.Append($"== {r.Test.Name} ({r.Test.Switch}): {r.Outcome} in {r.Time.TotalSeconds:0} s\n");
        if (r.Failure.Length > 0) b.Append("--- What failed ---\n").Append(r.Failure.TrimEnd()).Append('\n');
        foreach (var (name, text) in r.Reports) b.Append($"--- {name} ---\n").Append(text.TrimEnd()).Append('\n');
        if (r.Failure.Length == 0 && r.Reports.Count == 0) b.Append("(no report written)\n");
        b.Append($"(files: {r.Folder})\n");
        return b.ToString();
    }
    private string AllText()
    {
        var b = new StringBuilder();
        b.Append($"Flashback Tester results, {DateTime.Now:yyyy-MM-dd HH:mm}, testing {(target.SelectedItem as Target)?.Label}\n");
        b.Append($"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} logical processors\n");
        b.Append(string.Join("", done.Select(r => $"{r.Outcome,-12} {r.Test.Name} ({r.Time.TotalSeconds:0} s)\n"))).Append('\n');
        foreach (var r in done) b.Append(ReportText(r)).Append('\n');
        return b.ToString();
    }
}
