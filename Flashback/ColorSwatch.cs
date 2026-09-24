using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flashback;

// A small color button. Clicking it opens a palette with a hex field and, where see-through colors
// make sense, an opacity slider.
internal sealed class ColorSwatch : Button
{
    internal static readonly string[] Palette =
    {
        "#FFFFFF", "#000000", "#FFE600", "#FF9500", "#FF3B30", "#FF2D55", "#BF5AF2", "#5E5CE6",
        "#0A84FF", "#64D2FF", "#00C7BE", "#34C759", "#A2845E", "#8E8E93", "#3A3A3C", "#F2F2F7",
    };
    private readonly Border chip = new() { Width = 22, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)) };
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
    private readonly TextBox hex = new() { Width = 96, Height = 28, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Slider alpha = new() { Minimum = 0, Maximum = 255, Width = 150, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private bool loading;
    private readonly bool withAlpha;
    internal event Action<string>? Changed;
    internal string Value
    {
        get => color;
        set
        {
            color = value; loading = true;
            var c = OverlayRenderer.ParseColor(value, Colors.White);
            chip.Background = new SolidColorBrush(c);
            hex.Text = withAlpha && c.A < 255 ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            alpha.Value = c.A; loading = false;
        }
    }
    private string color = "#FFFFFF";

    internal ColorSwatch(bool withAlpha = false)
    {
        this.withAlpha = withAlpha;
        SetResourceReference(StyleProperty, "IconButton");
        Width = 34; Height = 26; MinHeight = 26; Padding = new Thickness(0);
        Content = chip; ToolTip = "Choose a color";
        popup.PlacementTarget = this;
        var grid = new UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var swatch in Palette)
        {
            var c = OverlayRenderer.ParseColor(swatch, Colors.White);
            var b = new Button { Width = 22, Height = 22, MinHeight = 22, Margin = new Thickness(2), Padding = new Thickness(0), ToolTip = swatch, Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)) };
            b.Click += (_, _) => Pick(withAlpha ? Color.FromArgb((byte)alpha.Value, c.R, c.G, c.B) : c);
            grid.Children.Add(b);
        }
        var panel = new StackPanel();
        panel.Children.Add(grid);
        var hexRow = new DockPanel();
        hexRow.Children.Add(new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), FontSize = 11 });
        hexRow.Children.Add(hex);
        panel.Children.Add(hexRow);
        if (withAlpha)
        {
            var alphaRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            alphaRow.Children.Add(new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), FontSize = 11 });
            alphaRow.Children.Add(alpha);
            panel.Children.Add(alphaRow);
        }
        var border = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Outline");
        popup.Child = border;
        Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        hex.TextChanged += (_, _) =>
        {
            if (loading) return;
            var text = hex.Text.Trim(); if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length is 7 or 9 && OverlayRenderer.ParseColor(text, Colors.Transparent) is var c && (c != Colors.Transparent || text.Length == 9))
            {
                color = text; chip.Background = new SolidColorBrush(c); loading = true; alpha.Value = c.A; loading = false;
                Changed?.Invoke(color);
            }
        };
        alpha.ValueChanged += (_, _) =>
        {
            if (loading) return;
            var c = OverlayRenderer.ParseColor(color, Colors.White);
            Pick(Color.FromArgb((byte)Math.Round(alpha.Value), c.R, c.G, c.B));
        };
    }
    private void Pick(Color c)
    {
        Value = withAlpha && c.A < 255 ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Changed?.Invoke(color);
    }
}
