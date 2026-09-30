using System;
using System.IO;
using System.Linq;
using System.Media;

namespace Flashback;

// The small cue that plays when a clip has saved. The sounds are made here from plain tones, so there are no
// sound files to ship, and each is short and soft so it sits under a game instead of over it.
public static class SaveSound
{
    public const string Off = "Off";
    public static readonly string[] Names = { "Hop high", "Hop", "Twitter", "Bird", "Pip", "Drop", "Ping", "Uplift", "Glass", "Chime", "Blip", "Coin", "Sparkle", "Bubble", "Click", "Shutter", "Double tap" };
    public const string Default = "Hop high";
    public static readonly string[] Choices = new[] { Off }.Concat(Names).ToArray();
    private const int Rate = 44100;
    private static SoundPlayer? playing;

    public static bool IsValid(string? name) => name != null && Choices.Contains(name);

    // Plays on its own thread and returns at once. A sound that can't play is never a reason to fail a save.
    public static void Play(string name, int volume)
    {
        if (name == Off || !IsValid(name)) return;
        try
        {
            var player = new SoundPlayer(new MemoryStream(Wave(name, volume)));
            playing = player;
            player.Play();
        }
        catch { /* Optional feedback. */ }
    }

    internal static byte[] Wave(string name, int volume)
    {
        double level = Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 2) * 0.45; // squared, so the low end has fine steps
        var samples = new double[(int)(Rate * 0.6)];
        int used = 0;
        void Tone(double start, double seconds, double from, double to, double gain, double decay, bool bell = true)
        {
            int first = (int)(start * Rate), count = (int)(seconds * Rate);
            double phase = 0;
            for (int i = 0; i < count && first + i < samples.Length; i++)
            {
                double t = (double)i / count, time = (double)i / Rate;
                double freq = from + (to - from) * t;
                phase += 2 * Math.PI * freq / Rate;
                double envelope = Math.Min(1, time / 0.004) * Math.Exp(-decay * time) * Math.Min(1, (seconds - time) / 0.01);
                double v = Math.Sin(phase) + (bell ? 0.25 * Math.Sin(phase * 2.0) + 0.08 * Math.Sin(phase * 3.0) : 0);
                samples[first + i] += v * envelope * gain;
                used = Math.Max(used, first + i + 1);
            }
        }
        var noise = new Random(7);
        void Click(double start, double seconds, double gain, double tone)
        {
            int first = (int)(start * Rate), count = (int)(seconds * Rate);
            double low = 0;
            for (int i = 0; i < count && first + i < samples.Length; i++)
            {
                double time = (double)i / Rate;
                low += 0.35 * ((noise.NextDouble() * 2 - 1) - low); // a little smoothing keeps it from hissing
                samples[first + i] += (low * 0.8 + Math.Sin(2 * Math.PI * tone * time) * 0.5) * Math.Exp(-time * 160) * gain;
                used = Math.Max(used, first + i + 1);
            }
        }
        switch (name)
        {
            case "Twitter": Tone(0, 0.045, 2600, 3300, 0.55, 25, false); Tone(0.06, 0.045, 3000, 3700, 0.55, 25, false); Tone(0.12, 0.06, 2800, 4200, 0.55, 22, false); break;
            case "Hop": Tone(0, 0.06, 500, 1100, 0.7, 20, false); Tone(0.08, 0.06, 500, 1100, 0.7, 20, false); break;
            case "Bird": Tone(0, 0.07, 2200, 3400, 0.6, 20, false); Tone(0.09, 0.09, 2400, 3900, 0.6, 18, false); break;
            case "Pip": Tone(0, 0.05, 2600, 2600, 0.7, 30, false); break;
            case "Drop": Tone(0, 0.1, 1900, 950, 0.7, 16, false); break;
            case "Ping": Tone(0, 0.4, 1568, 1568, 0.6, 11, false); break;
            case "Uplift": Tone(0, 0.07, 660, 660, 0.6, 14, false); Tone(0.06, 0.16, 990, 990, 0.6, 12, false); break;
            case "Glass": Tone(0, 0.35, 3136, 3136, 0.4, 14); Tone(0.05, 0.3, 4186, 4186, 0.3, 16); break;
            case "Chime": Tone(0, 0.45, 1318.5, 1318.5, 0.5, 9); Tone(0.09, 0.5, 1975.5, 1975.5, 0.45, 8); break;
            case "Blip": Tone(0, 0.11, 880, 1320, 0.7, 14, false); break;
            case "Coin": Tone(0, 0.07, 987.8, 987.8, 0.6, 6, false); Tone(0.07, 0.4, 1318.5, 1318.5, 0.6, 9, false); break;
            case "Sparkle": Tone(0, 0.2, 1568, 1568, 0.4, 14); Tone(0.06, 0.2, 2093, 2093, 0.4, 14); Tone(0.12, 0.3, 2637, 2637, 0.4, 11); break;
            case "Bubble": Tone(0, 0.12, 300, 700, 0.8, 18, false); break;
            case "Click": Click(0, 0.04, 1.0, 2400); break;
            case "Shutter": Click(0, 0.035, 0.9, 1800); Click(0.055, 0.05, 0.7, 1200); break;
            case "Double tap": Tone(0, 0.06, 1760, 1760, 0.6, 30, false); Tone(0.1, 0.08, 1760, 1760, 0.6, 26, false); break;
            default: Tone(0, 0.055, 800, 1700, 0.65, 22, false); Tone(0.075, 0.055, 800, 1700, 0.65, 22, false); break; // Hop high
        }
        used = Math.Min(samples.Length, used + Rate / 50);
        int bytes = used * 2;
        var stream = new MemoryStream(44 + bytes);
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes);
            for (int i = 0; i < used; i++) w.Write((short)(Math.Clamp(samples[i] * level, -1, 1) * 32000));
        }
        return stream.ToArray();
    }
}
