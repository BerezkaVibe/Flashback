using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
namespace Flashback;
internal static class FastClipExport
{
    // Stream-copy cuts retain existing compressed packets. Boundaries follow keyframes.
    internal static async Task<ClipResult> ExportAsync(string source,string destination,IEnumerable<KeepSection> sections,IProgress<double>? progress,CancellationToken token)
    {
        await ExportServices.Gate.WaitAsync(token);
        try { return await ExportCoreAsync(source,destination,sections,progress,token); }
        finally { ExportServices.Gate.Release(); }
    }
    private static async Task<ClipResult> ExportCoreAsync(string source,string destination,IEnumerable<KeepSection> sections,IProgress<double>? progress,CancellationToken token)
    {
        source=Path.GetFullPath(source); destination=Path.GetFullPath(destination);
        if(source.Equals(destination,StringComparison.OrdinalIgnoreCase)||File.Exists(destination)) throw new IOException("Choose a new filename. Existing clips are never overwritten.");
        var media=ClipMedia.Read(source); var ranges=ClipEditor.Validate(sections,media.Duration);
        Storage.EnsureWritable(Path.GetDirectoryName(destination)!);
        string scratch=Path.Combine(Path.GetDirectoryName(destination)!,".flashback-trim-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string partial=Path.Combine(scratch,"joined.mp4");
        try
        {
            string probe=await RunAsync(scratch,token,"-i",source,"-frames:v","1","-an","-f","framemd5","-");
            var clock=Regex.Match(probe,@"#tb 0: (\d+)/(\d+)");
            if(!clock.Success) throw new IOException("Cannot identify the clip timing. Use precise export.");
            double frameSeconds=double.Parse(clock.Groups[1].Value,CultureInfo.InvariantCulture)/double.Parse(clock.Groups[2].Value,CultureInfo.InvariantCulture);
            var names=new List<string>();
            for(int i=0;i<ranges.Count;i++)
            {
                token.ThrowIfCancellationRequested(); string name=$"part-{i:D3}.mp4";
                var s=ranges[i];
                await RunAsync(scratch,token,"-ss",s.Start.ToString("0.######",CultureInfo.InvariantCulture),"-i",source,"-t",s.Duration.ToString("0.######",CultureInfo.InvariantCulture),"-map","0:v:0","-map","0:a:0?","-c","copy","-avoid_negative_ts","make_zero",name);
                double length=ClipMedia.Read(Path.Combine(scratch,name)).Duration;
                double aligned=Math.Ceiling(length/frameSeconds-0.000001)*frameSeconds;
                names.Add("file '"+name+"'\nduration "+aligned.ToString("0.#########",CultureInfo.InvariantCulture));
                progress?.Report((i+1.0)/(ranges.Count+1));
            }
            // Only generated simple filenames enter the concat manifest.
            await File.WriteAllLinesAsync(Path.Combine(scratch,"parts.txt"),names,token);
            await RunAsync(scratch,token,"-f","concat","-safe","1","-i","parts.txt","-map","0:v:0","-map","0:a:0?","-c","copy","-movflags","+faststart","joined.mp4");
            var output=ClipMedia.Read(partial);
            if(output.Duration<=0 || output.HasAudio!=media.HasAudio) throw new IOException("The fast export could not be verified.");
            token.ThrowIfCancellationRequested(); File.Move(partial,destination);
            var clip=new ClipResult(destination,output.Duration,new DirectoryInfo(Path.GetDirectoryName(destination)!).Name,DateTimeOffset.Now);
            try{ClipLibrary.Remember(clip);}catch{}
            progress?.Report(1); return clip;
        }
        finally { try { Directory.Delete(scratch,true); } catch(IOException){} }
    }
    private static async Task<string> RunAsync(string directory,CancellationToken token,params string[] arguments)
    {
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe")){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"-hide_banner","-loglevel","error","-nostdin"}.Concat(arguments)) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested(); using var job=new ChildProcessJob(); using var process=Process.Start(info)!; job.Add(process); process.PriorityClass=ProcessPriorityClass.BelowNormal;
        var error=process.StandardError.ReadToEndAsync(); var output=process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch { if(!process.HasExited)process.Kill(true); await process.WaitForExitAsync(); await error; await output; throw; }
        var detail=await error; var text=await output; if(process.ExitCode!=0)throw new IOException("Fast export failed: "+detail); return text;
    }
}

