using System;
using System.IO;
using FFmpeg.AutoGen;

namespace Flashback;

// Reads a clip's video frames in order, and jumps to the exact frame at any moment. With a hardware device
// (Direct3D 11 on Windows) frames are decoded on the graphics card and stay there as textures; without one
// they're decoded on the CPU. Times are seconds from the start of the file, like the editor's playhead.
// Frames handed out belong to the reader until the next call; clone them (av_frame_clone) to keep one.
internal sealed unsafe class FfmpegVideoReader : IDisposable
{
    private AVFormatContext* format;
    private AVCodecContext* codec;
    private readonly AVPacket* packet;
    private AVFrame* current, pending;
    private readonly int stream;
    private readonly AVRational timeBase;
    private readonly long origin;
    private bool ended, hasPending;
    internal double Duration { get; }
    internal double FrameRate { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal bool Hardware { get; }
    internal AVFrame* Frame => current;
    internal double FrameTime { get; private set; } = -1;

    internal FfmpegVideoReader(string path, AVBufferRef* hardwareDevice = null)
    {
        if (!FfmpegLibrary.Available) throw new InvalidOperationException("FFmpeg's libraries aren't available. " + FfmpegLibrary.Error);
        AVFormatContext* opened = null;
        FfmpegLibrary.Check(ffmpeg.avformat_open_input(&opened, path, null, null), "Couldn't open the video");
        format = opened;
        FfmpegLibrary.Check(ffmpeg.avformat_find_stream_info(format, null), "Couldn't read the video");
        AVCodec* decoder = null;
        stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
        if (stream < 0 || decoder == null) throw new IOException("The file has no video to show.");
        var s = format->streams[stream];
        // Only this stream is read.
        for (int i = 0; i < format->nb_streams; i++) if (i != stream) format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        timeBase = s->time_base;
        origin = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time;
        codec = ffmpeg.avcodec_alloc_context3(decoder);
        FfmpegLibrary.Check(ffmpeg.avcodec_parameters_to_context(codec, s->codecpar), "Couldn't set up the video decoder");
        if (hardwareDevice != null)
        {
            codec->hw_device_ctx = ffmpeg.av_buffer_ref(hardwareDevice);
            // Room for the frames the player holds on to (the one on screen and a few ahead).
            codec->extra_hw_frames = 6;
            Hardware = true;
        }
        else codec->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        FfmpegLibrary.Check(ffmpeg.avcodec_open2(codec, decoder, null), "Couldn't start the video decoder");
        packet = ffmpeg.av_packet_alloc(); current = ffmpeg.av_frame_alloc(); pending = ffmpeg.av_frame_alloc();
        Width = codec->width; Height = codec->height;
        var rate = s->avg_frame_rate.num > 0 ? s->avg_frame_rate : s->r_frame_rate;
        FrameRate = rate.num > 0 && rate.den > 0 ? rate.num / (double)rate.den : 60;
        Duration = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : s->duration * ffmpeg.av_q2d(timeBase);
    }
    private double TimeOf(AVFrame* frame)
    {
        long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE) return FrameTime >= 0 ? FrameTime + 1 / FrameRate : 0;
        return pts * ffmpeg.av_q2d(timeBase) - origin / (double)ffmpeg.AV_TIME_BASE;
    }
    // The next frame into `into`; false at the end.
    private bool Decode(AVFrame* into)
    {
        while (true)
        {
            int got = ffmpeg.avcodec_receive_frame(codec, into);
            if (got == 0) return true;
            if (got == ffmpeg.AVERROR_EOF) return false;
            if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN)) FfmpegLibrary.Check(got, "Couldn't decode the video");
            if (ended) return false;
            int read = ffmpeg.av_read_frame(format, packet);
            if (read < 0) { ended = true; ffmpeg.avcodec_send_packet(codec, null); continue; }
            if (packet->stream_index == stream) ffmpeg.avcodec_send_packet(codec, packet);
            ffmpeg.av_packet_unref(packet);
        }
    }
    // The frame after the current one; false at the end of the clip.
    internal bool Next()
    {
        if (hasPending)
        {
            ffmpeg.av_frame_unref(current); ffmpeg.av_frame_move_ref(current, pending); hasPending = false;
            FrameTime = TimeOf(current); return true;
        }
        ffmpeg.av_frame_unref(current);
        if (!Decode(current)) return false;
        FrameTime = TimeOf(current);
        return true;
    }
    // The frame showing at a moment (the last one starting at or before it, give or take half a frame): back
    // to the keyframe before it, then decoded forward. Just ahead in the same stretch it decodes on instead,
    // which is what playback and small scrubs do. Recordings have a keyframe every 2 seconds.
    internal bool Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration));
        double half = .5 / FrameRate;
        bool onward = FrameTime >= 0 && seconds >= FrameTime - half && seconds - FrameTime < 1;
        if (!onward)
        {
            long target = (long)Math.Floor((seconds + origin / (double)ffmpeg.AV_TIME_BASE) / ffmpeg.av_q2d(timeBase));
            FfmpegLibrary.Check(ffmpeg.av_seek_frame(format, stream, target, ffmpeg.AVSEEK_FLAG_BACKWARD), "Couldn't seek the video");
            ffmpeg.avcodec_flush_buffers(codec);
            ended = false; hasPending = false; ffmpeg.av_frame_unref(pending); ffmpeg.av_frame_unref(current); FrameTime = -1;
        }
        while (true)
        {
            if (!hasPending) { if (!Decode(pending)) return FrameTime >= 0; hasPending = true; }
            double time = TimeOf(pending);
            // The next frame starts after the moment: the current one is it (the next waits for Next()).
            if (time > seconds + half && FrameTime >= 0) return true;
            ffmpeg.av_frame_unref(current); ffmpeg.av_frame_move_ref(current, pending); hasPending = false; FrameTime = time;
        }
    }
    public void Dispose()
    {
        fixed (AVFrame** c = &current) ffmpeg.av_frame_free(c);
        fixed (AVFrame** p = &pending) ffmpeg.av_frame_free(p);
        var pk = packet; ffmpeg.av_packet_free(&pk);
        fixed (AVCodecContext** c = &codec) ffmpeg.avcodec_free_context(c);
        fixed (AVFormatContext** f = &format) ffmpeg.avformat_close_input(f);
    }
}
