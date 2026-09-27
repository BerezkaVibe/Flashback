using System;
using System.Windows;
using System.Windows.Threading;

namespace Flashback;

// While the editor isn't in front it does as little as it can, and puts everything back once it's in front
// again, so nothing about the edit or the playhead changes:
// - Playing when it lost focus: paused, and playing again from the same spot on return.
// - Players put away (below): its memory is trimmed (MemoryTrim), as the main window's is when minimized.
// - Minimized, or a full-screen game in front, for ReleaseAfter: the video players (the preview, videos over
//   the clip and music) are closed, and reopened at the playhead on return. A paused editor costs next to
//   nothing, so a quick look at the game and back doesn't reopen anything. The check runs every 2 s, and
//   only while the editor is in the background.
// Only in the app itself (LightInBackground); the tests call these directly.
public partial class TrimWindow
{
    internal static bool LightInBackground;
    internal static TimeSpan ReleaseAfter = TimeSpan.FromSeconds(30);
    private bool pausedInBackground, playersReleased;
    private DispatcherTimer? gameWatch;
    // Since when the editor has been minimized or behind a full-screen game (while it's in the background).
    private DateTime? awaySince;
    internal bool PlayersReleased => playersReleased;
    internal bool PausedInBackground => pausedInBackground;
    internal bool IsPlaying => playing;

    private void InitBackground()
    {
        Deactivated += (_, _) => { if (LightInBackground) WentToBackground(); };
        Activated += (_, _) => { if (LightInBackground) CameToFront(); };
        StateChanged += (_, _) =>
        {
            if (!LightInBackground) return;
            if (WindowState == WindowState.Minimized) { awaySince ??= DateTime.UtcNow; WentToBackground(); } else if (IsActive) CameToFront();
        };
    }
    internal void WentToBackground()
    {
        if (closed) return;
        if (playing) { Pause(); pausedInBackground = true; }
        if (gameWatch == null)
        {
            gameWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            gameWatch.Tick += (_, _) =>
            {
                if (IsActive || WindowState != WindowState.Minimized && GameDetector.ForegroundGame() == 0) { awaySince = null; return; }
                awaySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - awaySince >= ReleaseAfter) ReleasePlayers();
            };
        }
        gameWatch.Start();
    }
    internal void CameToFront()
    {
        gameWatch?.Stop(); awaySince = null;
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
        // With the players gone, what they held can go too (not before, or coming back would page it all in again).
        MemoryTrim.Trim();
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
