using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Flashback;

// Decodes a clip's audio tracks and mixes the chosen ones, each at its own volume, into 48 kHz stereo float,
// from any moment: the whole mix (track 0), or desktop and microphone, or Other apps and each app's layer,
// as the export mixes them, with each track's cut-outs and volume parts. Only tracks that are on are decoded.
// Times are seconds from the start of the file.
internal sealed unsafe class FfmpegAudioMixer : IDisposable
{
    internal const int Rate = 48000, Channels = 2;
    private AVFormatContext* format;
    private readonly AVPacket* packet;
    private readonly AVFrame* frame;
    private readonly Track[] tracks;
    private readonly long origin;
    private bool ended;
    private double position;
    private float[] resampled = new float[8192 * Channels];
    internal int TrackCount => tracks.Length;
    internal double Duration { get; }
    // The moment the next mixed sample belongs to.
    internal double Position => position;
    // A stretch of a track at another volume: 0 for a cut-out. Overlapping parts multiply, as in the export.
    internal readonly record struct GainPart(double Start, double End, double Gain);

    private sealed class Track
    {
        internal int Stream;
        internal AVCodecContext* Codec;
        internal SwrContext* Resample;
        internal AVRational TimeBase;
        internal float Volume;
        internal bool On, Synced;
        internal GainPart[] Parts = Array.Empty<GainPart>();
        internal readonly SampleQueue Queue = new();
    }

