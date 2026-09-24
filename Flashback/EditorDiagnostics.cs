using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

internal static class EditorDiagnostics
{
    internal static async Task RunAsync(bool interactive = false)
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            checks.Add(name); File.WriteAllText(Path.Combine(Storage.Root, "editor-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        var folder = Path.Combine(Storage.Root, "clips", "Test game"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Three colors ' source.mp4");
        await Ffmpeg("-y", "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=4", "-f", "lavfi", "-i", "color=green:s=640x360:r=30:d=4", "-f", "lavfi", "-i", "color=blue:s=640x360:r=30:d=4",
            "-f", "lavfi", "-i", @"aevalsrc=0.1*sin(2*PI*if(lt(t\,4)\,440\,if(lt(t\,8)\,880\,1320))*t):s=48000:d=12",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var metadata = ClipMedia.Read(source);
        Check(Math.Abs(metadata.Duration - 12) < .01 && metadata.HasAudio, "Read MP4 duration and audio without a second media binary");
        Check(Math.Abs(KeepSection.Parse("1:02.500") - 62.5) < .001 && KeepSection.Parse("2.25") == 2.25, "Parse fractional seconds and minute timestamps");
        foreach (var invalid in new[] { new[] { new KeepSection(3, 1) }, new[] { new KeepSection(-1, 2) }, new[] { new KeepSection(0, 13) }, new[] { new KeepSection(double.NaN, 2) }, new[] { new KeepSection(0, 3), new KeepSection(2, 4) } })
        {
            bool rejected = false; try { ClipEditor.Validate(invalid, 12); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Reject invalid or overlapping ranges: " + JsonSerializer.Serialize(invalid.Select(s => s.Start + " to " + s.End)));
        }
        var ranges = new[] { new KeepSection(.5, 2.5), new KeepSection(8.5, 10.5) };
        var joined = Path.Combine(folder, "Red and blue — combined.mp4");
        var result = await ClipEditor.ExportAsync(source, joined, ranges, null, CancellationToken.None);
        Check(Math.Abs(result.Duration - 4) < .1 && ClipMedia.Read(joined).HasAudio, "Hardware export joins two sections in chronological order with audio");
        await Ffmpeg("-i", joined, "-f", "null", "-");
        Check(true, "Combined clip fully decodes without errors");
        Check(metadata.Width == 640 && metadata.Height == 360 && metadata.AudioTracks == 1 && !metadata.HasSeparateTracks, "Read video size and audio track count from MP4 headers");
        var cropped = Path.Combine(folder, "Cropped square.mp4");
        await ExportServices.PreciseAsync(source, cropped, new[] { new KeepSection(1, 3) }, null, CancellationToken.None, true, new ShareExportOptions { Crop = new CropRect(100, 20, 300, 300) });
        Check(ClipMedia.Read(cropped).Width == 300 && ClipMedia.Read(cropped).Height == 300, "Crop exports the selected area at its source size");
        foreach (var format in new[] { ExportFormat.Gif, ExportFormat.Mp3, ExportFormat.Mov })
        {
            var options = ShareExportOptions.For(format);
            var output = Path.Combine(folder, "Format check" + options.Extension);
            await ExportServices.PreciseAsync(source, output, new[] { new KeepSection(1, 3) }, null, CancellationToken.None, true, options);
            var header = File.ReadAllBytes(output).Take(12).ToArray();
            bool valid = format switch
            {
                ExportFormat.Gif => header[0] == 'G' && header[1] == 'I' && header[2] == 'F',
                ExportFormat.Mp3 => header[0] == 'I' && header[1] == 'D' && header[2] == '3' || header[0] == 0xFF,
                _ => System.Text.Encoding.ASCII.GetString(header, 4, 4) is "ftyp" or "moov" or "wide" or "mdat"
            };
            Check(valid && new FileInfo(output).Length > 1000, $"{format} export writes a valid file");
        }
        // Slow motion: a 2 s selection at 0.5x and 0.25x becomes 4 s and 8 s, audio included.
        foreach (double speed in new[] { .5, .25 })
        {
            var slow = Path.Combine(folder, $"Slow {speed}.mp4");
            var slowResult = await ExportServices.PreciseAsync(source, slow, new[] { new KeepSection(1, 3) }, null, CancellationToken.None, true, new ShareExportOptions { Speed = speed });
            var slowMedia = ClipMedia.Read(slow);
            Check(Math.Abs(slowMedia.Duration - 2 / speed) < .3 && slowMedia.HasAudio && Math.Abs(slowResult.Duration - 2 / speed) < .3, $"{speed}x slow motion stretches 2 s to {2 / speed} s with audio");
        }
        // Slow-motion regions: 4 s kept with 2-3 s at 0.5x becomes 5 s; with a 0.25x region across two sections too.
        var partSlow = Path.Combine(folder, "Part slow.mp4");
        await ExportServices.PreciseAsync(source, partSlow, new[] { new KeepSection(1, 5) }, null, CancellationToken.None, true,
            new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(2, 3, .5) }, Cuts = new[] { new CutRegion(-1, 3.5, 4) } });
        Check(Math.Abs(ClipMedia.Read(partSlow).Duration - 5) < .3 && ClipMedia.Read(partSlow).HasAudio, "A 0.5x slow-motion part lengthens only that stretch, alongside a cut elsewhere");
        var splitSlow = Path.Combine(folder, "Split slow.mp4");
        await ExportServices.PreciseAsync(source, splitSlow, new[] { new KeepSection(1, 2), new KeepSection(3, 4) }, null, CancellationToken.None, true,
            new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(1.5, 3.5, .25) } });
        Check(Math.Abs(ClipMedia.Read(splitSlow).Duration - (1 + 1 * 4)) < .4, "Slow motion applies only where it overlaps the kept sections");
        // Faster parts: 4 s kept with 2-4 s at 2x becomes 3 s; very slow and fast audio chain tempo stages.
        var fastPart = Path.Combine(folder, "Fast part.mp4");
        await ExportServices.PreciseAsync(source, fastPart, new[] { new KeepSection(1, 5) }, null, CancellationToken.None, true, new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(2, 4, 2) } });
        Check(Math.Abs(ClipMedia.Read(fastPart).Duration - 3) < .3 && ClipMedia.Read(fastPart).HasAudio, "A 2x speed part shortens only that stretch");
        Check(ShareExportOptions.AudioTempo(4) == "atempo=2,atempo=2" && ShareExportOptions.AudioTempo(.1).StartsWith("atempo=0.5,atempo=0.5,atempo=0.5"), "Audio tempo chains stages below 0.5x and above 2x");
        // Zoom: curve math, then a real export compared frame by frame against the source.
        var ramp = ZoomPreset.BuiltIns[0].Curve;
        Check(ramp.At(0) == 0 && ramp.At(ramp.Length) == 1 && Enumerable.Range(1, 40).All(i => ramp.At(ramp.Length * i / 40) >= ramp.At(ramp.Length * (i - 1) / 40) - 1e-9), "Zoom curves rise from 0 to 1 without dipping");
        var zoomRegion = new ZoomRegion(1, 3, .5, .5, 2, ZoomPreset.BuiltIns[1].Curve);
        Check(zoomRegion.ZoomAt(.9) == 1 && Math.Abs(zoomRegion.ZoomAt(2.5) - 2) < 1e-6 && zoomRegion.ZoomAt(3) == 1, "Zoom is 1x outside, max on the plateau, and cuts back at the end");
        var zoomOut = zoomRegion with { ZoomOut = true };
        Check(zoomOut.ZoomAt(2.99) < 1.1 && Math.Abs(zoomOut.ZoomAt(2) - 2) < 1e-6, "Zoom-out eases back down before the end");
        Check(new ZoomRegion(1, 1.2, .5, .5, 3, ZoomPreset.BuiltIns[3].Curve, true).ZoomAt(1.1) > 1, "Ramps longer than a short zoom are squeezed to fit");
        var detailed = Path.Combine(folder, "Detailed.mp4");
        await Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", detailed);
        var zoomed = Path.Combine(folder, "Zoomed.mp4");
        await ExportServices.PreciseAsync(detailed, zoomed, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { ZoomRegions = new[] { zoomRegion } });
        Check(Math.Abs(ClipMedia.Read(zoomed).Duration - 4) < .2 && ClipMedia.Read(zoomed).Width == 640 && ClipMedia.Read(zoomed).HasAudio, "Zoom keeps the clip's length, size and sound");
        async Task<byte[]> Frame(string file, double at, string filter = "")
        {
            var raw = Path.Combine(folder, $"frame-{Guid.NewGuid():N}.raw");
            await Ffmpeg("-y", "-ss", ExportServices.Number(at), "-i", file, "-frames:v", "1", "-vf", filter + "scale=64:36,format=gray", "-f", "rawvideo", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }
        double Diff(byte[] a, byte[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second));
        double outside = Diff(await Frame(detailed, .5), await Frame(zoomed, .5));
        double plateau = Diff(await Frame(detailed, 2.5, "crop=320:180:160:90,"), await Frame(zoomed, 2.5));
        double unzoomed = Diff(await Frame(detailed, 2.5), await Frame(zoomed, 2.5));
        Check(outside < 8 && plateau < 12 && unzoomed > plateau + 5, $"Zoomed frames match a 2x crop of the target (outside {outside:0.0}, plateau {plateau:0.0}, versus unzoomed {unzoomed:0.0})");
        // Text and pictures: drawn at the right place and time, stuck to or fixed against zoom.
        async Task<byte[]> Pixel(string file, double at, int x, int y)
        {
            var raw = Path.Combine(folder, $"pixel-{Guid.NewGuid():N}.rgb");
            await Ffmpeg("-y", "-ss", ExportServices.Number(at), "-i", file, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }
        bool Near(byte[] rgb, int r, int g, int b) => Math.Abs(rgb[0] - r) < 60 && Math.Abs(rgb[1] - g) < 60 && Math.Abs(rgb[2] - b) < 60;
        var caption = OverlayItem.NewText(1, 3, null) with { Text = "TEST", Y = .5, Shape = OverlayShape.Box, ShapeColor = "#FF0000FF", Padding = 60 };
        var captioned = Path.Combine(folder, "Captioned.mp4");
        await ExportServices.PreciseAsync(source, captioned, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { Overlays = new[] { caption } });
        var boxEdge = await Pixel(captioned, 2, 285, 180);
        Check(Near(boxEdge, 0, 0, 255) && Near(await Pixel(captioned, .5, 285, 180), 255, 0, 0) && Near(await Pixel(captioned, 3.5, 285, 180), 255, 0, 0) && Math.Abs(ClipMedia.Read(captioned).Duration - 4) < .2,
            "A caption shows only during its part, with its box drawn behind it");
        var sticker = Path.Combine(folder, "Sticker.png");
        await Ffmpeg("-y", "-f", "lavfi", "-i", "color=white:s=200x200,drawbox=x=50:y=50:w=100:h=100:color=0x00FF00:t=fill", "-frames:v", "1", sticker);
        var picture = OverlayItem.NewImage(0, 4, sticker) with { Key = OverlayKey.White };
        var pictured = Path.Combine(folder, "Pictured.mp4");
        await ExportServices.PreciseAsync(source, pictured, new[] { new KeepSection(0, 2) }, null, CancellationToken.None, true, new ShareExportOptions { Overlays = new[] { picture } });
        Check(Near(await Pixel(pictured, 1, 320, 180), 0, 255, 0) && Near(await Pixel(pictured, 1, 320 - 70, 180), 255, 0, 0), "Pictures show over the video with a white background removed");
        // A small blue square at x 0.6 under a 2x zoom on the centre: stuck, it moves out to 0.7; fixed, it stays.
        var square = OverlayItem.NewImage(0, 4, sticker) with { Key = OverlayKey.None, X = .6, Y = .5, Scale = .1, ImageCorner = 0 };
        var steady = new ZoomRegion(0, 4, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve);
        foreach (bool stuck in new[] { true, false })
        {
            var output = Path.Combine(folder, $"Zoom overlay {stuck}.mp4");
            await ExportServices.PreciseAsync(source, output, new[] { new KeepSection(0, 3) }, null, CancellationToken.None, true, new ShareExportOptions { ZoomRegions = new[] { steady }, Overlays = new[] { square with { StickToVideo = stuck } } });
            int x = (int)(640 * (stuck ? .7 : .6));
            Check(Near(await Pixel(output, 2, x, 180), 0, 255, 0), stuck ? "Pictures stuck to the video follow a zoom" : "Pictures fixed on screen ignore a zoom");
        }
        // Everything at once: animated entrances, a typewriter caption, a speed part and two sections.
        var busy = Path.Combine(folder, "Busy overlays.mp4");
        var busyItems = new[]
        {
            caption with { Start = .5, End = 3.5, In = OverlayMotion.Typewriter, InLength = 1, Out = OverlayMotion.Fade, Shape = OverlayShape.Bubble, Shadow = new OverlayShadow(true) },
            picture with { Start = 1, End = 5, In = OverlayMotion.Pop, Out = OverlayMotion.SlideLeft, Rotation = 30, Opacity = .7, Layer = 1, StickToVideo = false },
            caption with { Start = 0, End = 4, Y = .2, Text = "SKEW", Shape = OverlayShape.Pill, ShapeColor = "#99FFE600", ShapeCorners = new[] { (-60.0, -30.0), (40.0, 0.0), (0.0, 30.0), (0.0, 0.0) }, Layer = 2 },
        };
        var plain = caption with { Shape = OverlayShape.Pill };
        Check(OverlayRenderer.Content(busyItems[2], 4, 0, 16.0 / 9).Bounds.Left < OverlayRenderer.Content(plain, 4, 0, 16.0 / 9).Bounds.Left - 40, "Dragging a shape's corner stretches the shape");
        await ExportServices.PreciseAsync(source, busy, new[] { new KeepSection(0, 2), new KeepSection(3, 5) }, null, CancellationToken.None, true,
            new ShareExportOptions { Overlays = busyItems, SlowRegions = new[] { new SpeedRegion(1, 2, .5) }, ZoomRegions = new[] { zoomRegion } });
        Check(Math.Abs(ClipMedia.Read(busy).Duration - 5) < .3, "Animated text and pictures export alongside speed parts, zoom and two sections");
        var typing = caption with { In = OverlayMotion.Typewriter, InLength = 1 };
        Check(typing.StateAt(0).Chars == 0 && typing.StateAt(.5).Chars == 2 && typing.StateAt(1.5).Chars == 4, "Typewriter reveals the text over its entrance");
        Check(Math.Abs((caption with { In = OverlayMotion.Fade, InLength = 1 }).StateAt(.25).Opacity - .25) < 1e-6, "Fade in eases the opacity up");
        var slowGif = Path.Combine(folder, "Slow.gif");
        await ExportServices.PreciseAsync(source, slowGif, new[] { new KeepSection(1, 2) }, null, CancellationToken.None, true, ShareExportOptions.For(ExportFormat.Gif) with { Speed = .5, Crop = new CropRect(0, 0, 320, 180) });
        Check(new FileInfo(slowGif).Length > 1000, "Slow motion also works for cropped GIFs");
        // Cut out: 1.0-2.0 s of the source is blacked out and muted; the section keeps its full length.
        var censored = Path.Combine(folder, "Censored.mp4");
        await ExportServices.PreciseAsync(source, censored, new[] { new KeepSection(.5, 3.5) }, null, CancellationToken.None, true,
            new ShareExportOptions { Cuts = new[] { new CutRegion(-1, 1, 2), new CutRegion(0, 1, 2) } });
        string pixel = Path.Combine(Storage.Root, "cut-pixel.rgb"), cutPcm = Path.Combine(Storage.Root, "cut-audio.pcm"), openPcm = Path.Combine(Storage.Root, "open-audio.pcm");
        await Ffmpeg("-y", "-ss", "1", "-i", censored, "-frames:v", "1", "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", pixel);
        await Ffmpeg("-y", "-ss", "0.7", "-t", "0.6", "-i", censored, "-f", "s16le", "-ac", "1", cutPcm);
        await Ffmpeg("-y", "-ss", "2.0", "-t", "0.6", "-i", censored, "-f", "s16le", "-ac", "1", openPcm);
        short Loudest(string file) { var b = File.ReadAllBytes(file); short max = 0; for (int i = 0; i + 1 < b.Length; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(b, i))); return max; }
        var dark = File.ReadAllBytes(pixel);
        Check(Math.Abs(ClipMedia.Read(censored).Duration - 3) < .15 && dark.All(v => v < 24), "Cut out blacks out the picture without removing time");
        Check(Loudest(cutPcm) < 200 && Loudest(openPcm) > 1000, "Cut out mutes audio only inside the cut");
        var tracks = Path.Combine(folder, "Three tracks.mp4");
        await Ffmpeg("-y", "-f", "lavfi", "-i", "color=gray:s=320x180:r=30:d=3", "-f", "lavfi", "-i", "sine=f=440:d=3", "-f", "lavfi", "-i", "sine=f=660:d=3", "-f", "lavfi", "-i", "sine=f=880:d=3",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", tracks);
        Check(ClipMedia.Read(tracks).HasSeparateTracks, "Clips with desktop and microphone tracks are recognized");
        var remixed = Path.Combine(folder, "Microphone muted.mp4");
        await ExportServices.PreciseAsync(tracks, remixed, new[] { new KeepSection(.5, 2.5) }, null, CancellationToken.None, true, new ShareExportOptions { MicrophoneVolume = 0 });
        Check(ClipMedia.Read(remixed).AudioTracks == 1 && Math.Abs(ClipMedia.Read(remixed).Duration - 2) < .15, "Track volumes mix separate recordings down to one playable track");
        var colors = Path.Combine(Storage.Root, "frames.rgb");
        await Ffmpeg("-y", "-i", joined, "-vf", "fps=1,scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", colors);
        var rgb = File.ReadAllBytes(colors);
        Check(rgb.Length == 12 && rgb[0] > 180 && rgb[2] < 60 && rgb[3] > 180 && rgb[8] > 180 && rgb[11] > 180 && rgb[7] < 60,
            "Export contains red then blue; the green filler section is removed");
        var audio = Path.Combine(Storage.Root, "joined-audio.pcm");
        await Ffmpeg("-y", "-i", joined, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", audio);
        byte[] pcm = File.ReadAllBytes(audio);
        double Frequency(double start)
        {
            int crossings = 0, first = (int)(start * 48000), samples = 24000;
            for (int i = first + 1; i < first + samples; i++)
                if (BitConverter.ToInt16(pcm, i * 2 - 2) <= 0 && BitConverter.ToInt16(pcm, i * 2) > 0) crossings++;
            return crossings * 2;
        }
        Check(Math.Abs(Frequency(.5) - 440) < 20 && Math.Abs(Frequency(2.5) - 1320) < 20, "Audio matches the retained video sections after the join");
        var single = Path.Combine(folder, "Precise single trim.mp4");
        await ClipEditor.ExportAsync(source, single, new[] { new KeepSection(.35, 1.85) }, null, CancellationToken.None);
        Check(Math.Abs(ClipMedia.Read(single).Duration - 1.5) < .05, "A single trim cuts between keyframes accurately");
        var silent = Path.Combine(folder, "Silent source.mp4");
        await Ffmpeg("-y", "-i", source, "-an", "-c:v", "copy", silent);
        var silentEdit = Path.Combine(folder, "Silent combined.mp4");
        await ClipEditor.ExportAsync(silent, silentEdit, ranges, null, CancellationToken.None);
        Check(!ClipMedia.Read(silentEdit).HasAudio && Math.Abs(ClipMedia.Read(silentEdit).Duration - 4) < .1, "Multi-section export also supports clips without audio");
        foreach (string target in new[] { source, joined })
        {
            bool rejected = false; try { await ClipEditor.ExportAsync(source, target, ranges, null, CancellationToken.None); } catch (IOException) { rejected = true; }
            Check(rejected, "Refuse overwriting an original or existing export");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var canceled = Path.Combine(folder, "Canceled.mp4"); bool stopped = false;
            try { await ClipEditor.ExportAsync(source, canceled, new[] { new KeepSection(0, 12) }, new CancelProgress(cancel), cancel.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !File.Exists(canceled) && !Directory.EnumerateFiles(folder, "*.partial").Any(), "Cancel an active export and remove its partial output");
        }
        var fast = Path.Combine(folder,"Fast keyframe sections.mp4");
        await FastClipExport.ExportAsync(source, fast, new[]{new KeepSection(0,2),new KeepSection(8,10)},null,CancellationToken.None);
        await Ffmpeg("-i",fast,"-f","null","-");
        Check(ClipMedia.Read(fast).HasAudio,"Fast stream-copy section export fully decodes with audio");
        bool fastRejected=false; try { await FastClipExport.ExportAsync(source,source,ranges,null,CancellationToken.None); } catch(IOException){fastRejected=true;}
        Check(fastRejected,"Fast export rejects overwriting the original");
        using(var canceledToken=new CancellationTokenSource())
        {
            canceledToken.Cancel(); string canceledFast=Path.Combine(folder,"Canceled fast.mp4"); bool canceled=false;
            try{await FastClipExport.ExportAsync(source,canceledFast,ranges,null,canceledToken.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled && !File.Exists(canceledFast) && !Directory.EnumerateDirectories(folder,".flashback-trim-*").Any(),"Fast cancellation removes temporary parts and leaves no output");
        }
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "All editing leaves the original recording byte-for-byte unchanged");
        var oldFolder = Path.Combine(Storage.Root, "previous save folder"); Directory.CreateDirectory(oldFolder);
        var old = Path.Combine(oldFolder, "Old clip.mp4"); File.Copy(single, old);
        ClipLibrary.Remember(new(old, 1.5, "Previous game", DateTimeOffset.Now));
        ClipLibrary.Remember(new(joined, 4, "Test game", DateTimeOffset.Now));
        ClipLibrary.Remember(new(Path.Combine(folder, "missing.mp4"), 4, "Missing", DateTimeOffset.Now));
        File.AppendAllText(Path.Combine(Storage.Root, "clips.jsonl"), "broken json\n");
        var library = ClipLibrary.Load(Path.GetDirectoryName(folder)!);
        Check(library.Any(c => c.Path == old) && library.Count(c => c.Path == joined) == 1 && !library.Any(c => !File.Exists(c.Path)), "Library merges previous save folders, removes duplicates, and skips missing/malformed history");
        var preferences = new Settings { OutputFolder = Path.GetDirectoryName(folder)!, ExternalEditorPath = @"C:\Example Editor\editor.exe" };
        Storage.Save(preferences);
        Check(Storage.Load(out _).ExternalEditorPath == preferences.ExternalEditorPath && !preferences.RequiresBufferRestart(new Settings { OutputFolder = preferences.OutputFolder }), "External editor preference persists without restarting recording");
        var trim = new TrimWindow(source, true);
        trim.StartBox.Text = ".5"; trim.EndBox.Text = "2.5";
        trim.GetType().GetMethod("Add_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() });
        trim.StartBox.Text = "8.5"; trim.EndBox.Text = "10.5";
        trim.GetType().GetMethod("Add_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() });
        trim.SeekTo(1.5);
        trim.GetType().GetMethod("Split_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(trim,new object[]{trim,new RoutedEventArgs()});
        Check(trim.SectionsList.Items.Count==3,"Trimmer splits a kept section at the playhead");
        trim.GetType().GetMethod("Undo_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(trim,new object[]{trim,new RoutedEventArgs()});
        Check(trim.SectionsList.Items.Count==2,"Undo restores both original kept ranges");
        Render((FrameworkElement)trim.Content, 1020, 800, "trimmer-preview.png");
        trim.Close();
        if (interactive)
        {
            var live = new TrimWindow(source);
            try
            {
                live.Show(); SystemTestNative.ShowWindow(new System.Windows.Interop.WindowInteropHelper(live).Handle, 9);
                var deadline = Stopwatch.StartNew();
                while (!live.Player.NaturalDuration.HasTimeSpan && deadline.Elapsed.TotalSeconds < 12) await Task.Delay(100);
                Check(live.Player.NaturalDuration.HasTimeSpan && live.Player.NaturalVideoWidth == 640, "Windows opens the embedded H.264 preview with the correct dimensions");
                live.Player.Position = TimeSpan.Zero; live.Player.Play(); await Task.Delay(1800);
                Check(live.Player.Position.TotalSeconds >= 1, "Embedded preview playback advances");
                live.Player.Pause(); live.Player.Position = TimeSpan.FromSeconds(9); await Task.Delay(500);
                Check(Math.Abs(live.Player.Position.TotalSeconds - 9) < .2, "Embedded preview seeks to a selected time");
            }
            finally { live.Close(); }
        }
        var main = new MainWindow(true); main.MainTabs.SelectedItem = main.LibraryTab;
        await Task.Delay(300); Render((FrameworkElement)main.Content, 880, 780, "library-preview.png");
        Check(true, "Render populated library and multi-section trimmer layouts");
        await main.QuitAsync();
    }
    internal static async Task Ffmpeg(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }.Concat(args)) info.ArgumentList.Add(arg);
        using var job = new ChildProcessJob(); using var process = Process.Start(info)!; job.Add(process);
        var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); } finally { if (!process.HasExited) process.Kill(true); }
        string text = await errors; await output;
        if (process.ExitCode != 0 || text.Length > 0) throw new Exception("FFmpeg diagnostic failed: " + text);
    }
    internal static void Render(FrameworkElement content, int width, int height, string name)
    {
        var parent = content.Parent as ContentControl; if (parent != null) parent.Content = null;
        if (content.Parent is Border border) border.Child = null;
        var canvas = new Border { Child = content, Background = new SolidColorBrush(Color.FromRgb(17, 20, 25)), Width = width, Height = height };
        System.Windows.Documents.TextElement.SetFontFamily(canvas, new FontFamily("Segoe UI")); System.Windows.Documents.TextElement.SetFontSize(canvas, 14);
        System.Windows.Documents.TextElement.SetForeground(canvas, (Brush)Application.Current.FindResource("Ink"));
        canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Storage.Root, name)); png.Save(file);
    }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<double>
    { public void Report(double value) => source.Cancel(); }
}



