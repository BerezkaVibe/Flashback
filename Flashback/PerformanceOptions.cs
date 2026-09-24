namespace Flashback;

// Extras that can be switched off in Settings > Performance for older or busy PCs. Read wherever the
// work happens; set from the saved settings at startup and when settings are applied.
internal static class PerformanceOptions
{
    internal static bool Thumbnails = true, Waveforms = true, PreviewEffects = true, Animations = true;
    internal static void Apply(Settings s)
    {
        Thumbnails = s.ShowThumbnails; Waveforms = s.ShowWaveforms; PreviewEffects = s.PreviewEffects; Animations = s.UiAnimations;
    }
}
