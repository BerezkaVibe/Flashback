using System;
using NAudio.Wave;

namespace Flashback;

// A desktop audio source the recorder can pipe into FFmpeg: either the whole
// playback device (AudioLoopback) or per-app streams mixed with levels (AppMixSource).
public interface IRecordingAudio : IAsyncDisposable
{
    WaveFormat Format { get; }
    string RawFormat { get; }
    string InputPath { get; }
    string DeviceName { get; }
    string DeviceId { get; }
    bool Muted { get; set; }
    double Gain { get; set; }
    bool Hold { get; set; }
    bool DeviceChanged { get; }
    bool StreamStalled { get; }
    Exception? Failure { get; }
    string SyncReport { get; }
    void Start(Func<long>? timelineOrigin = null);
}
