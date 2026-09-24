using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Flashback;

// Settings > Performance: Lighter mode switches every extra off at once; the individual switches
// stay available, and Lighter mode shows as on only while all of them are off.
public partial class MainWindow
{
    private CheckBox[] PerformanceExtras => new[] { ThumbnailsCheck, WaveformsCheck, PreviewEffectsCheck, AnimationsCheck };
    private void LighterMode_Click(object sender, RoutedEventArgs e)
    {
        bool lighter = LighterModeCheck.IsChecked == true;
        foreach (var box in PerformanceExtras) box.IsChecked = !lighter;
        Tell(lighter ? "Lighter mode: extras off. Click Apply settings to use it." : "Extras back on. Click Apply settings to use them.");
    }
    private void PerformanceToggle_Click(object sender, RoutedEventArgs e) => SyncLighterMode();
    private void SyncLighterMode() => LighterModeCheck.IsChecked = PerformanceExtras.All(b => b.IsChecked == false);
    // Fills in the recommendation without applying it; the user confirms with Apply settings.
    private void RecommendedVideo_Click(object sender, RoutedEventArgs e)
    {
        ResolutionBox.SelectedValue = 720; FpsBox.SelectedValue = 30;
        Tell("720p at 30 FPS is set under Video. Click Apply settings to record with it.");
    }
}
