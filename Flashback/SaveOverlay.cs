using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flashback;

public enum SaveFeedback { Saving, Saved, Failed, Preview }

// Created only while a notification is visible. No polling, rendering loop or game hooks.
public sealed class SaveOverlay : IDisposable
{
    private OverlayWindow? window;
    private readonly FullscreenOsd fullscreen = new();
    public static IntPtr CurrentMonitor() => NativeOverlay.MonitorFromWindow(NativeOverlay.GetForegroundWindow(), 2);
    public void Show(SaveFeedback state, string detail, Settings options, IntPtr monitor)
    {
        if (!options.OverlayEnabled)
        { Dispose(); return; } // Never leave a previous clip's success flag visible during a new save.
        if (options.FullscreenNotifications && fullscreen.Show(state, options.OverlaySeconds))
        { window?.Close(); window = null; return; }
        fullscreen.Dispose();
        if (window == null)
        {
            var created = new OverlayWindow();
            created.Closed += (_, _) => { if (ReferenceEquals(window, created)) window = null; };
            window = created;
        }
        window.Present(state, detail, options, monitor);
    }
    public void Dispose() { fullscreen.Dispose(); window?.Close(); window = null; }
}

internal sealed class OverlayWindow : Window
{
    private readonly TextBlock title;
    private readonly TextBlock subtitle;
    private readonly TextBlock symbol;
    private readonly Border accent;
    private readonly DispatcherTimer dismiss;
    internal SaveFeedback State { get; private set; }
    internal bool CaptureExclusionApplied { get; private set; }
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    public OverlayWindow()
    {
        Width = 344; Height = 88;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        IsHitTestVisible = false; Title = "Flashback save status";
        FontFamily = new FontFamily("Segoe UI");
        title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        subtitle = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(199, 207, 214)), Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        symbol = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 21, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(17, 0, 16, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        text.Children.Add(title); text.Children.Add(subtitle);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1); grid.Children.Add(symbol); grid.Children.Add(text);
        accent = new Border { BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(31, 36, 43)), Child = grid };
        Content = accent;
        dismiss = new DispatcherTimer(DispatcherPriority.Normal); dismiss.Tick += (_, _) => { dismiss.Stop(); Close(); };
        Closed += (_, _) => dismiss.Stop();
        SourceInitialized += (_, _) =>
        {
            var style = NativeOverlay.GetWindowLongPtr(Handle, -20).ToInt64();
            NativeOverlay.SetWindowLongPtr(Handle, -20, new IntPtr(style | 0x08000000L | 0x80 | 0x20));
            // Keep our own status flag out of subsequent desktop replays when Windows supports it.
            CaptureExclusionApplied = NativeOverlay.SetWindowDisplayAffinity(Handle, 0x11);
            HwndSource.FromHwnd(Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                if (msg == 0x0084) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
                return IntPtr.Zero;
            });
        };
    }
    internal void SetMessage(SaveFeedback state, string detail)
    {
        State = state;
        title.Text = state switch { SaveFeedback.Saved => "Clip saved", SaveFeedback.Failed => "Clip not saved", SaveFeedback.Preview => "Preview · Clip saved", _ => "Saving replay…" };
        subtitle.Text = detail.Replace('\r', ' ').Replace('\n', ' ');
        symbol.Text = state switch { SaveFeedback.Saved or SaveFeedback.Preview => "\uE73E", SaveFeedback.Failed => "\uE711", _ => "\uE74E" };
        var color = state == SaveFeedback.Failed ? Color.FromRgb(245, 140, 133) : state == SaveFeedback.Saving ? Color.FromRgb(189, 201, 216) : Color.FromRgb(117, 212, 163);
        accent.BorderBrush = symbol.Foreground = new SolidColorBrush(color);
    }
    public void Present(SaveFeedback state, string detail, Settings options, IntPtr monitor, bool showWindow = true)
    {
        dismiss.Stop(); SetMessage(state, detail);
        new WindowInteropHelper(this).EnsureHandle();
        Position(monitor, options.OverlayCorner);
        if (showWindow && !IsVisible) Show();
        Position(monitor, options.OverlayCorner);
        // Progress stays until save completes or fails. Success/failure is short-lived.
        if (state != SaveFeedback.Saving) { dismiss.Interval = TimeSpan.FromSeconds(options.OverlaySeconds); dismiss.Start(); }
    }
    private void Position(IntPtr monitor, string corner)
    {
        var info = new NativeOverlay.MonitorInfo { Size = Marshal.SizeOf<NativeOverlay.MonitorInfo>() };
        if (!NativeOverlay.GetMonitorInfo(monitor, ref info))
        { monitor = NativeOverlay.MonitorFromWindow(Handle, 2); NativeOverlay.GetMonitorInfo(monitor, ref info); }
        // Move to the target monitor before querying its per-window DPI.
        if (NativeOverlay.MonitorFromWindow(Handle, 2) != monitor)
            NativeOverlay.SetWindowPos(Handle, new IntPtr(-1), info.Work.Left, info.Work.Top, 0, 0, 0x0011);
        double scale = Math.Max(96, NativeOverlay.GetDpiForWindow(Handle)) / 96.0;
        int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale), gap = (int)Math.Round(22 * scale);
        int x = corner.EndsWith("right") ? info.Work.Right - width - gap : info.Work.Left + gap;
        int y = corner.StartsWith("Bottom") ? info.Work.Bottom - height - gap : info.Work.Top + gap;
        NativeOverlay.SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, 0x0010);
    }
}

internal static class NativeOverlay
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}

