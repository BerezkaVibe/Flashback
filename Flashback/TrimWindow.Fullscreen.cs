using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Flashback;

// Full screen: the video fills the screen, and a slim dock with the timeline and the tool icons fades up
// over its bottom edge when the pointer comes near it, then fades away again. F11 (or Esc) leaves.
public partial class TrimWindow
{
    private bool fullscreen;
    internal bool IsFullscreen => fullscreen;
    private (WindowStyle Style, ResizeMode Resize, WindowState State, Thickness Margin, CornerRadius Corners, bool LanesOpen, bool LayersOpen)? beforeFullscreen;
    private readonly List<(UIElement Element, Visibility Was)> hiddenForFullscreen = new();
    private DispatcherTimer? dockHide;
    private bool dockShown;
    // Where the pointer last was over the window, for deciding whether the dock can go.
    private Point lastPointer;
    // How near the bottom the pointer has to come for the dock to show.
    private const double DockReach = 90;

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    internal void ToggleFullscreen()
    {
        if (!fullscreen && source.Length == 0) return;
        fullscreen = !fullscreen;
        if (fullscreen) EnterFullscreen(); else LeaveFullscreen();
        FullscreenButton.Content = fullscreen ? "" : "";
        FullscreenButton.ToolTip = fullscreen ? "Leave full screen · F11 or Esc" : "Full screen · F11";
        FullscreenMenu.IsChecked = fullscreen;
    }
    private void EnterFullscreen()
    {
        beforeFullscreen = (WindowStyle, ResizeMode, WindowState, TrimContent.Margin, PreviewFrame.CornerRadius, Timeline.LanesExpanded, Timeline.OverlaysOpen);
        // Borderless and maximized covers the taskbar too (going through Normal makes Windows recompute it).
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Normal; WindowState = WindowState.Maximized;
        EditorMenu.Visibility = Visibility.Collapsed;
        hiddenForFullscreen.Clear();
        foreach (UIElement child in TrimContent.Children)
            if (Grid.GetRow(child) != 1 && !ReferenceEquals(child, FullscreenDock) && !ReferenceEquals(child, Timeline))
            { hiddenForFullscreen.Add((child, child.Visibility)); child.Visibility = Visibility.Collapsed; }
        TrimContent.Margin = new Thickness(0); PreviewFrame.CornerRadius = new CornerRadius(0);
        // The timeline and the tool icons move into the dock, laid out in one slim row.
        TrimContent.Children.Remove(Timeline); DockTimeline.Child = Timeline;
        ToolsHome.Child = null; DockTools.Child = ListControls; ListControls.Orientation = Orientation.Horizontal;
        // Slim: the layer rows fold into their tick strip and the audio lanes close.
        Timeline.OverlaysOpen = false;
        if (Timeline.LanesExpanded) LanesToggleRequested();
        FullscreenDock.Visibility = Visibility.Visible;
        MouseMove -= DockFollowsPointer;
        MouseMove += DockFollowsPointer;
        // Show it briefly, so it's clear where it is.
        ShowDock(true); HideDockSoon();
    }
    private void LeaveFullscreen()
    {
        MouseMove -= DockFollowsPointer;
        dockHide?.Stop();
        FullscreenDock.BeginAnimation(OpacityProperty, null); FullscreenDock.Opacity = 0; FullscreenDock.IsHitTestVisible = false; dockShown = false;
        FullscreenDock.Visibility = Visibility.Collapsed;
        DockTools.Child = null; ListControls.Orientation = Orientation.Vertical; ToolsHome.Child = ListControls;
        DockTimeline.Child = null; TrimContent.Children.Add(Timeline);
        foreach (var (element, was) in hiddenForFullscreen) element.Visibility = was;
        hiddenForFullscreen.Clear();
        EditorMenu.Visibility = Visibility.Visible;
        if (beforeFullscreen is { } before)
        {
            TrimContent.Margin = before.Margin; PreviewFrame.CornerRadius = before.Corners;
            if (before.LanesOpen != Timeline.LanesExpanded) LanesToggleRequested();
            Timeline.OverlaysOpen = before.LayersOpen;
            WindowStyle = before.Style; ResizeMode = before.Resize; WindowState = before.State;
        }
        beforeFullscreen = null;
    }
    // Near the bottom (or over the dock, or while a tool is out, the timeline is being dragged or one of the
    // dock's pop-ups is open) the dock shows; otherwise it fades away a moment later.
    private bool DockWanted(Point pointer) =>
        pointer.Y >= ((FrameworkElement)Content).ActualHeight - DockReach || FullscreenDock.IsMouseOver || Timeline.IsPlacing || Timeline.IsDragging
        || SlowPopup.IsOpen || FreezePopup.IsOpen || SpeedPopup.IsOpen;
    private void DockFollowsPointer(object sender, MouseEventArgs e)
    {
        if (!fullscreen) return;
        lastPointer = e.GetPosition(this);
        if (DockWanted(lastPointer)) { dockHide?.Stop(); ShowDock(true); }
        else if (dockShown) HideDockSoon();
    }
    internal void UpdateDock(Point pointer)
    {
        if (!fullscreen) return;
        lastPointer = pointer;
        if (DockWanted(pointer)) { dockHide?.Stop(); ShowDock(true); } else if (dockShown) HideDockSoon();
    }
    internal bool DockShown => dockShown;
    private void HideDockSoon()
    {
        dockHide ??= new DispatcherTimer(TimeSpan.FromSeconds(1.2), DispatcherPriority.Normal, (_, _) =>
        {
            dockHide!.Stop();
            if (fullscreen && !DockWanted(lastPointer)) ShowDock(false);
        }, Dispatcher);
        if (!dockHide.IsEnabled) dockHide.Start();
    }
    private void ShowDock(bool show)
    {
        if (dockShown == show) return;
        dockShown = show;
        FullscreenDock.IsHitTestVisible = show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 150 : 300));
        if (SystemParameters.ClientAreaAnimation && PerformanceOptions.Animations && IsLoaded) FullscreenDock.BeginAnimation(OpacityProperty, fade);
        else { FullscreenDock.BeginAnimation(OpacityProperty, null); FullscreenDock.Opacity = show ? 1 : 0; }
    }
}
