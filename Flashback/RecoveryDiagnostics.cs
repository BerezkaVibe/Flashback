using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal static class RecoveryDiagnostics
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string lost = "[Parsed_ddagrab_0] AcquireNextFrame failed: 887a0026";
        const string released = "Audio capture stopped. Pipe is broken.\n[Parsed_ddagrab_0 @ 00000204c144cbc0] DDA ReleaseFrame failed!\nEOF timestamp not reliable\nError requesting a frame from the filtergraph: Generic error in an external library";
        check(CaptureRecovery.IsRecoverable(released) && CaptureRecovery.IsRecoverable(released.ToUpperInvariant()), "Reported DDA ReleaseFrame failure is recoverable even when an audio broken-pipe message comes first");
        check(!CaptureRecovery.IsRecoverable("Generic error in an external library") && !CaptureRecovery.IsRecoverable("Pipe is broken."), "Unrelated encoder and pipe failures are not blindly retried");
        check(CaptureRecovery.IsRecoverable(Recorder.FailureDetails("Audio capture stopped. Pipe is broken.", lost)), "Broken audio pipe retains the underlying recoverable DXGI cause");
        check(!CaptureRecovery.IsRecoverable(Recorder.FailureDetails("Audio capture stopped. Pipe is broken.", "Guessed Channel Layout: stereo")), "Pipe failure alone does not cause blind capture retries");
        var micWatch = new AudioDeviceWatch("mic", true, NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
        micWatch.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Communications, "headset");
        check(!micWatch.Changed, "Microphone watch ignores playback changes");
        micWatch.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications, "new-mic");
        check(micWatch.Changed && CaptureRecovery.IsRecoverable(AudioLoopback.MicrophoneChangedMessage), "Default microphone changes trigger reconnection");
        var micSettings = new Settings { MicrophoneAudio = true, MicrophoneDeviceId = "mic" };
        check(micSettings.Copy().MicrophoneDeviceId == "mic" && micSettings.Copy().MicrophoneAudio && micSettings.RequiresBufferRestart(new Settings()), "Microphone settings persist and rebuild the pipeline when changed");
        check(!new Settings().MicrophoneAudio, "Existing settings leave microphone recording off by default");
        check(Hotkeys.Parse("F8").Modifiers == 0x4000 && Hotkeys.Parse("A").Key == 0x41, "Single function and letter keys are accepted with repeat suppression");
        check(Hotkeys.Parse("Ctrl+Shift+F8").Modifiers == 0x4006, "Existing modifier shortcuts retain their bindings");
        foreach (var invalid in new[] { "Ctrl", "LeftShift", "F8+F9", "None" })
        {
            try { Hotkeys.Parse(invalid); throw new Exception("Invalid shortcut accepted"); }
            catch (ArgumentException) { check(true, "Reject incomplete or ambiguous shortcut: " + invalid); }
        }
        using (var first = new Hotkeys(allowShared: false))
        using (var second = new Hotkeys(allowShared: false))
        {
            first.Register("F22");
            try { second.Register("F22"); throw new Exception("Duplicate single key accepted"); }
            catch (InvalidOperationException) { check(true, "Single-key registration retains conflict detection"); }
        }
        check(CaptureRecovery.IsRecoverable(AudioLoopback.StreamStalledMessage), "A stalled audio sample stream can reconnect automatically");
        check(CaptureRecovery.IsAccessLost(lost.ToUpperInvariant()), "Capture access-loss code is recognized regardless of case");
        check(!CaptureRecovery.IsAccessLost("Guessed Channel Layout: stereo") && !CaptureRecovery.IsAccessLost("AcquireNextFrame failed: 887a0005"), "Audio warnings and other GPU errors do not trigger capture recovery");
        var autoAudio = new AudioDeviceWatch("headphones", true);
        autoAudio.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia, "microphone");
        autoAudio.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Communications, "chat");
        check(!autoAudio.Changed, "Unrelated microphone and communication endpoint changes do not interrupt desktop audio");
        autoAudio.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia, "monitor-speakers");
        check(autoAudio.Changed && CaptureRecovery.IsRecoverable(AudioLoopback.DeviceChangedMessage), "Windows playback changes trigger automatic audio reconnection");
        var fixedAudio = new AudioDeviceWatch("headphones", false);
        fixedAudio.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia, "monitor-speakers");
        check(!fixedAudio.Changed, "Pinned headphones stay selected when the Windows default changes");
        fixedAudio.OnDeviceRemoved("headphones");
        check(fixedAudio.Changed, "Disconnected pinned devices are detected");
        var preferences = new Settings { AudioDeviceId = "headphones" };
        check(preferences.Copy().AudioDeviceId == "headphones" && preferences.RequiresBufferRestart(new Settings()), "Audio source persists and changing it rebuilds the recording pipeline");
        var time = DateTimeOffset.UtcNow;
        var recovery = new CaptureRecovery(() => time, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        int attempts = 0;
        await recovery.RunAsync(_ => ++attempts < 3 ? Task.FromException(new IOException(lost)) : Task.CompletedTask, _ => { });
        check(attempts == 3 && !recovery.IsRunning, "Transient access loss reconnects after bounded retries");
        var delays = new System.Collections.Generic.List<double>();
        recovery = new CaptureRecovery(delay: (duration, token) => { delays.Add(duration.TotalSeconds); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        attempts = 0;
        await recovery.RunAsync(_ => ++attempts < 25 ? Task.FromException(new IOException(lost)) : Task.CompletedTask, _ => { });
        check(attempts == 25 && delays.TrueForAll(d => d <= 10), "Recovery continues beyond three failures with capped backoff");
        await recovery.RunAsync(_ => { attempts++; return Task.CompletedTask; }, _ => { });
        check(attempts == 26, "Repeated interruptions never exhaust a shared retry budget");
        recovery.ResetBudget(); attempts = 0;
        try { await recovery.RunAsync(_ => { attempts++; return Task.FromException(new IOException("Disk full")); }, _ => { }); }
        catch (IOException) { check(attempts == 1, "Unrelated restart failures are not retried"); }
        var cancel = new CaptureRecovery(delay: (_, token) => Task.Delay(10000, token));
        var pending = cancel.RunAsync(_ => { attempts++; return Task.CompletedTask; }, _ => { });
        cancel.Cancel();
        try { await pending; throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { check(attempts == 1 && !cancel.IsRunning, "Pause cancels a pending retry before launching another encoder"); }
        await using var recorder = new Recorder();
        using var cts = new CancellationTokenSource(400);
        try { await recorder.StartAsync(new Settings { DesktopAudio = false, OutputFolder = Path.Combine(Storage.Root, "cancel-clips") }, true, cts.Token); throw new Exception("Startup cancellation ignored"); }
        catch (OperationCanceledException) { check(!recorder.IsRecording, "Cancellation during capture startup stops its child process"); }
    }
}

