using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Flashback;

public partial class MainWindow
{
    private UpdateInfo? availableUpdate;
    private string? downloadedUpdate;
    private bool updating, downloadingUpdate;
    private DispatcherTimer? updateTimer, idleInstallTimer;

    // Checks shortly after launch and every six hours, never while the app is only rendering for tests.
    private void StartUpdateChecks()
    {
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => { if (settings.CheckForUpdates) await CheckForUpdateAsync(false); };
        updateTimer.Start();
        Loaded += async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(8)); if (settings.CheckForUpdates) await CheckForUpdateAsync(false); };
        // Once an update is downloaded, it installs at the first quiet moment (see IdleForUpdate).
        idleInstallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        idleInstallTimer.Tick += async (_, _) =>
        {
            if (downloadedUpdate == null || availableUpdate == null || !settings.InstallUpdatesAutomatically || !settings.CheckForUpdates) return;
            if (IdleForUpdate()) await InstallUpdateAsync(availableUpdate, downloadedUpdate, quiet: true);
        };
        idleInstallTimer.Start();
    }
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (updating) return;
        UpdateStatusLabel.Text = "Checking…"; CheckUpdatesButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var found = await Updater.CheckAsync(timeout.Token);
            if (found == null)
            {
                availableUpdate = null; downloadedUpdate = null;
                UpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusLabel.Text = $"You're on the latest version ({Updater.Current}).";
                return;
            }
            if (availableUpdate?.Version != found.Version) downloadedUpdate = null;
            availableUpdate = found;
            // A small green download icon beside the title; the tooltip says what it installs.
            UpdateButton.ToolTip = $"Flashback {found.Version} is ready. Click to update from {Updater.Current}.";
            System.Windows.Automation.AutomationProperties.SetName(UpdateButton, $"Install Flashback {found.Version}");
            UpdateButton.Visibility = Visibility.Visible;
            if (settings.InstallUpdatesAutomatically)
            {
                UpdateStatusLabel.Text = $"Flashback {found.Version} is available. It installs by itself when you're not in a game.";
                if (manual) Tell($"Flashback {found.Version} is available. It downloads now and installs by itself when you're not in a game, or click the green download icon to update now.");
                _ = DownloadInBackgroundAsync(found);
            }
            else
            {
                UpdateStatusLabel.Text = $"Flashback {found.Version} is available.";
                if (manual || !IsActive) Tell($"Flashback {found.Version} is available. Click the green download icon next to the title when you're ready.");
            }
        }
        catch (Exception ex)
        {
            // Automatic checks stay quiet; only a manual check reports why it failed.
            UpdateStatusLabel.Text = "Couldn't check for updates.";
            if (manual) Tell("Couldn't check for updates. " + ex.Message, true);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
    // Downloads without a word; a failure just waits for the next check.
    private async Task DownloadInBackgroundAsync(UpdateInfo update)
    {
        if (downloadingUpdate || downloadedUpdate != null) return;
        downloadingUpdate = true;
        try
        {
            var path = await Task.Run(() => Updater.DownloadAsync(update, new Progress<double>(_ => { }), CancellationToken.None));
            if (availableUpdate?.Version == update.Version) downloadedUpdate = path;
        }
        catch { }
        finally { downloadingUpdate = false; }
    }
    // A quiet moment: no game running or in front, no editor open, nothing being saved, and Flashback's own
    // window isn't the one being used.
    private bool IdleForUpdate()
    {
        if (updating || busy || quitting || recorder.IsSaving || recovery.IsRunning || IsActive) return false;
        if (Application.Current.Windows.OfType<TrimWindow>().Any()) return false;
        if (autoGamePid != 0 && GameDetector.IsRunning(autoGamePid)) return false;
        return GameDetector.ForegroundGame() == 0 && !GameTracker.AnyKnownGameRunning();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(true);
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || updating) return;
        string notes = Updater.Summary(update.Notes);
        string warning = recordingRequested ? "\n\nThe replay buffer's footage so far is discarded; recording starts again straight after. Save a clip first if you need it." : "";
        if (!ThemedDialog.Confirm(this, $"Update to Flashback {update.Version}?",
            (notes.Length > 0 ? notes + "\n\n" : "") + "Flashback closes, installs the update and reopens in a few seconds. Your clips and settings are kept." + warning, "Update now", "Later")) return;
        updating = true; UpdateButton.IsEnabled = false;
        try
        {
            var installer = downloadedUpdate ?? await Updater.DownloadAsync(update, new Progress<double>(p => Tell($"Downloading Flashback {update.Version}… {p:P0}")), CancellationToken.None);
            updating = false;
            await InstallUpdateAsync(update, installer, quiet: false);
        }
        catch (Exception ex)
        {
            updating = false; UpdateButton.IsEnabled = true;
            Tell("The update couldn't be downloaded. " + ex.Message, true);
        }
    }
    // Hands over to the installer and quits. It installs, then starts Flashback again the way it was: in the
    // tray if the window was hidden, and recording if it was.
    private async Task InstallUpdateAsync(UpdateInfo update, string installer, bool quiet)
    {
        if (updating) return;
        updating = true;
        try
        {
            string args = "--update --updated" + (quiet ? " --quiet" : "") + (IsVisible && WindowState != WindowState.Minimized ? "" : " --tray") + (recordingRequested ? " --resume-recording" : "");
            if (!quiet) Tell($"Installing Flashback {update.Version}…");
            Process.Start(new ProcessStartInfo(installer, args) { UseShellExecute = true });
            await QuitAsync();
        }
        catch (Exception ex)
        {
            updating = false; UpdateButton.IsEnabled = true;
            // A broken download is fetched again next time.
            downloadedUpdate = null;
            if (!quiet) Tell("The update couldn't start. " + ex.Message, true);
        }
    }
    // After an update, say so once (in the tray too if the window stays hidden).
    private void AnnounceUpdated()
    {
        string message = $"Updated to Flashback {Updater.Current}.";
        Tell(message);
        if (!IsVisible) try { tray.ShowBalloonTip(3500, "Flashback", message, System.Windows.Forms.ToolTipIcon.Info); } catch { }
    }
}
