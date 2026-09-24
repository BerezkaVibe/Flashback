using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Flashback;

public partial class MainWindow
{
    private UpdateInfo? availableUpdate;
    private bool updating;
    private DispatcherTimer? updateTimer;

    // Checks shortly after launch and every six hours, never while the app is only rendering for tests.
    private void StartUpdateChecks()
    {
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => { if (settings.CheckForUpdates) await CheckForUpdateAsync(false); };
        updateTimer.Start();
        Loaded += async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(8)); if (settings.CheckForUpdates) await CheckForUpdateAsync(false); };
    }
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (updating) return;
        UpdateStatusLabel.Text = "Checking…"; CheckUpdatesButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            availableUpdate = await Updater.CheckAsync(timeout.Token);
            if (availableUpdate == null)
            {
                UpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusLabel.Text = $"You're on the latest version ({Updater.Current}).";
                return;
            }
            // A small green download icon beside the title; the tooltip says what it installs.
            UpdateButton.ToolTip = $"Flashback {availableUpdate.Version} is ready. Click to update from {Updater.Current}.";
            System.Windows.Automation.AutomationProperties.SetName(UpdateButton, $"Install Flashback {availableUpdate.Version}");
            UpdateButton.Visibility = Visibility.Visible;
            UpdateStatusLabel.Text = $"Flashback {availableUpdate.Version} is available.";
            if (manual || !IsActive) Tell($"Flashback {availableUpdate.Version} is available. Click the green download icon next to the title when you're ready.");
        }
        catch (Exception ex)
        {
            // Automatic checks stay quiet; only a manual check reports why it failed.
            UpdateStatusLabel.Text = "Couldn't check for updates.";
            if (manual) Tell("Couldn't check for updates. " + ex.Message, true);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(true);
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || updating) return;
        string notes = Updater.Summary(update.Notes);
        string warning = recordingRequested ? "\n\nRecording stops for the update, and unsaved replay footage is discarded. Save a clip first if you need it." : "";
        if (!ThemedDialog.Confirm(this, $"Update to Flashback {update.Version}?",
            (notes.Length > 0 ? notes + "\n\n" : "") + "Flashback closes, installs the update and reopens. Your clips and settings are kept." + warning, "Update now", "Later")) return;
        updating = true; UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => Tell($"Downloading Flashback {update.Version}… {p:P0}"));
            var installer = await Updater.DownloadAsync(update, progress, CancellationToken.None);
            Tell("Starting the update…");
            Process.Start(new ProcessStartInfo(installer, "--update") { UseShellExecute = true });
            await QuitAsync();
        }
        catch (Exception ex)
        {
            updating = false; UpdateButton.IsEnabled = true;
            Tell("The update couldn't be downloaded. " + ex.Message, true);
        }
    }
}
