using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

internal static class OverlayDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var passed = new List<string>();
        void Check(bool okay, string message) { if (!okay) throw new Exception(message); passed.Add(message); }
        FullscreenOsdDiagnostics.Run(Check);
        var legacy = JsonSerializer.Deserialize<Settings>("{\"Hotkey\":\"Alt+F8\",\"Notifications\":true}")!;
        legacy.Validate();
        Check(legacy.OverlayEnabled && legacy.OverlaySeconds == 3 && legacy.Notifications, "Old settings retain preferences and gain overlay defaults");
        var updated = legacy.Copy(); updated.OverlaySeconds = 6; updated.OverlayCorner = "Bottom left"; updated.PauseHotkey = "Alt+F9";
        Storage.Save(updated); var loaded = Storage.Load(out var warning);
        Check(warning == null && loaded.OverlaySeconds == 6 && loaded.OverlayCorner == "Bottom left" && loaded.PauseHotkey == "Alt+F9", "Overlay and shortcut preferences persist");
        Check(!legacy.RequiresBufferRestart(updated), "Overlay and shortcut changes preserve the buffer");
        updated.FrameRate = 120;
        Check(legacy.RequiresBufferRestart(updated), "Encoding changes still require a buffer restart");
        var duplicate = legacy.Copy(); duplicate.PauseHotkey = duplicate.Hotkey;
        bool rejected = false; try { duplicate.Validate(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Reject matching save and pause shortcuts");
        using (var keys = new Hotkeys(allowShared: false))
        using (var collision = new Hotkeys(allowShared: false))
        using (var probe = new Hotkeys(allowShared: false))
        {
            keys.Register("Ctrl+Alt+F10", "Ctrl+Alt+F11"); collision.Register("Ctrl+Alt+F12");
            rejected = false; try { keys.Register("Ctrl+Alt+F9", "Ctrl+Alt+F12"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Reject a conflicting pause shortcut");
            rejected = false; try { probe.Register("Ctrl+Alt+F10"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "A failed shortcut change restores the previous save shortcut");
            keys.Suspend(); probe.Register("Ctrl+Alt+F10"); probe.Suspend(); keys.Resume();
            rejected = false; try { probe.Register("Ctrl+Alt+F10"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Shortcuts suspend for rebinding and resume afterward");
        }
        foreach (var state in new[] { SaveFeedback.Saving, SaveFeedback.Saved, SaveFeedback.Failed, SaveFeedback.Preview })
        {
            var visual = new OverlayWindow();
            visual.SetMessage(state, state == SaveFeedback.Failed ? "The output folder could not be written" : "Rainbow Six Siege · 60 seconds");
            var element = (FrameworkElement)visual.Content; visual.Content = null;
            element.Measure(new Size(344, 88)); element.Arrange(new Rect(0, 0, 344, 88)); element.UpdateLayout();
            var image = new RenderTargetBitmap(344, 88, 96, 96, PixelFormats.Pbgra32); image.Render(element);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(Storage.Root, "overlay-" + state + ".png"))) png.Save(stream);
            Check(visual.State == state, "Render distinct " + state + " feedback"); visual.Close();
        }
        var options = new Settings { OverlaySeconds = 2 };
        var overlay = new OverlayWindow(); bool closed = false; overlay.Closed += (_, _) => closed = true;
        var before = NativeOverlay.GetForegroundWindow();
        overlay.Present(SaveFeedback.Saving, "Finishing your replay", options, SaveOverlay.CurrentMonitor(), showWindow: false);
        var flags = NativeOverlay.GetWindowLongPtr(new WindowInteropHelper(overlay).Handle, -20).ToInt64();
        Check((flags & 0x080000A0) == 0x080000A0 && !overlay.ShowActivated && !overlay.IsHitTestVisible, "Native overlay is non-activating, click-through and absent from the taskbar");
        Check(NativeOverlay.GetForegroundWindow() == before && !overlay.IsVisible, "Offscreen checks do not change focus or display a window");
        await Task.Delay(2200);
        Check(!closed, "Saving progress remains until a result arrives");
        overlay.Present(SaveFeedback.Saved, "Replay saved", options, SaveOverlay.CurrentMonitor(), showWindow: false);
        await Task.Delay(2300);
        Check(closed, "Result overlay closes after the configured duration");
        File.WriteAllText(Path.Combine(Storage.Root, "overlay-results.json"), JsonSerializer.Serialize(new { passed, captureExclusionApplied = overlay.CaptureExclusionApplied }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

