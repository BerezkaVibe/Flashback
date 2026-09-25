using System;
using FFmpeg.AutoGen;

namespace Flashback;

// Plays the mix faster or slower with its pitch kept, through FFmpeg's atempo filter (what the export uses
// for speed parts). At 1× it passes the mix straight through.
// A stretch plays at one speed up to a moment (the next speed part's edge): reading stops there once
// everything given to the filter has come out, so the next stretch starts exactly where this one ended.
internal sealed unsafe class FfmpegTempo : IDisposable
{
    private const int Chunk = 1024, Channels = FfmpegAudioMixer.Channels;
    private readonly FfmpegAudioMixer mixer;
    private AVFilterGraph* graph;
    private AVFilterContext* source, sink;
    private readonly AVFrame* input, output;
    private readonly float[] chunk = new float[Chunk * Channels];
    private float[] spare = new float[Chunk * Channels * 8];
    private int spareStart, spareCount; // in floats
    private bool drained;
    private long pts;
    internal double Speed { get; private set; } = 1;
    // Sound already made at the speed before, still to be read.
    internal int PendingFrames => spareCount / Channels;

    internal FfmpegTempo(FfmpegAudioMixer mixer) { this.mixer = mixer; input = ffmpeg.av_frame_alloc(); output = ffmpeg.av_frame_alloc(); }

