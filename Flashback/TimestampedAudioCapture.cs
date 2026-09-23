using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback;

internal sealed class AudioPacketEventArgs : EventArgs
{
    internal byte[] Buffer { get; }
    internal int BytesRecorded => Buffer.Length;
    internal double TimestampSeconds { get; }
    internal AudioPacketEventArgs(byte[] buffer, double timestamp) { Buffer = buffer; TimestampSeconds = timestamp; }
}

// WASAPI's packet timestamp, rather than callback arrival time, defines the audio clock.
internal sealed class TimestampedAudioCapture : IDisposable
{
    private readonly AudioClient client;
    private readonly AudioCaptureClient packets;
    private readonly AutoResetEvent ready = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly int blockAlign;
    private Task? worker;
    internal event EventHandler<AudioPacketEventArgs>? DataAvailable;
    internal event EventHandler<StoppedEventArgs>? RecordingStopped;
    internal TimestampedAudioCapture(MMDevice device, bool microphone, WaveFormat format)
    {
        client = device.AudioClient; blockAlign = format.BlockAlign;
        try
        {
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback | (microphone ? 0 : AudioClientStreamFlags.Loopback), 1000000, 0, format, Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            packets = client.AudioCaptureClient;
        }
        catch { client.Dispose(); ready.Dispose(); stop.Dispose(); throw; }
    }
    internal void StartRecording() => worker = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private void Read()
    {
        Exception? error = null;
        try
        {
            client.Start();
            while (!stop.IsCancellationRequested)
            {
                ready.WaitOne(100);
                while (!stop.IsCancellationRequested && packets.GetNextPacketSize() > 0)
                {
                    var data = packets.GetBuffer(out int frames, out var flags, out long _, out long qpc);
                    try
                    {
                        var bytes = new byte[frames * blockAlign];
                        if (((int)flags & 2) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                        double timestamp = ((int)flags & 4) == 0 ? qpc / 10000000.0 : double.NaN;
                        DataAvailable?.Invoke(this, new AudioPacketEventArgs(bytes, timestamp));
                    }
                    finally { packets.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) error = ex; }
        finally { try { client.Stop(); } catch { } RecordingStopped?.Invoke(this, new StoppedEventArgs(error)); }
    }
    internal void StopRecording() { stop.Cancel(); ready.Set(); worker?.GetAwaiter().GetResult(); }
    public void Dispose() { StopRecording(); client.Dispose(); ready.Dispose(); stop.Dispose(); }
}
