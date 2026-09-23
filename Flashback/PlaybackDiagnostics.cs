using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback;
internal static class PlaybackDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); File.AppendAllText(Path.Combine(Storage.Root, "playback-results.txt"), "PASS " + text + "\n"); }
        using var devices = new MMDeviceEnumerator();
        using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var tone = new WasapiOut(output, AudioClientShareMode.Shared, false, 100);
        tone.Init(new Tone());
        await using var capture = new AudioLoopback(output.ID);
        await using var pipe = new NamedPipeClientStream(".", capture.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        capture.Start(); await pipe.ConnectAsync(timeout.Token); tone.Play();
        async Task<bool> Samples(double seconds)
        {
            int total = 0; bool signal = false; var bytes = new byte[16384];
            while (total < capture.Format.AverageBytesPerSecond * seconds)
            {
                int count = await pipe.ReadAsync(bytes, timeout.Token);
                if (count == 0) throw new IOException("Playback pipe closed");
                signal |= bytes.Take(count).Any(b => b != 0); total += count;
            }
            return signal;
        }
        try
        {
            await Samples(.5);
            Check(await Samples(1), "Pinned headphone/output device receives actual playback: " + output.FriendlyName);
            capture.Muted = true; await Samples(1);
            Check(!await Samples(1), "Live quick mute outputs silence without stopping the audio stream");
            capture.Muted = false; await Samples(.5);
            Check(await Samples(1) && capture.Failure == null, "Live unmute restores playback on the same audio pipe");
            Check(!capture.DeviceChanged && !capture.StreamStalled, "Pinned output remains connected independent of any selected capture display");
        }
        finally { tone.Stop(); }
    }
    private sealed class Tone : IWaveProvider
    {
        private long frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i + 7 < count; i += 8)
            {
                var bytes = BitConverter.GetBytes((float)(.03 * Math.Sin(2 * Math.PI * 440 * frame++ / 48000)));
                Buffer.BlockCopy(bytes, 0, buffer, offset + i, 4); Buffer.BlockCopy(bytes, 0, buffer, offset + i + 4, 4);
            }
            return count;
        }
    }
}
