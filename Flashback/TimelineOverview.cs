using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// A lightweight full-clip map: the highlighted window is the portion being edited.
internal sealed class TimelineOverview : FrameworkElement
{
    internal TrimTimeline? Timeline;
    private double grabOffset;
    private double TrackWidth => Math.Max(1,ActualWidth-4);
    internal Rect Viewport => Timeline is not {Duration:>0} t ? Rect.Empty :
        new Rect(2+t.ViewStart/t.Duration*TrackWidth,5,Math.Max(2,t.VisibleDuration/t.Duration*TrackWidth),Math.Max(1,ActualHeight-10));
    public TimelineOverview() { Cursor=Cursors.SizeWE; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        if(Timeline is not {Duration:>0})return;
        var accent=(Brush)FindResource("Accent");
        dc.DrawRoundedRectangle((Brush)FindResource("ControlSurface"),null,new Rect(2,5,TrackWidth,Math.Max(1,ActualHeight-10)),3,3);
        var view=Viewport;
        dc.DrawRoundedRectangle((Brush)FindResource("SelectionSurface"),new Pen(accent,1.5),view,3,3);
        if(view.Width>16)
        {
            dc.DrawLine(new Pen(accent,2),new Point(view.Left+5,view.Top+4),new Point(view.Left+5,view.Bottom-4));
            dc.DrawLine(new Pen(accent,2),new Point(view.Right-5,view.Top+4),new Point(view.Right-5,view.Bottom-4));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if(Timeline is not {Duration:>0} t || !t.IsEnabled)return;
        var p=e.GetPosition(this);var view=Viewport;
        grabOffset=view.Contains(p) ? p.X-view.Left : view.Width/2;
        CaptureMouse();Pan(p.X);e.Handled=true;
    }
    private void Pan(double x)
    {
        if(Timeline is {} t)t.PanTo((x-2-grabOffset)/TrackWidth*t.Duration);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e);if(IsMouseCaptured)Pan(e.GetPosition(this).X); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if(!IsMouseCaptured)return;
        Pan(e.GetPosition(this).X);ReleaseMouseCapture();Timeline?.Focus();e.Handled=true;
    }
}

public partial class TrimWindow
{
    private void RefreshTimelineZoom()
    {
        double zoom=Timeline.Duration/Math.Max(.001,Timeline.VisibleDuration);
        bool zoomed=zoom>1.001;
        TimelineZoomLabel.Text=zoomed ? $"Timeline · {zoom:0.#}× zoom" : "Timeline · Full clip";
        TimelineZoomLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,zoomed ? "Accent" : "Muted");
        TimelineFitButton.IsEnabled=zoomed;
        TimelineMap.ToolTip=zoomed ? "Full clip overview · Drag the highlighted window to pan the zoomed timeline. Fit shows the entire clip." : "Full clip overview · Zoom in to see a smaller portion of the timeline.";
        System.Windows.Automation.AutomationProperties.SetName(TimelineMap,$"Clip overview. Showing {KeepSection.TimeText(Timeline.ViewStart)} to {KeepSection.TimeText(Timeline.ViewStart+Timeline.VisibleDuration)} of {KeepSection.TimeText(Timeline.Duration)}");
        TimelineMap.InvalidateVisual();
    }
}
