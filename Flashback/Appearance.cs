using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Flashback;

internal static class Appearance
{
    internal static readonly string[] Palettes = { "Charcoal", "Midnight", "Slate" };
    internal static readonly string[] Accents = { "Mint", "Blue", "Lavender", "Rose", "Amber" };
    // The console layouts' colors: Surface, controls and outlines come from the palette, the highlight from the accent.
    internal static readonly string[] ConsolePalettes = { "Monochrome", "Matrix", "Midnight", "Dracula", "Gruvbox", "Charcoal", "Slate" };
    internal static readonly string[] ConsoleAccents = { "White", "Green", "Cyan", "Purple", "Amber", "Rose", "Mint" };
    // Colors by resource name for a console layout: its palette and accent, then any of its own colors on top.
    internal static Dictionary<string, string> ConsoleColors(string palette, string accent, IReadOnlyDictionary<string, string>? own = null)
    {
        string[] c = palette switch
        {
            "Matrix" => new[] { "#040805", "#08120A", "#0F1F12", "#1C3822", "#050B06" },
            "Midnight" => new[] { "#080D1A", "#0F172A", "#19243C", "#28385E", "#0A1020" },
            "Dracula" => new[] { "#12111A", "#1A1826", "#262338", "#3C3756", "#15141F" },
            "Gruvbox" => new[] { "#141412", "#1E1E1A", "#292924", "#404037", "#181815" },
            "Charcoal" => new[] { "#0E0F12", "#16181D", "#22252C", "#353945", "#111317" },
            "Slate" => new[] { "#171B21", "#20252B", "#2B323B", "#3E4854", "#1B2027" },
            _ => new[] { "#000000", "#080808", "#121212", "#222222", "#050505" }
        };
        string highlight = accent switch { "Green" => "#00FF66", "Cyan" => "#38BDF8", "Purple" => "#C084FC", "Amber" => "#FBBF24", "Rose" => "#F43F5E", "Mint" => "#2DD4BF", _ => "#FFFFFF" };
        var map = new Dictionary<string, string> { ["AppBackground"] = c[0], ["Surface"] = c[1], ["ControlSurface"] = c[2], ["Outline"] = c[3], ["TitlebarBackground"] = c[4], ["Accent"] = highlight, ["Ink"] = "#EEEEEE", ["Muted"] = "#888888", ["Dim"] = "#555555" };
        if (own != null)
        {
            string? Own(string key) => own.TryGetValue(key, out var v) ? v : null;
            if (Own("background") is { } bg) { map["AppBackground"] = bg; map["TitlebarBackground"] = bg; }
            if (Own("surface") is { } sf) map["Surface"] = sf;
            if (Own("control") is { } ct) map["ControlSurface"] = ct;
            if (Own("outline") is { } ol) map["Outline"] = ol;
            if (Own("accent") is { } ac) map["Accent"] = ac;
            if (Own("text") is { } tx) map["Ink"] = tx;
            if (Own("muted") is { } mu) map["Muted"] = mu;
            if (Own("dim") is { } dm) map["Dim"] = dm;
        }
        var surface = (Color)ColorConverter.ConvertFromString(map["Surface"]); var accentColor = (Color)ColorConverter.ConvertFromString(map["Accent"]);
        string Tint(double s) => Color.FromRgb((byte)(surface.R * (1 - s) + accentColor.R * s), (byte)(surface.G * (1 - s) + accentColor.G * s), (byte)(surface.B * (1 - s) + accentColor.B * s)).ToString();
        map["SelectionSurface"] = Tint(.25); map["HoverSurface"] = Tint(.14);
        double luma = (accentColor.R * .299 + accentColor.G * .587 + accentColor.B * .114) / 255;
        map["OnAccent"] = luma > .55 ? "#111419" : "#F5F5F5";
        return map;
    }
    internal static void Apply(string palette, string accent)
    {
        string[] colors=palette switch {
            "Midnight"=>new[]{"#0D1323","#161E32","#212C43","#31415F"},
            "Slate"=>new[]{"#20252B","#282F37","#353E49","#505C6B"},
            _=>new[]{"#060708","#111318","#252B34","#353E49"}
        };
        void Set(string key,string value) { var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); Application.Current.Resources[key]=brush; }
        Set("AppBackground",colors[0]); Set("Surface",colors[1]); Set("ControlSurface",colors[2]); Set("Outline",colors[3]);
        Set("Accent",accent switch { "Blue"=>"#9FCBFF","Lavender"=>"#CEB5FF","Rose"=>"#F5AAC9","Amber"=>"#F3C881",_=>"#9CE2C1" });
        var surface=(Color)ColorConverter.ConvertFromString(colors[1]);
        var highlight=((SolidColorBrush)Application.Current.Resources["Accent"]).Color;
        void Tint(string key,double strength)
        {
            var color=Color.FromRgb((byte)(surface.R*(1-strength)+highlight.R*strength),(byte)(surface.G*(1-strength)+highlight.G*strength),(byte)(surface.B*(1-strength)+highlight.B*strength));
            Set(key,color.ToString());
        }
        Tint("SelectionSurface",.24); Tint("HoverSurface",.12);
    }
}