    // A new speed (0.1× to 4×) from what's mixed next, starting over: anything waiting is dropped.
    internal void SetSpeed(double speed) { Speed = Math.Clamp(speed, .1, 4); Reset(); }
    // After a seek: nothing old comes out.
    internal void Reset() { spareStart = spareCount = 0; Start(Speed); }
    // A new stretch at a speed. Sound already made (Finish) still comes out first.
    internal void Start(double speed)
    {
        FreeGraph(); drained = false; pts = 0;
        Speed = Math.Clamp(speed, .1, 4);
        if (Math.Abs(Speed - 1) > 1e-6) Build();
    }
    // Everything given to the filter comes out now (at the speed it went in at); Start follows.
    internal void Finish()
    {
        if (graph == null) return;
        if (!drained) { ffmpeg.av_buffersrc_add_frame(source, null); drained = true; }
        while (Pull()) { }
    }
    private void Build()
    {
        graph = ffmpeg.avfilter_graph_alloc();
        AVFilterContext* src = null, dst = null;
        FfmpegLibrary.Check(ffmpeg.avfilter_graph_create_filter(&src, ffmpeg.avfilter_get_by_name("abuffer"), "in",
            $"sample_rate={FfmpegAudioMixer.Rate}:sample_fmt=flt:channel_layout=stereo:time_base=1/{FfmpegAudioMixer.Rate}", null, graph), "Couldn't set up the speed filter");
        FfmpegLibrary.Check(ffmpeg.avfilter_graph_create_filter(&dst, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, graph), "Couldn't set up the speed filter");
        source = src; sink = dst;
        // atempo takes 0.5 to 2 per stage (as the export chains it).
        string stages = Stages(Speed) + ",aformat=sample_fmts=flt:channel_layouts=stereo";
        var outputs = ffmpeg.avfilter_inout_alloc(); var inputs = ffmpeg.avfilter_inout_alloc();
        outputs->name = ffmpeg.av_strdup("in"); outputs->filter_ctx = source; outputs->pad_idx = 0; outputs->next = null;
        inputs->name = ffmpeg.av_strdup("out"); inputs->filter_ctx = sink; inputs->pad_idx = 0; inputs->next = null;
        int parsed = ffmpeg.avfilter_graph_parse_ptr(graph, stages, &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs); ffmpeg.avfilter_inout_free(&outputs);
        FfmpegLibrary.Check(parsed, "Couldn't set up the speed filter");
        FfmpegLibrary.Check(ffmpeg.avfilter_graph_config(graph, null), "Couldn't start the speed filter");
    }
    // atempo takes 0.5 to 2 per stage, so very slow or fast speeds chain stages (like ShareExportOptions.AudioTempo).
    internal static string Stages(double speed)
    {
        var stages = new System.Collections.Generic.List<string>(); double left = speed;
        while (left < .5 - 1e-9) { stages.Add("atempo=0.5"); left /= .5; }
        while (left > 2 + 1e-9) { stages.Add("atempo=2"); left /= 2; }
        if (Math.Abs(left - 1) > 1e-9 || stages.Count == 0) stages.Add("atempo=" + left.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(",", stages);
    }
    internal int Read(float[] buffer, int frames) => Read(buffer, 0, frames, double.PositiveInfinity);
    // The next sound at the current speed into buffer from frame `offset`, taking the mix no further than
    // `until` (seconds). Fewer than asked only when the stretch or the clip has ended.
    internal int Read(float[] buffer, int offset, int frames, double until)
    {
        int written = 0;
        while (written < frames)
        {
            if (spareCount > 0)
            {
                int take = Math.Min(spareCount, (frames - written) * Channels);
                Array.Copy(spare, spareStart, buffer, (offset + written) * Channels, take);
                spareStart += take; spareCount -= take; written += take / Channels;
                if (spareCount == 0) spareStart = 0;
                continue;
            }
            int allowed = Allowed(until);
            if (graph == null)
            {
                int want = Math.Min(Math.Min(frames - written, allowed), Chunk);
                if (want <= 0) break;
                int got = mixer.Read(chunk, want);
                Array.Copy(chunk, 0, buffer, (offset + written) * Channels, got * Channels); written += got;
                if (got == 0) break;
                continue;
            }
            if (Pull()) continue;
            if (drained) break;
            int mixed = allowed > 0 ? mixer.Read(chunk, Math.Min(Chunk, allowed)) : 0;
            Push(mixed);
        }
        return written;
    }
    // How much more of the mix this stretch may take.
    private int Allowed(double until)
    {
        if (double.IsPositiveInfinity(until)) return int.MaxValue;
        double left = (until - mixer.Position) * FfmpegAudioMixer.Rate;
        return left < .5 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(left));
    }
    private void Push(int frames)
    {
        if (frames <= 0) { ffmpeg.av_buffersrc_add_frame(source, null); drained = true; return; }
        input->nb_samples = frames; input->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT; input->sample_rate = FfmpegAudioMixer.Rate;
        AVChannelLayout stereo; ffmpeg.av_channel_layout_default(&stereo, Channels); input->ch_layout = stereo;
        input->pts = pts; pts += frames;
        FfmpegLibrary.Check(ffmpeg.av_frame_get_buffer(input, 0), "Couldn't prepare sound for the speed filter");
        fixed (float* from = chunk) Buffer.MemoryCopy(from, input->data[0], frames * Channels * 4, frames * Channels * 4);
        FfmpegLibrary.Check(ffmpeg.av_buffersrc_add_frame(source, input), "Couldn't feed the speed filter");
        ffmpeg.av_frame_unref(input);
    }
    // What the filter has ready joins the end of what's waiting.
    private bool Pull()
    {
        if (graph == null) return false;
        int got = ffmpeg.av_buffersink_get_frame(sink, output);
        if (got < 0) return false;
        int floats = output->nb_samples * Channels;
        if (spareStart > 0) { Array.Copy(spare, spareStart, spare, 0, spareCount); spareStart = 0; }
        if (spare.Length < spareCount + floats) Array.Resize(ref spare, (spareCount + floats) * 2);
        fixed (float* to = &spare[spareCount]) Buffer.MemoryCopy(output->data[0], to, (spare.Length - spareCount) * 4L, floats * 4L);
        spareCount += floats;
        ffmpeg.av_frame_unref(output);
        return true;
    }
    private void FreeGraph() { if (graph != null) { var g = graph; ffmpeg.avfilter_graph_free(&g); graph = null; source = sink = null; } }
    public void Dispose()
    {
        FreeGraph();
        var i = input; ffmpeg.av_frame_free(&i);
        var o = output; ffmpeg.av_frame_free(&o);
    }
}
