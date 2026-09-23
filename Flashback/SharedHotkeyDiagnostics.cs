using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Flashback;
internal static class SharedHotkeyDiagnostics
{
    internal static async Task OwnerAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        using var owner = new Hotkeys(allowShared: false);
        owner.Register("F8");
        owner.Pressed += () => File.AppendAllText(Path.Combine(Storage.Root, "owner-events.txt"), "F8 received " + DateTimeOffset.Now.ToString("O") + "\n");
        File.WriteAllText(Path.Combine(Storage.Root, "owner-ready.txt"), "Independent F8 owner ready");
        await Task.Delay(TimeSpan.FromMinutes(4));
    }
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); File.AppendAllText(Path.Combine(Storage.Root, "shared-results.txt"), "PASS " + text + "\n"); }
        using var owner = new Hotkeys(allowShared: false);
        owner.Register("F22", "Ctrl+Alt+F23");
        using var shared = new Hotkeys(); int saves = 0, pauses = 0;
        shared.Pressed += () => saves++; shared.PausePressed += () => pauses++;
        shared.Register("F22", "Ctrl+Alt+F23");
        Check(shared.UsesSharedInput, "Occupied save and pause shortcuts register through background raw input");
        shared.FeedSharedForTest(0x85, true); shared.FeedSharedForTest(0x85, true); shared.FeedSharedForTest(0x85, false);
        Check(saves == 1, "Held shared save key triggers once until released");
        shared.FeedSharedForTest(0xA2, true); shared.FeedSharedForTest(0xA4, true); shared.FeedSharedForTest(0x86, true); shared.FeedSharedForTest(0x86, false);
        Check(pauses == 1 && saves == 1, "Shared modifier shortcut selects the correct action");
        shared.FeedSharedForTest(0x85, true); shared.FeedSharedForTest(0x85, false);
        Check(saves == 1, "Extra modifiers do not accidentally trigger a single-key shortcut");
        shared.Suspend(); shared.FeedSharedForTest(0x85, true);
        Check(!shared.UsesSharedInput && saves == 1, "Editing suspends the shared background listener");
        shared.Resume(); shared.FeedSharedForTest(0x85, true); shared.FeedSharedForTest(0x85, false);
        Check(shared.UsesSharedInput && saves == 2, "Shared shortcuts resume after editing");
        shared.Register("F24"); Check(!shared.UsesSharedInput, "Moving to an available shortcut releases raw input and uses normal Windows registration");
        shared.FeedSharedForTest(0x85, true); Check(saves == 2, "Old shared binding cannot trigger after rebinding");
        var main = new MainWindow(true);
        main.Show(); main.WindowState = WindowState.Minimized; await Task.Delay(100);
        Check(main.IsVisible && main.ShowInTaskbar && main.WindowState == WindowState.Minimized, "Minimize retains the taskbar window");
        main.WindowState = WindowState.Normal; main.Close();
        Check(!main.IsVisible, "X hides the window into the tray without quitting");
        main.ShowWindow(); Check(main.IsVisible && main.WindowState == WindowState.Normal, "Reopening from the tray restores the window");
        await main.QuitAsync();
    }
}
