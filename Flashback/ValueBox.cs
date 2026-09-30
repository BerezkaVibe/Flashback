using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// A slider's value that can be typed: it reads like the label it replaces ("150%", "2.5×", "1.0 s") and
// looks like one until pointed at, then click, type a number and press Enter (or click away). Units are
// optional, "mute" or "off" mean 0, and the number is kept within Minimum and Maximum. Escape puts it back.
internal sealed class ValueBox : TextBox
{
    public double Minimum { get; set; }
    public double Maximum { get; set; } = double.MaxValue;
    internal event Action<double>? Committed;
    private string shown = "";

    public ValueBox()
    {
        MinWidth = 44; Padding = new Thickness(2, 0, 2, 0); FontSize = 11; TextAlignment = TextAlignment.Right; VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center; Background = Brushes.Transparent; BorderThickness = new Thickness(1); BorderBrush = Brushes.Transparent;
        Cursor = Cursors.IBeam; ToolTip = "Click to type an exact value";
        SetResourceReference(ForegroundProperty, "Accent"); SetResourceReference(CaretBrushProperty, "Ink");
        MouseEnter += (_, _) => Outline(); MouseLeave += (_, _) => Outline();
        GotKeyboardFocus += (_, _) => { shown = Text; Outline(); Dispatcher.BeginInvoke(SelectAll); };
        LostKeyboardFocus += (_, _) => { Commit(); Outline(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); shown = Text; SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Text = shown; SelectAll(); e.Handled = true; }
        };
        // A click selects the whole value, ready to type over.
        PreviewMouseLeftButtonDown += (_, e) => { if (!IsKeyboardFocusWithin) { Focus(); e.Handled = true; } };
    }
    private void Outline()
    {
        if (IsKeyboardFocusWithin) { SetResourceReference(BorderBrushProperty, "Accent"); SetResourceReference(BackgroundProperty, "ControlSurface"); }
        else if (IsMouseOver) { SetResourceReference(BorderBrushProperty, "Outline"); Background = Brushes.Transparent; }
        else { BorderBrush = Brushes.Transparent; Background = Brushes.Transparent; }
    }
    // For tests: type a value and press Enter.
    internal void Type(string text) { Text = text; Commit(); }
    private void Commit()
    {
        if (Text == shown) return;
        if (Parse(Text) is double value) { shown = Text; Committed?.Invoke(Math.Clamp(value, Minimum, Maximum)); }
        else Text = shown;
    }
    // A box for a slider: shows format(slider value), and a typed value (in the units it shows, turned into the
    // slider's with toSlider) moves the slider exactly there, even between its ticks.
    internal static ValueBox For(System.Windows.Controls.Slider slider, Func<double, string> format, Func<double, double>? toSlider = null, double? typedMin = null, double? typedMax = null)
    {
        var box = new ValueBox { Minimum = typedMin ?? slider.Minimum, Maximum = typedMax ?? slider.Maximum, Text = format(slider.Value) };
        box.Committed += v =>
        {
            bool snap = slider.IsSnapToTickEnabled; slider.IsSnapToTickEnabled = false;
            slider.Value = Math.Clamp(toSlider?.Invoke(v) ?? v, slider.Minimum, slider.Maximum);
            slider.IsSnapToTickEnabled = snap;
            box.Text = format(slider.Value);
        };
        return box;
    }
    // How a slider's shown number relates to its value when it's a plain scale: 100 for a 0-1 slider shown in
    // percent, otherwise 1. Read from the label itself, halfway-ish along the slider.
    internal static double ShownScale(double min, double max, Func<double, string> format)
    {
        double t = min + (max - min) * .37;
        if (Math.Abs(t) < 1e-9 || Parse(format(t)) is not double shown || Math.Abs(shown) < 1e-9) return 1;
        return Math.Abs(Math.Log10(Math.Abs(shown / t)) - 2) < 1 ? 100 : 1;
    }

    // The first number in the text (a comma works as the decimal point too); "mute"/"off" are 0.
    internal static double? Parse(string text)
    {
        var m = Regex.Match(text, @"-?\d+(?:[.,]\d+)?|-?[.,]\d+");
        if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
        return Regex.IsMatch(text, @"\b(mute|muted|off|silent)\b", RegexOptions.IgnoreCase) ? 0 : null;
    }
}
