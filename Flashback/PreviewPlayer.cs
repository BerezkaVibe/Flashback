using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flashback;

// The editor's preview player: the Windows player (MediaElement) or the FFmpeg one (FfmpegPreviewPlayer),
// chosen in Settings > Performance. It answers the way MediaElement does, so the editor uses either the
// same way; the picture it shows is Visual.
internal sealed class PreviewPlayer
{
    private readonly MediaElement windows;
    private readonly FfmpegPreviewPlayer? engine;
    internal FfmpegPreviewPlayer? Engine => engine;
    internal bool UsesFfmpeg => engine != null;
    internal FrameworkElement Visual => engine != null ? engine.View : windows;
    // Where the preview shows (for the blur and pixelate shapes, which show it treated).
    public static implicit operator UIElement(PreviewPlayer player) => player.Visual;

    internal PreviewPlayer(MediaElement windows, FfmpegPreviewPlayer? engine)
    {
        this.windows = windows; this.engine = engine;
        if (engine != null) windows.Visibility = Visibility.Collapsed;
    }
    internal Duration NaturalDuration => engine == null ? windows.NaturalDuration : engine.IsOpen && engine.Duration > 0 ? new Duration(TimeSpan.FromSeconds(engine.Duration)) : Duration.Automatic;
    // The recording's moment playing. (The FFmpeg player's own time runs along the editor's timeline; the
    // editor drives it through Engine.)
    internal TimeSpan Position
    {
        get => engine == null ? windows.Position : TimeSpan.FromSeconds(engine.Where.Source);
        set { if (engine == null) windows.Position = value; else engine.Position = engine.TimeOf(value.TotalSeconds); }
    }
    internal void Play() { if (engine == null) windows.Play(); else engine.Play(); }
    internal void Pause() { if (engine == null) windows.Pause(); else engine.Pause(); }
    internal void Close() { if (engine == null) windows.Close(); else engine.Close(); }
    // The FFmpeg player plays the editor's whole timeline itself (sections, speed parts, freezes, music and
    // sound files); with the Windows player the editor steps it through them.
    internal bool PlaysTimeline => engine != null;
    internal double SpeedRatio { get => engine == null ? windows.SpeedRatio : engine.SpeedRatio; set { if (engine == null) windows.SpeedRatio = value; else engine.SpeedRatio = value; } }
    internal double Volume { get => engine == null ? windows.Volume : engine.Volume; set { if (engine == null) windows.Volume = value; else engine.Volume = value; } }
    internal bool IsMuted { get => engine == null ? windows.IsMuted : engine.IsMuted; set { if (engine == null) windows.IsMuted = value; else engine.IsMuted = value; } }
    // The Windows player needs scrubbing switched on to show frames while paused; the FFmpeg one always does.
    internal bool ScrubbingEnabled { get => engine != null || windows.ScrubbingEnabled; set { if (engine == null) windows.ScrubbingEnabled = value; } }
    private Uri? source;
    internal Uri? Source
    {
        get => engine == null ? windows.Source : source;
        set
        {
            if (engine == null) { windows.Source = value; return; }
            source = value;
            if (value == null) engine.Close(); else engine.Open(value.LocalPath);
        }
    }
    internal int NaturalVideoWidth => engine == null ? windows.NaturalVideoWidth : engine.VideoWidth;
    internal Transform RenderTransform { get => Visual.RenderTransform; set => Visual.RenderTransform = value; }
    internal Geometry? Clip { get => Visual.Clip; set => Visual.Clip = value; }
    internal double ActualWidth => Visual.ActualWidth;
    internal double ActualHeight => Visual.ActualHeight;
}
