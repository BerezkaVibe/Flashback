using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Flashback;

// A yes/no prompt drawn with the app's palette instead of the stock Windows message box.
internal static class ThemedDialog
{
    internal static bool Confirm(Window owner, string title, string message, string confirm, string cancel = "Cancel")
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var body = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var no = new Button { Content = cancel, IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 8, 14, 8) };
        var yes = new Button { Content = confirm, IsDefault = true, MinWidth = 90, Padding = new Thickness(14, 8, 14, 8), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(no); buttons.Children.Add(yes);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(heading); panel.Children.Add(body); panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = owner, Title = title, Content = panel, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = false, FontFamily = owner.FontFamily
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "Surface");
        WindowTheme.Attach(dialog);
        yes.Click += (_, _) => dialog.DialogResult = true;
        // Focus the safe choice so a stray Enter never deletes anything.
        dialog.Loaded += (_, _) => Keyboard.Focus(no);
        return dialog.ShowDialog() == true;
    }
    // Three ways out: the main choice (true), the other one (false), or Cancel / closing the dialog (null).
    internal static bool? Choose(Window owner, string title, string message, string confirm, string other)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var body = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 8, 14, 8) };
        var second = new Button { Content = other, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 8, 14, 8) };
        var first = new Button { Content = confirm, IsDefault = true, MinWidth = 90, Padding = new Thickness(14, 8, 14, 8), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(second); buttons.Children.Add(first);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(heading); panel.Children.Add(body); panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = owner, Title = title, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = false, FontFamily = owner.FontFamily
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "Surface");
        WindowTheme.Attach(dialog);
        bool? choice = null;
        first.Click += (_, _) => { choice = true; dialog.DialogResult = true; };
        second.Click += (_, _) => { choice = false; dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => Keyboard.Focus(first);
        dialog.ShowDialog();
        return choice;
    }
}
