using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal static class ReliabilityDiagnostics
{
    internal static void Clocks()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok,string message) { if(!ok) throw new Exception(message); File.AppendAllText(Path.Combine(Storage.Root,"clock-results.txt"),"PASS "+message+"\n"); }
        foreach(var format in new[]{"f32le","s16le","s24le","s32le"})
        foreach(double drift in new[]{-.0002,.0002})
        {
            int bytes=format=="s16le" ? 2 : format=="s24le" ? 3 : 4;
            var normalizer=new AudioClockNormalizer(48000,bytes*2,format);
            var packet=new byte[480*bytes*2]; double worst=0;
            // Two hours of timestamps in an accelerated run, +/-200 ppm device clock error.
            for(int n=0;n<720000;n++)
            {
                double time=n*.01/(1+drift);
                var result=normalizer.Process(packet,time,0,time+.01+(n%7)*.003);
                worst=Math.Max(worst,Math.Abs(normalizer.FramesWritten/48000d-(time+.01/(1+drift))));
                if(n%10000==0 && result.Samples.Any(b=>b!=0)) throw new Exception("Silence changed during correction.");
            }
            Check(worst<.005 && normalizer.CorrectedFrames>1000,$"{format} {drift*1e6:+0;-0} ppm: two-hour sample timeline stays within {worst*1000:0.000} ms");
        }
        var clock=new AudioClockNormalizer(48000,4,"f32le");
        clock.Process(new byte[480*4],0,0,.01);
        var gap=clock.Process(new byte[480*4],.26,0,.27);
        Check(gap.Silence?.Length==12000*4,"A 250 ms missing packet span is preserved as silence, not collapsed time");
        long before=clock.FramesWritten;
        var duplicate=clock.Process(new byte[480*4],.26,0,.28);
        // A repeated small packet timestamp enters the smooth correction band, never a negative output length.
        Check(duplicate.Samples.Length>0 && clock.FramesWritten>before,"Repeated packet timestamp keeps a valid bounded output");
        clock.Process(new byte[480*4],double.NaN,0,99);
        Check(clock.FramesWritten-before<1000,"Untrusted timestamp does not insert a huge silence block");
        var lag=new EncoderLagMonitor();
        Check(!lag.Observe(2,4) && !lag.Observe(11,.1),"Startup buffering and normal pipe delay do not restart capture");
        Check(!lag.Observe(12,2) && !lag.Observe(13,.1) && !lag.Observe(14,2),"A brief game-load spike resets without recovery");
        Check(lag.Observe(17,2) && CaptureRecovery.IsRecoverable(EncoderLagMonitor.Message) && CaptureRecovery.IsRecoverable(AudioLoopback.BacklogMessage),"Sustained encoder lag and bounded audio backlog trigger recoverable faults");
        for(int s=30;s<=300;s+=5) new Settings {ReplaySeconds=s}.Validate();
        bool rejected=false;try{new Settings {ReplaySeconds=31}.Validate();}catch(ArgumentException){rejected=true;}
        Check(rejected,"All five-second replay settings validate and off-grid values are rejected");
    }
    internal static async Task SoakAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var options=new Settings {ReplaySeconds=45,FrameRate=30,Height=720,DesktopAudio=true,OutputFolder=Path.Combine(Storage.Root,"clips")};
        await using var recorder=new Recorder {UseBridgeForTests=true};
        string? fault=null;recorder.Faulted+=(_,message)=>fault=message;
        await recorder.StartAsync(options,synthetic:true);
        var run=Stopwatch.StartNew();long lowMemory=long.MaxValue,highMemory=0;int maxSegments=0;
        while(run.Elapsed.TotalSeconds<120)
        {
            await Task.Delay(1000);
            if(fault!=null || !recorder.IsRecording) throw new Exception("Sustained recorder failed: "+fault);
            if(run.Elapsed.TotalSeconds>30)
            {
                long memory=Process.GetCurrentProcess().WorkingSet64;lowMemory=Math.Min(lowMemory,memory);highMemory=Math.Max(highMemory,memory);
                maxSegments=Math.Max(maxSegments,Directory.GetFiles(Path.Combine(Storage.Root,"buffer"),"*.ts",SearchOption.AllDirectories).Length);
            }
        }
        var clip=await recorder.SaveAsync("Sustained-45-second-buffer",DateTimeOffset.Now);
        await recorder.StopAsync();await EditorDiagnostics.Ffmpeg("-i",clip.Path,"-f","null","-");
        if(Math.Abs(clip.Duration-45)>.2 || maxSegments>35) throw new Exception("Replay duration or retention exceeded its bound.");
        File.WriteAllText(Path.Combine(Storage.Root,"soak-results.txt"),$"PASS 120 seconds of continuous frame-bridge recording; 45-second replay fully decodes.\nMaximum retained segments: {maxSegments}\nApp working-set range after warmup: {lowMemory/1048576d:0.0}–{highMemory/1048576d:0.0} MiB\nSynthetic encoder/capture test, not a multi-hour hardware/gameplay guarantee.\n");
    }
    internal static async Task SyncMediaAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        const int rate=48000; const double skew=.0002;
        string raw=Path.Combine(Storage.Root,"corrected.f32");var clock=new AudioClockNormalizer(rate,4,"f32le");
        using(var stream=File.Create(raw))
        for(int n=0;n<6002;n++)
        {
            double time=n*.01/(1+skew);var bytes=new byte[480*4];
            for(int i=0;i<480;i++)
            {
                double t=time+i/(rate*(1+skew));float sample=t%2<.1 ? (float)(.3*Math.Sin(2*Math.PI*1000*t)) : 0;
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*4,4),sample);
            }
            var packet=clock.Process(bytes,time,0,time+.012);if(packet.Silence!=null)stream.Write(packet.Silence);stream.Write(packet.Samples);
        }
        string video=Path.Combine(Storage.Root,"clock-sync.mp4");
        await EditorDiagnostics.Ffmpeg("-f","lavfi","-i",@"color=black:s=320x180:r=30:d=60,drawbox=color=white:t=fill:enable='lt(mod(t,2),0.1)'","-f","f32le","-ar","48000","-ac","1","-i",raw,"-t","60","-c:v","libx264","-preset","ultrafast","-threads","2","-pix_fmt","yuv420p","-c:a","aac",video);
        string pictures=Path.Combine(Storage.Root,"video.gray"),audio=Path.Combine(Storage.Root,"audio.f32");
        await EditorDiagnostics.Ffmpeg("-i",video,"-vf","scale=1:1","-pix_fmt","gray","-f","rawvideo",pictures);
        await EditorDiagnostics.Ffmpeg("-i",video,"-vn","-ac","1","-ar","48000","-f","f32le",audio);
        var gray=File.ReadAllBytes(pictures);var samples=File.ReadAllBytes(audio);var offsets=new System.Collections.Generic.List<double>();
        for(int f=1;f<gray.Length;f++)
        {
            if(gray[f]<180 || gray[f-1]>=180)continue;
            double v=f/30d;int first=Math.Max(0,(int)((v-.15)*rate)),last=Math.Min(samples.Length/4,(int)((v+.15)*rate));
            int found=-1;for(int i=first;i<last;i++)if(Math.Abs(BitConverter.ToSingle(samples,i*4))>.06){found=i;break;}
            if(found<0)throw new Exception("Missing matching audio pulse at "+v);
            offsets.Add(found/(double)rate-v);
        }
        if(offsets.Count<25 || offsets.Max()-offsets.Min()>.045 || offsets.Any(x=>Math.Abs(x)>.045))throw new Exception("Flash/beep alignment drifted outside one video frame plus AAC tolerance.");
        File.WriteAllText(Path.Combine(Storage.Root,"sync-results.txt"),$"PASS {offsets.Count} decoded flash/beep pairs, 60-second media with +200 ppm simulated audio clock drift.\nAudio minus video offsets: {offsets.Min()*1000:0.000} to {offsets.Max()*1000:0.000} ms.\nSynthetic media verifies PCM correction and encoding, not live game capture.\n");
    }
    internal static async Task BacklogRecoveryAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var settings=new Settings { ReplaySeconds=30,FrameRate=30,DesktopAudio=true,OutputFolder=Path.Combine(Storage.Root,"clips") };
        await using var recorder=new Recorder {UseBridgeForTests=true};
        var fault=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Faulted+=(_,message)=>fault.TrySetResult(message);
        await recorder.StartAsync(settings,synthetic:true);await Task.Delay(6000);
        double before=recorder.RecordedSeconds;long generation=recorder.Generation;
        recorder.InjectEncoderBacklogForTest();
        string reason=await fault.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if(!CaptureRecovery.IsRecoverable(reason))throw new Exception("Encoder backlog did not classify for recovery.");
        var recovery=new CaptureRecovery();
        await recovery.RunAsync(token=>recorder.RestartAfterCaptureLossAsync(settings,true,token),_=>{});
        await Task.Delay(3000);
        if(recorder.Generation<=generation || !recorder.IsRecording)throw new Exception("Encoder session did not recover.");
        var clip=await recorder.SaveAsync("Recovered-backlog",DateTimeOffset.Now);
        await recorder.StopAsync();await EditorDiagnostics.Ffmpeg("-i",clip.Path,"-f","null","-");
        if(clip.Duration<before)throw new Exception("Recovery discarded completed replay history.");
        File.WriteAllText(Path.Combine(Storage.Root,"backlog-results.txt"),$"PASS Injected encoder backlog classified and recovered through the normal recovery coordinator.\nPASS Retained completed history and new footage in a fully decoded {clip.Duration:0.00}s replay.\n");
    }
}
