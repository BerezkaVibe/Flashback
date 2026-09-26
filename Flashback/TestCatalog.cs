using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flashback;

// Every built-in test the Tester can run (Flashback.exe --tester), in groups. Each is a switch this app
// answers (see App.OnStartup): it runs in a data folder of its own, writes its reports there and exits with
// 0 when it passed. `--list-tests` writes this list to tests.json in the data folder, so the Tester shows
// what the copy of Flashback being tested actually has.
internal sealed record TestInfo(string Switch, string Name, string Group, string About, bool Long = false, bool UsesScreen = false, bool NeedsClip = false, bool ClipOptional = false);

internal static class TestCatalog
{
    internal static readonly TestInfo[] All =
    {
        // The FFmpeg preview player
        new("--ffmpeg-engine-test", "FFmpeg engine", "FFmpeg player", "Exact frames (CPU and graphics card), scrubbing, track mixing, cut-outs, the timeline's pictures and sound, music, speed changes. About a minute."),
        new("--player-compare-test", "Windows vs FFmpeg player", "FFmpeg player", "Both preview players on the same clip: open time, seek accuracy, scrubbing, smoothness, speed parts, freezes, CPU and memory. Opens editor windows; leave the mouse alone. A few minutes.", UsesScreen: true),
        // The editor
        new("--sequence-test", "Finished-video timeline", "Editor", "Sections, speed parts and freezes laid out as the export plays them; sounds under holds; the live editor playing across holds and section jumps.", UsesScreen: true),
        new("--trim-interaction-test", "Editor controls", "Editor", "Keyboard shortcuts, dragging, tools, cut-outs, undo and the playhead in a live editor.", UsesScreen: true),
        new("--trim-preview-test", "Editor preview", "Editor", "The preview following seeks, playback and the parts on the timeline.", UsesScreen: true),
        new("--editor-workspace-test", "Saving edits", "Editor", "Saving, reopening and discarding edits; recovery after a crash.", UsesScreen: true),
        new("--edit-test", "Editing and exports", "Editor", "Trimming, sections, cut-outs and speed with real exports checked.", UsesScreen: true),
        new("--overlay-test", "Text, pictures and shapes", "Editor", "Items over the video: placement, timing, keyframes, and how they export.", UsesScreen: true),
        new("--media-parts-test", "Blur, videos, keyframes, freezes, music", "Editor", "Blur and pixelate, videos over the clip, keyframes, freeze frames, volume parts and music in real exports, checked pixel by pixel and sample by sample."),
        new("--media-parts-live-test", "Blur, videos, keyframes, freezes, music (live)", "Editor", "The same, plus the live editor with screenshots.", UsesScreen: true),
        new("--trim-perf-test", "Editor speed", "Editor", "Times the editor's busiest paths with a realistic edit (1080p60, lanes open, speed and zoom parts, text and pictures).", UsesScreen: true),
        new("--stress-test", "Heavy edit", "Editor", "A heavy edit: many drawings, blur areas, inset videos, keyframes, freezes, reordered sections. Times it, plays it, exports it.", UsesScreen: true),
        new("--stress-big-test", "Heavy edit, bigger", "Editor", "The heavy edit at a larger size.", Long: true, UsesScreen: true),
        new("--scrub-latency-test", "Scrubbing latency", "Editor", "How quickly the picture follows the playhead while dragging.", UsesScreen: true),
        new("--slider-test", "Sliders", "Editor", "The editor's sliders and their values.", UsesScreen: true),
        new("--frame-history-test", "Frame history", "Editor", "Stepping frame by frame and the history of frames shown.", UsesScreen: true),
        new("--background-test", "Editor in the background", "Editor", "CPU and memory with the editor in front, behind other windows, minimized and back.", UsesScreen: true),
        new("--scrub-test", "Scrub a clip you choose", "Editor", "Scrubs the chosen clip and reports how the preview keeps up.", UsesScreen: true, NeedsClip: true),
        new("--playback-cost-test", "Playback cost of a clip you choose", "Editor", "CPU and memory while playing the chosen clip.", UsesScreen: true, NeedsClip: true),
        new("--lag-compare", "Lag comparison", "Editor", "Timeline lag with and without the newer parts (uses the chosen clip if there is one).", UsesScreen: true, ClipOptional: true),
        // Exporting
        new("--export-matrix-test", "Export formats", "Export", "Every export format and size, checked."),
        new("--export-threads-test", "Export threads", "Export", "Export speed with different thread counts."),
        new("--export-decode-test", "Export decoding: CPU vs graphics card", "Export", "Exports decoding on the CPU and on the graphics card, with and without a game-like load (needs an NVIDIA card for the load)."),
        new("--encoder-test", "Video encoders", "Export", "Which graphics-card and CPU encoders work here."),
        // Recording (records the screen and sound)
        new("--self-test", "Quick self-test", "Recording", "A quick check of settings, storage and the recorder.", UsesScreen: true),
        new("--capture-test", "Screen capture", "Recording", "Records the screen briefly and checks the clip.", UsesScreen: true),
        new("--audio-test", "Audio", "Recording", "Desktop and microphone capture.", UsesScreen: true),
        new("--audio-hardware-test", "Audio (your devices)", "Recording", "Audio capture on the real devices.", UsesScreen: true),
        new("--mixer-test", "Per-app audio", "Recording", "Records the per-app mix for 4 seconds; play something first.", UsesScreen: true),
        new("--motion-test", "Motion", "Recording", "Smoothness of recorded motion.", UsesScreen: true),
        new("--motion-hardware-test", "Motion (graphics card)", "Recording", "Smoothness of recorded motion with the graphics-card encoder.", UsesScreen: true),
        new("--continuity-test", "Continuity", "Recording", "No gaps or repeats across the replay buffer's segments.", UsesScreen: true),
        new("--continuity-hardware-test", "Continuity (graphics card)", "Recording", "The same with the graphics-card encoder.", UsesScreen: true),
        new("--system-test", "Whole system", "Recording", "Recording, saving clips and the buffer end to end.", UsesScreen: true),
        new("--performance-test", "Recording performance", "Recording", "CPU and memory while recording.", UsesScreen: true),
        new("--performance-compatibility-test", "Recording performance (compatibility)", "Recording", "The same in compatibility mode.", UsesScreen: true),
        new("--performance-matrix-test", "Lighter-mode settings", "Recording", "Records 12 s with each setting a lighter mode could change and compares CPU and memory.", Long: true, UsesScreen: true),
        new("--start-timing-test", "Start timing", "Recording", "How long recording takes to start.", UsesScreen: true),
        new("--clock-test", "Clocks", "Recording", "The recorder's clocks agree."),
        new("--sync-media-test", "Audio and video sync", "Recording", "Sound and picture stay together in clips.", UsesScreen: true),
        new("--backlog-test", "Backlog recovery", "Recording", "Recovering when the encoder falls behind.", UsesScreen: true),
        new("--soak-test", "Soak", "Recording", "Records for a long time and watches for leaks and drift.", Long: true, UsesScreen: true),
        // The app
        new("--ui-test", "Main window", "App", "The main window's controls and settings.", UsesScreen: true),
        new("--startup-ui-test", "Start-up", "App", "Starting up and the first window.", UsesScreen: true),
        new("--shortcut-ui-test", "Shortcut settings", "App", "Setting and clearing the clip shortcut.", UsesScreen: true),
        new("--shortcut-logic-test", "Shortcut logic", "App", "Shortcut parsing and conflicts."),
        new("--shared-hotkey-test", "Shared shortcut", "App", "The clip shortcut when another app holds it too.", UsesScreen: true),
        new("--update-check-test", "Update check", "App", "Checking GitHub for updates (needs internet)."),
        new("--playback-test", "Playback", "App", "Playing clips from the main window.", UsesScreen: true),
    };

    // Writes the list for the Tester (--list-tests).
    internal static void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tests.json"), JsonSerializer.Serialize(All));
    }
    internal static IReadOnlyList<TestInfo>? Read(string folder)
    {
        try { return JsonSerializer.Deserialize<TestInfo[]>(File.ReadAllText(Path.Combine(folder, "tests.json"))); }
        catch { return null; }
    }
}
