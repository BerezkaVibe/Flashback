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
    // Graphics-card decoding: one pool of frames (a Direct3D 11 texture array) for the whole clip. Left to
    // itself the decoder makes a new pool at every keyframe seek, which made each seek allocate ~80 MB of
    // graphics memory, and let the pools pile up while anything still held one of their frames.
    private AVBufferRef* pool;
    private (int Width, int Height, AVPixelFormat Format) poolShape;
    private readonly AVCodecContext_get_format? pickFormat; // kept alive while the decoder can call it
    // Frames pools made for this clip (1 unless the video changes size partway).
    internal int PoolsMade { get; private set; }
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
            // Room for the frames the player holds on to (see ExtraFrames).
            codec->extra_hw_frames = ExtraFrames;
            pickFormat = PickFormat;
            codec->get_format = pickFormat;
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
    // The frames the player holds on to beyond the decoder's own: up to 12 recent ones (FfmpegRecentFrames,
    // which the few queued ahead are among), the one on screen, a paused seek's and the reader's next.
    private const int ExtraFrames = 16;
    // The decoder asks which picture format to decode to (at the start, and again after each keyframe seek):
    // graphics-card frames from the clip's one pool, made the first time and handed back every time after.
    private AVPixelFormat PickFormat(AVCodecContext* context, AVPixelFormat* offered)
    {
        try
        {
            for (var f = offered; *f != AVPixelFormat.AV_PIX_FMT_NONE; f++)
            {
                if (*f != AVPixelFormat.AV_PIX_FMT_D3D11) continue;
                var shape = (context->coded_width, context->coded_height, context->sw_pix_fmt);
                if (pool == null || shape != poolShape)
                {
                    AVBufferRef* made = null;
                    if (ffmpeg.avcodec_get_hw_frames_parameters(context, context->hw_device_ctx, AVPixelFormat.AV_PIX_FMT_D3D11, &made) < 0) return *f;
                    var frames = (AVHWFramesContext*)made->data;
                    if (frames->initial_pool_size > 0) frames->initial_pool_size += ExtraFrames;
                    if (ffmpeg.av_hwframe_ctx_init(made) < 0) { ffmpeg.av_buffer_unref(&made); return *f; }
                    if (pool != null) { var old = pool; ffmpeg.av_buffer_unref(&old); }
                    pool = made; poolShape = shape; PoolsMade++;
                }
                // (libavcodec lets go of the last reference before asking; this makes sure.)
                if (context->hw_frames_ctx != null) ffmpeg.av_buffer_unref(&context->hw_frames_ctx);
                context->hw_frames_ctx = ffmpeg.av_buffer_ref(pool);
                return *f;
            }
            // No graphics-card format for this video: the first one decoded on the CPU.
            for (var f = offered; *f != AVPixelFormat.AV_PIX_FMT_NONE; f++)
                if ((ffmpeg.av_pix_fmt_desc_get(*f)->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0) return *f;
        }
        catch { }
        return *offered;
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
    // `cancel` is asked before each frame decoded on the way: true gives up (false back), leaving the reader
    // where it got to, so a scrub's next seek carries on from there instead of waiting for this one.
    internal bool Seek(double seconds, Func<bool>? cancel = null)
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
            if (cancel?.Invoke() == true) return false;
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
        if (pool != null) { var p = pool; ffmpeg.av_buffer_unref(&p); pool = null; }
        fixed (AVFormatContext** f = &format) ffmpeg.avformat_close_input(f);
    }
}
