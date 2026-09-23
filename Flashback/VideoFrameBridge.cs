using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;


namespace Flashback;

// A paced writer owns the video clock. Capture loss never closes the encoder inputs.
internal sealed class VideoFrameBridge : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<string, Process> launch;
    private readonly Action<string> report;
    private readonly Func<FramePool, GpuDesktopCapture>? nativeCapture;
    private readonly FramePool pool;
    // Capture gaps: time from losing the display to the next fresh frame.
    internal long Recoveries;
    internal double LastGapMilliseconds, LongestGapMilliseconds;
    private long gapStarted;
    private readonly int frameBytes;
    private readonly byte[] black;
    private readonly FrameHistory frames;
    internal long DroppedFrames => frames.Dropped;
    internal double MaxWriteMilliseconds;
    internal long SlowWrites;
    private readonly int fps;
    internal double ProducerCpu { get { try { return producer?.TotalProcessorTime.TotalSeconds ?? 0; } catch { return 0; } } }
    internal long ProducerMemory { get { try { return producer?.WorkingSet64 ?? 0; } catch { return 0; } } }
    internal long ReceivedFrames;
    internal long RepeatedFrames;
    internal long GpuBusyReads, GpuSubmittedFrames;
    internal double MaxCaptureMilliseconds;
    internal double CaptureMilliseconds;
    internal long CaptureCalls;
    internal long TimelineOrigin;
    private long writtenFrames;
    internal double EncoderLagSeconds => TimelineOrigin==0 ? 0 : Math.Max(0,Stopwatch.GetElapsedTime(Interlocked.Read(ref TimelineOrigin)).TotalSeconds-Interlocked.Read(ref writtenFrames)/(double)fps);

    private Process? producer;
    private Task? capture, writer;
    private long holdUntil;
    internal string InputPath { get; }
    internal string PixelFormat { get; }
    internal Exception? Failure { get; private set; }
    internal int Connections { get; private set; }

    internal VideoFrameBridge(int width, int height, string pixelFormat, int fps, Func<string, Process> launch, Action<string> report, Func<FramePool, GpuDesktopCapture>? nativeCapture = null)
    {
        this.fps = fps; this.launch = launch; this.report = report; this.nativeCapture = nativeCapture; PixelFormat = pixelFormat;
        frameBytes = checked(width * height * (pixelFormat == "nv12" ? 3 : 8) / 2);
        pool = new FramePool(frameBytes, FrameHistory.CapacityFor(frameBytes, fps) + 4);
        frames = new FrameHistory(pool, fps);
        black = new byte[frameBytes];
        if (pixelFormat == "nv12") { Array.Fill(black, (byte)16, 0, width * height); Array.Fill(black, (byte)128, width * height, frameBytes - width * height); }
        else for (int i = 3; i < black.Length; i += 4) black[i] = 255;
        string name = "flashback-video-" + Guid.NewGuid().ToString("N");
        InputPath = @"\\.\pipe\" + name;
        pipe = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 65536);
    }
    internal void Start()
    {
        capture = Task.Run(() => CaptureAsync(cancellation.Token));
        writer = Task.Factory.StartNew(() => WriteLoop(cancellation.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    internal void InterruptForTest(TimeSpan duration)
    {
        Interlocked.Exchange(ref holdUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));

        try { producer?.Kill(true); } catch { }
    }
    private async Task CaptureAsync(CancellationToken token)
    {
        if (nativeCapture != null && await Task.Factory.StartNew(() => CaptureNative(token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)) return;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            Process? running = null;
            Task? errors = null;
            byte[]? buffer = null;
            var uptime = Stopwatch.StartNew();
            try
            {
                while (Stopwatch.GetTimestamp() < Interlocked.Read(ref holdUntil)) await Task.Delay(50, token);
                string captureName = "flashback-frames-" + Guid.NewGuid().ToString("N");
                using var capturePipe = new NamedPipeServerStream(captureName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024 * 1024, 4096);
                running = launch(@"\\.\pipe\" + captureName); producer = running; Connections++;
                errors = Task.Run(async () => { while (await running.StandardError.ReadLineAsync(token) is { } line) report(line); }, token);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                { connect.CancelAfter(TimeSpan.FromSeconds(10)); await capturePipe.WaitForConnectionAsync(connect.Token); }
                while (true)
                {
                    buffer = pool.Rent();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await capturePipe.ReadExactlyAsync(buffer.AsMemory(0, frameBytes), timeout.Token);

                    Interlocked.Increment(ref ReceivedFrames);
                    frames.Publish(buffer, Stopwatch.GetTimestamp(), true); buffer = null;
                    failures = 0;
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { report("Display capture unavailable; recording black frames. " + ex.Message); }
            catch (OperationCanceledException) { }
            finally
            {
        
                frames.Publish(null, Stopwatch.GetTimestamp(), false);
                pool.Return(buffer);
                try { if (running is { HasExited: false }) running.Kill(true); } catch { }
                if (errors != null) try { await errors; } catch { }
                producer = null; running?.Dispose();
            }
            if (token.IsCancellationRequested) break;
            try { await Task.Delay(RetryDelay(failures++), token); }
            catch (OperationCanceledException) { break; }
        }
    }
    // Reconnect immediately, then back off gently; a missing display is retried at least once a second.
    private void EndGap(long now)
    {
        double ms = (now - gapStarted) * 1000.0 / Stopwatch.Frequency;
        gapStarted = 0; Interlocked.Increment(ref Recoveries);
        LastGapMilliseconds = ms; LongestGapMilliseconds = Math.Max(LongestGapMilliseconds, ms);
        string line = $"{DateTimeOffset.Now:O} display gap {ms:0} ms\n";
        _ = Task.Run(() =>
        {
            try
            {
                var path = Path.Combine(Storage.Root, "capture-gaps.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, line);
            }
            catch { }
        });
    }
    internal static TimeSpan RetryDelay(int failures) => TimeSpan.FromMilliseconds(failures switch { 0 => 0, 1 => 50, 2 => 100, 3 => 250, 4 => 500, _ => 1000 });
    internal bool UsingNative { get; private set; }
    private bool CaptureNative(CancellationToken token)
    {
        int failures = 0;
        using var cadence = new FrameCadence(fps);
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (Stopwatch.GetTimestamp() < Interlocked.Read(ref holdUntil))
                    if (token.WaitHandle.WaitOne(25)) return true;
                using var source = nativeCapture!(pool); Connections++; UsingNative = true;
                long previousBusy = 0, previousSubmitted = 0;
                report("Native DXGI capture: GPU resize/color conversion, NV12 readback.");
                cadence.Reset();
                while (!token.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() < Interlocked.Read(ref holdUntil)) break;
                    cadence.Wait();
                    long captureStarted = Stopwatch.GetTimestamp();
                    var frame = source.Read();
                    double work = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
                    MaxCaptureMilliseconds = Math.Max(MaxCaptureMilliseconds, work); CaptureMilliseconds += work; CaptureCalls++;
                    GpuBusyReads += source.BusyReads-previousBusy; GpuSubmittedFrames += source.SubmittedFrames-previousSubmitted;
                    previousBusy=source.BusyReads; previousSubmitted=source.SubmittedFrames;

                    long now = Stopwatch.GetTimestamp();
                    if (frame != null)
                    {
                        Interlocked.Increment(ref ReceivedFrames);
                        failures = 0;
                        if (gapStarted != 0) EndGap(now);
                    }
                    else if (source.Lost && gapStarted == 0) gapStarted = now;
                    // Brief gaps hold the last picture; longer ones fall back to black.
                    frames.Publish(frame, now, gapStarted == 0 || Stopwatch.GetElapsedTime(gapStarted).TotalSeconds < .5);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested && (ex is NotSupportedException ||
                ex.HResult == unchecked((int)0x80070057) || ex.HResult == unchecked((int)0x80004002) ||
                ex.HResult == unchecked((int)0x80004001) || ex.HResult == unchecked((int)0x887A0004)))
            {
                UsingNative = false;
                report("Native GPU conversion unavailable; using compatibility capture. " + ex.Message);
                return false;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                if (gapStarted == 0) gapStarted = Stopwatch.GetTimestamp();
                report("Display capture unavailable; rebuilding capture. " + ex.Message);
            }
            catch (OperationCanceledException) { }
            // No black frame is published here: the writer holds the last picture briefly
            // and only turns black if the rebuild takes longer.
            if (token.WaitHandle.WaitOne(RetryDelay(failures++))) break;
        }
        return true;
    }
    private void WriteLoop(CancellationToken token)
    {

        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        try
        {
            if (timer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            long origin = Stopwatch.GetTimestamp(), count = 0;
            Interlocked.Exchange(ref TimelineOrigin, origin);
            while (!token.IsCancellationRequested)
            {
                double remaining = count / (double)fps - Stopwatch.GetElapsedTime(origin).TotalSeconds;
                if (remaining > 0)
                {
                    long due = -(long)(remaining * 10000000);
                    if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new System.ComponentModel.Win32Exception();
                    WaitForSingleObject(timer, 1000);
                }
                long target = origin + (long)(count * (double)Stopwatch.Frequency / fps);
                var picture = frames.Select(target, out bool changed);
                if (!changed) Interlocked.Increment(ref RepeatedFrames);
                long writeStarted = Stopwatch.GetTimestamp();
                pipe.Write(picture ?? black, 0, frameBytes);
                double writeMs = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds;
                MaxWriteMilliseconds = Math.Max(MaxWriteMilliseconds, writeMs);
                if (writeMs > 1000.0 / fps) SlowWrites++;
                count++; Interlocked.Exchange(ref writtenFrames,count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested) { Failure = ex; }
        finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { producer?.Kill(true); } catch { }
        pipe.Dispose();
        if (capture != null) try { await capture; } catch { }
        if (writer != null) try { await writer; } catch { }
        frames.Dispose();
        cancellation.Dispose();
    }
}




