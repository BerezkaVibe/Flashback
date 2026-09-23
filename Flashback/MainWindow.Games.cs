using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Flashback;

public partial class MainWindow
{
    private int autoGamePid;
    private long autoGameGoneSince;
    private bool autoGameBusy;

    // Starts the buffer when a game comes to the front. Only a buffer this started is
    // stopped again, 30 seconds after that game's process has exited.
    private async Task AutoGameTickAsync()
    {
        if (!settings.AutoStartWithGames || autoGameBusy || quitting || busy) return;
        autoGameBusy = true;
        try
        {
            if (autoGamePid == 0)
            {
                if (recordingRequested) return;
                int game = GameDetector.ForegroundGame();
                if (game == 0) return;
                autoGamePid = game; autoGameGoneSince = 0;
                await ToggleAsync();
                if (!recordingRequested) { autoGamePid = 0; return; }
                GameTracker.Update();
                Tell($"{GameTracker.LastForeground} detected, so recording started automatically.");
                return;
            }
            if (!recordingRequested) { autoGamePid = 0; return; } // Paused by hand: stay paused until the next game.
            if (GameDetector.IsRunning(autoGamePid)) { autoGameGoneSince = 0; return; }
            if (autoGameGoneSince == 0) { autoGameGoneSince = Stopwatch.GetTimestamp(); return; }
            if (Stopwatch.GetElapsedTime(autoGameGoneSince).TotalSeconds < 30) return;
            autoGamePid = 0;
            await ToggleAsync();
            Tell("The game closed, so recording paused automatically.");
        }
        catch (Exception ex) { Tell("Automatic recording could not start. " + ex.Message, true); autoGamePid = 0; }
        finally { autoGameBusy = false; }
    }
}