    internal FfmpegAudioMixer(string path)
    {
        if (!FfmpegLibrary.Available) throw new InvalidOperationException("FFmpeg's libraries aren't available. " + FfmpegLibrary.Error);
        AVFormatContext* opened = null;
        FfmpegLibrary.Check(ffmpeg.avformat_open_input(&opened, path, null, null), "Couldn't open the audio");
        format = opened;
        FfmpegLibrary.Check(ffmpeg.avformat_find_stream_info(format, null), "Couldn't read the audio");
        origin = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time;
        Duration = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        var list = new List<Track>();
        for (int i = 0; i < format->nb_streams; i++)
        {
            var s = format->streams[i];
            if (s->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO) { s->discard = AVDiscard.AVDISCARD_ALL; continue; }
            var decoder = ffmpeg.avcodec_find_decoder(s->codecpar->codec_id);
            if (decoder == null) { s->discard = AVDiscard.AVDISCARD_ALL; continue; }
            var codec = ffmpeg.avcodec_alloc_context3(decoder);
            FfmpegLibrary.Check(ffmpeg.avcodec_parameters_to_context(codec, s->codecpar), "Couldn't set up an audio decoder");
            FfmpegLibrary.Check(ffmpeg.avcodec_open2(codec, decoder, null), "Couldn't start an audio decoder");
            SwrContext* swr = null; AVChannelLayout stereo; ffmpeg.av_channel_layout_default(&stereo, Channels);
            FfmpegLibrary.Check(ffmpeg.swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate, &codec->ch_layout, codec->sample_fmt, codec->sample_rate, 0, null), "Couldn't set up resampling");
            FfmpegLibrary.Check(ffmpeg.swr_init(swr), "Couldn't start resampling");
            list.Add(new Track { Stream = i, Codec = codec, Resample = swr, TimeBase = s->time_base });
        }
        tracks = list.ToArray();
        packet = ffmpeg.av_packet_alloc(); frame = ffmpeg.av_frame_alloc();
        if (tracks.Length > 0) { tracks[0].On = true; tracks[0].Volume = 1; }
    }

    // Which tracks play and how loud (1 as recorded, 0 off); tracks left out are off. Turning a track on or
    // off starts the mix again from where it is, so everything stays lined up.
    internal void SetVolumes(IReadOnlyDictionary<int, double> volumes)
    {
        bool changed = false;
        for (int t = 0; t < tracks.Length; t++)
        {
            float volume = volumes.TryGetValue(t, out var v) ? (float)Math.Clamp(v, 0, 2) : 0;
            bool on = volume > .0001f;
            if (on != tracks[t].On) changed = true;
            tracks[t].On = on; tracks[t].Volume = volume;
        }
        if (changed) Seek(position);
    }
    // Each track's cut-outs and volume parts (tracks left out have none). Takes effect from the next sound mixed.
    internal void SetGains(IReadOnlyDictionary<int, IReadOnlyList<GainPart>>? gains)
    {
        for (int t = 0; t < tracks.Length; t++)
            tracks[t].Parts = gains != null && gains.TryGetValue(t, out var parts) ? parts.Where(p => p.End > p.Start).ToArray() : Array.Empty<GainPart>();
    }
    internal void Seek(double seconds)
    {
        position = Math.Max(0, seconds);
        long target = (long)((position * ffmpeg.AV_TIME_BASE) + origin);
        ffmpeg.av_seek_frame(format, -1, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ended = false;
        foreach (var t in tracks) { ffmpeg.avcodec_flush_buffers(t.Codec); ffmpeg.swr_init(t.Resample); t.Queue.Clear(); t.Synced = false; }
    }
    // Mixes the next `frames` sample frames (stereo) into `buffer`; returns how many there were (fewer only at
    // the end of the clip).
    internal int Read(float[] buffer, int frames)
    {
        int left = (int)Math.Max(0, Math.Round((Duration - position) * Rate));
        if (Duration > 0) frames = Math.Min(frames, left);
        if (frames <= 0) return 0;
        var on = tracks.Where(t => t.On).ToArray();
        while (!ended && on.Any(t => t.Queue.Frames < frames)) ReadPacket();
        Array.Clear(buffer, 0, frames * Channels);
        foreach (var t in on) t.Queue.MixInto(buffer, frames, t.Volume, t.Parts, position);
        position += frames / (double)Rate;
        return frames;
    }
    private void ReadPacket()
    {
        int read = ffmpeg.av_read_frame(format, packet);
        if (read < 0)
        {
            ended = true;
            foreach (var t in tracks.Where(t => t.On)) { ffmpeg.avcodec_send_packet(t.Codec, null); Drain(t); }
            return;
        }
        var track = tracks.FirstOrDefault(t => t.Stream == packet->stream_index);
        if (track is { On: true } && ffmpeg.avcodec_send_packet(track.Codec, packet) >= 0) Drain(track);
        ffmpeg.av_packet_unref(packet);
    }
    private void Drain(Track t)
    {
        while (ffmpeg.avcodec_receive_frame(t.Codec, frame) == 0)
        {
            long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
            double at = pts == ffmpeg.AV_NOPTS_VALUE ? position : pts * ffmpeg.av_q2d(t.TimeBase) - origin / (double)ffmpeg.AV_TIME_BASE;
            int room = ffmpeg.swr_get_out_samples(t.Resample, frame->nb_samples);
            if (resampled.Length < room * Channels) resampled = new float[room * Channels * 2];
            int got;
            fixed (float* output = resampled)
            {
                byte* o = (byte*)output;
                got = ffmpeg.swr_convert(t.Resample, &o, room, frame->extended_data, frame->nb_samples);
            }
            ffmpeg.av_frame_unref(frame);
            if (got <= 0) continue;
            // The first sound after a seek is lined up with where the mix is: silence before it, or its start
            // dropped if it began earlier.
            int skip = 0;
            if (!t.Synced)
            {
                double queueEnd = position + t.Queue.Frames / (double)Rate;
                int offset = (int)Math.Round((at - queueEnd) * Rate);
                if (offset > 0) t.Queue.AppendSilence(offset); else skip = Math.Min(got, -offset);
                if (skip >= got) continue;
                t.Synced = true;
            }
            t.Queue.Append(resampled, skip, got - skip);
        }
    }
    public void Dispose()
    {
        foreach (var t in tracks)
        {
            var codec = t.Codec; ffmpeg.avcodec_free_context(&codec);
            var swr = t.Resample; ffmpeg.swr_free(&swr);
        }
        var f = frame; ffmpeg.av_frame_free(&f);
        var p = packet; ffmpeg.av_packet_free(&p);
        fixed (AVFormatContext** fc = &format) ffmpeg.avformat_close_input(fc);
    }

    // Interleaved stereo samples waiting to be mixed, oldest first.
    private sealed class SampleQueue
    {
        private float[] data = new float[Rate * Channels];
        private int start, count; // in floats
        internal int Frames => count / Channels;
        internal void Clear() { start = 0; count = 0; }
        private void Room(int floats)
        {
            if (start + count + floats <= data.Length) return;
            if (count + floats <= data.Length) { Array.Copy(data, start, data, 0, count); start = 0; return; }
            var bigger = new float[Math.Max(data.Length * 2, count + floats)];
            Array.Copy(data, start, bigger, 0, count); data = bigger; start = 0;
        }
        internal void Append(float[] source, int fromFrame, int frames)
        {
            Room(frames * Channels);
            Array.Copy(source, fromFrame * Channels, data, start + count, frames * Channels); count += frames * Channels;
        }
        internal void AppendSilence(int frames) { Room(frames * Channels); Array.Clear(data, start + count, frames * Channels); count += frames * Channels; }
        // Adds up to `frames` of it to the buffer at a volume, with the track's parts (the first sample being at
        // `from` seconds), and removes that much (short: silence for the rest).
        internal void MixInto(float[] buffer, int frames, float volume, GainPart[] parts, double from)
        {
            int available = Math.Min(frames, count / Channels);
            for (int i = 0; i < available;)
            {
                var (gain, until) = GainAt(parts, from + i / (double)Rate);
                int end = double.IsPositiveInfinity(until) ? available : Math.Clamp((int)Math.Ceiling((until - from) * Rate - 1e-6), i + 1, available);
                float level = volume * gain;
                if (level != 0) for (int k = i * Channels; k < end * Channels; k++) buffer[k] += data[start + k] * level;
                i = end;
            }
            start += available * Channels; count -= available * Channels;
            if (count == 0) start = 0;
        }
        // The gain at a moment, and when it next changes.
        private static (float Gain, double Until) GainAt(GainPart[] parts, double at)
        {
            float gain = 1; double until = double.PositiveInfinity;
            foreach (var p in parts)
            {
                if (p.Start > at) until = Math.Min(until, p.Start);
                else if (p.End > at) { gain *= (float)p.Gain; until = Math.Min(until, p.End); }
            }
            return (gain, until);
        }
    }
}