public partial class MainWindow
{
    internal void SetRecordingAppearance(bool recording)
    {
        // A small light: grey while paused, red with a red outline while recording.
        RecordDot.SetResourceReference(Shape.FillProperty,recording ? "RecordingLight" : "IdleLight");
        ToggleButton.SetResourceReference(Control.BorderBrushProperty,recording ? "RecordingLight" : "Outline");
        StatusDot.SetResourceReference(Shape.FillProperty,recording ? "RecordingLight" : "IdleLight");
    }
    // While recording is starting, the light breathes gently instead of sitting still.
    private bool pulsing;
    private void PulseRecordLight(bool on)
    {
        if (on == pulsing) return;
        pulsing = on;
        if (!on || !PerformanceOptions.Animations) { RecordDot.BeginAnimation(UIElement.OpacityProperty, null); RecordDot.Opacity = on ? .6 : 1; return; }
        RecordDot.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, .3, TimeSpan.FromMilliseconds(450))
            { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }
    private bool appearanceReady;
    private void LoadAppearance()
    {
        appearanceReady=false;
        // The default layout's colors go on the whole app (the editor too); a console layout's go on this window only.
        Appearance.Apply(Appearance.Palettes.Contains(settings.Palette) ? settings.Palette : "Charcoal", Appearance.Accents.Contains(settings.AccentColor) ? settings.AccentColor : "Mint");
        layoutList = LayoutCatalog.Load(layoutProblems = new());
        LayoutBox.ItemsSource = layoutList.Select(l => l.Name).ToList();
        LayoutBox.DropDownOpened += (_, _) => RescanLayouts();
        var chosen = LayoutCatalog.Find(settings.Layout, layoutList);
        settings.Layout = chosen.Name;
        LayoutBox.SelectedItem = chosen.Name;
        RefreshPaletteChoices(chosen.Console);
        ApplyLayout(chosen);
        ShowLayoutNote(chosen);
        if (layoutProblems.Count > 0) ShowLayoutStatus("Some layout files could not be used: " + string.Join(" ", layoutProblems), true);
        appearanceReady=true;
    }
    // The palette and accent lists follow the layout: the default layout's own set, or the console set.
    private void RefreshPaletteChoices(bool console)
    {
        var palettes = console ? Appearance.ConsolePalettes : Appearance.Palettes; var accents = console ? Appearance.ConsoleAccents : Appearance.Accents;
        string palette = console ? settings.ConsolePalette : settings.Palette, accent = console ? settings.ConsoleAccent : settings.AccentColor;
        PaletteBox.ItemsSource = palettes; AccentBox.ItemsSource = accents;
        PaletteBox.SelectedItem = palettes.Contains(palette) ? palette : palettes[0];
        AccentBox.SelectedItem = accents.Contains(accent) ? accent : accents[0];
    }
    private void Appearance_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!appearanceReady) return;
        if (PaletteBox.SelectedItem == null || AccentBox.SelectedItem == null) return;
        if (layout.Console)
        {
            settings.ConsolePalette=(string)PaletteBox.SelectedItem; settings.ConsoleAccent=(string)AccentBox.SelectedItem;
            ApplyConsoleColors(layout); ShowLayoutNote(layout);
        }
        else
        {
            settings.Palette=(string)PaletteBox.SelectedItem; settings.AccentColor=(string)AccentBox.SelectedItem;
            Appearance.Apply(settings.Palette,settings.AccentColor);
        }
        UpdateNavigation(); UpdateQuickAudio(); UpdateConsoleChrome();
        try { Storage.Save(settings); } catch(Exception ex) { Tell("Appearance changed but could not be saved: "+ex.Message,true); }
    }
}
