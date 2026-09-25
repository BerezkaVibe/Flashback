using System;
using FFmpeg.AutoGen;

namespace Flashback;

// Plays the mix faster or slower with its pitch kept, through FFmpeg's atempo filter (what the export uses
// for speed parts). At 1× it passes the mix straight through.
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

    internal FfmpegTempo(FfmpegAudioMixer mixer) { this.mixer = mixer; input = ffmpeg.av_frame_alloc(); output = ffmpeg.av_frame_alloc(); }

    // A new speed (0.25× to 4×) starts from what's mixed next; anything waiting in the filter is dropped.
    internal void SetSpeed(double speed)
    {
        speed = Math.Clamp(speed, .25, 4);
        if (Math.Abs(speed - Speed) < 1e-6 && (graph != null || Math.Abs(speed - 1) < 1e-6)) return;
        Speed = speed; Reset();
    }
    // After a seek: nothing old comes out.
    internal void Reset()
    {
        FreeGraph(); spareStart = spareCount = 0; drained = false; pts = 0;
        if (Math.Abs(Speed - 1) > 1e-6) Build();
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
    // The next `frames` of sound at the current speed; fewer only at the end of the clip.
    internal int Read(float[] buffer, int frames)
    {
        if (graph == null) return mixer.Read(buffer, frames);
        int written = 0;
        while (written < frames)
        {
            if (spareCount > 0)
            {
                int take = Math.Min(spareCount, (frames - written) * Channels);
                Array.Copy(spare, spareStart, buffer, written * Channels, take);
                spareStart += take; spareCount -= take; written += take / Channels;
                continue;
            }
            if (Pull()) continue;
            if (drained) break;
            int mixed = mixer.Read(chunk, Chunk);
            Push(mixed);
        }
        return written;
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
    private bool Pull()
    {
        int got = ffmpeg.av_buffersink_get_frame(sink, output);
        if (got < 0) return false;
        int floats = output->nb_samples * Channels;
        if (spare.Length < floats) spare = new float[floats * 2];
        fixed (float* to = spare) Buffer.MemoryCopy(output->data[0], to, spare.Length * 4, floats * 4);
        spareStart = 0; spareCount = floats;
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
