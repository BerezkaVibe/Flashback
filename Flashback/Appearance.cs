using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flashback;

internal static class Appearance
{
    internal static readonly string[] Palettes = { "Charcoal", "Midnight", "Slate" };
    internal static readonly string[] Accents = { "Mint", "Blue", "Lavender", "Rose", "Amber" };
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
        ToggleButton.SetResourceReference(Control.BackgroundProperty,recording ? "RecordingRed" : "Accent");
        ToggleButton.SetResourceReference(Control.BorderBrushProperty,recording ? "RecordingRed" : "Accent");
        ToggleButton.SetResourceReference(Control.ForegroundProperty,recording ? "Ink" : "OnAccent");
    }
    private bool appearanceReady;
    private void LoadAppearance()
    {
        appearanceReady=false;
        PaletteBox.ItemsSource=Appearance.Palettes; AccentBox.ItemsSource=Appearance.Accents;
        PaletteBox.SelectedItem=Appearance.Palettes.Contains(settings.Palette) ? settings.Palette : "Charcoal";
        AccentBox.SelectedItem=Appearance.Accents.Contains(settings.AccentColor) ? settings.AccentColor : "Mint";
        Appearance.Apply((string)PaletteBox.SelectedItem,(string)AccentBox.SelectedItem); appearanceReady=true;
    }
    private void Appearance_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!appearanceReady) return;
        settings.Palette=(string)PaletteBox.SelectedItem; settings.AccentColor=(string)AccentBox.SelectedItem;
        Appearance.Apply(settings.Palette,settings.AccentColor); UpdateNavigation(); UpdateQuickAudio();
        try { Storage.Save(settings); } catch(Exception ex) { Tell("Appearance changed but could not be saved: "+ex.Message,true); }
    }
}


