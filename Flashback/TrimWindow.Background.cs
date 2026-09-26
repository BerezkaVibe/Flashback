using System;
using System.Windows;
using System.Windows.Threading;

namespace Flashback;

// While the editor isn't in front it does as little as it can, and puts everything back once it's in front
// again, so nothing about the edit or the playhead changes:
// - Playing when it lost focus: paused, and playing again from the same spot on return.
// - Minimized: its memory is trimmed (MemoryTrim), as the main window's is.
// - Minimized, or a full-screen game in front: the video players (the preview, videos over the clip and
//   music) are closed, and reopened at the playhead on return. The game check runs every 2 s, and only
//   while the editor is in the background.
// Only in the app itself (LightInBackground); the tests call these directly.
public partial class TrimWindow
{
    internal static bool LightInBackground;
    private bool pausedInBackground, playersReleased;
    private DispatcherTimer? gameWatch;
    internal bool PlayersReleased => playersReleased;
    internal bool PausedInBackground => pausedInBackground;
    internal bool IsPlaying => playing;

    private void InitBackground()
    {
        MemoryTrim.Attach(this);
        Deactivated += (_, _) => { if (LightInBackground) WentToBackground(); };
        Activated += (_, _) => { if (LightInBackground) CameToFront(); };
        StateChanged += (_, _) =>
        {
            if (!LightInBackground) return;
            if (WindowState == WindowState.Minimized) ReleasePlayers(); else if (IsActive) CameToFront();
        };
    }
    internal void WentToBackground()
    {
        if (closed) return;
        if (playing) { Pause(); pausedInBackground = true; }
        if (gameWatch == null)
        {
            gameWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            gameWatch.Tick += (_, _) => { if (!IsActive && (WindowState == WindowState.Minimized || GameDetector.ForegroundGame() != 0)) ReleasePlayers(); };
        }
        gameWatch.Start();
    }
    internal void CameToFront()
    {
        gameWatch?.Stop();
        if (closed || WindowState == WindowState.Minimized) return;
        RestorePlayers();
        if (pausedInBackground) { pausedInBackground = false; if (!playing && source.Length > 0 && exportCancellation == null) Resume(); }
    }
    internal void ReleasePlayers()
    {
        if (playersReleased || !previewEnabled || source.Length == 0 || closed) return;
        if (playing) { Pause(); pausedInBackground = true; }
        clock.Stop();
        Player.Close(); Player.Source = null;
        OverlayView.CloseVideos(); CloseSounds();
        playersReleased = true;
    }
    internal void RestorePlayers()
    {
        if (!playersReleased) return;
        playersReleased = false;
        if (source.Length == 0 || closed) return;
        // Opening primes the player and seeks it to the playhead (Player_Opened).
        Player.Source = new Uri(source); if (!Player.PlaysTimeline) { Player.Play(); Player.Pause(); } clock.Start();
        OverlayView.Items = OverlayView.Items;
        SyncSounds();
    }
}
