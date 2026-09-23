using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
namespace Flashback;
internal static class PerformanceDiagnostics
{
    internal static async Task RunAsync(bool compatibility)
    {
        Directory.CreateDirectory(Storage.Root);
        var settings=Storage.Load(out _); settings.FrameRate=60; settings.Height=1080;
        settings.OutputFolder=Path.Combine(Storage.Root,"clips");
        await using var recorder=new Recorder { DisableNativeForTests=compatibility };
        await recorder.StartAsync(settings); await Task.Delay(1500);
        var before=recorder.Performance; var timer=Stopwatch.StartNew(); long peak=before.Memory;
        for(int i=0;i<30;i++) { await Task.Delay(500); peak=Math.Max(peak,recorder.Performance.Memory); }
        var after=recorder.Performance;
        File.WriteAllText(Path.Combine(Storage.Root,"performance.txt"), $"Compatibility: {compatibility}\nElapsed: {timer.Elapsed.TotalSeconds:0.000}s\nCPU seconds: {after.Cpu-before.Cpu:0.000}\nAverage CPU cores: {(after.Cpu-before.Cpu)/timer.Elapsed.TotalSeconds:0.000}\nProcess working sets sum, peak MiB: {peak/1048576.0:0.0}\n"+recorder.VideoStats);
        var clip=await recorder.SaveAsync("Performance test",DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(Storage.Root,"clip.txt"),clip.Path);
        await recorder.StopAsync();
    }
}
