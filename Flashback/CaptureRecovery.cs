using System;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal sealed class CaptureRecovery
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? cancellation;
    internal bool IsRunning { get; private set; }
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal CaptureRecovery(Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { this.delay = delay ?? Task.Delay; }
    internal static bool IsAccessLost(string error) => error.Contains("DXGI_ERROR_ACCESS_LOST", StringComparison.OrdinalIgnoreCase)
        || error.Contains("AcquireNextFrame failed", StringComparison.OrdinalIgnoreCase) && error.Contains("887a0026", StringComparison.OrdinalIgnoreCase);
    internal static bool IsDuplicationInterrupted(string error) => IsAccessLost(error) || error.Contains("DDA ReleaseFrame failed", StringComparison.OrdinalIgnoreCase);
    internal static bool IsRecoverable(string error) => error.Contains(EncoderLagMonitor.Message,StringComparison.Ordinal) || error.Contains(AudioLoopback.BacklogMessage,StringComparison.Ordinal) || error.Contains(AudioClockNormalizer.Discontinuity,StringComparison.Ordinal) || IsDuplicationInterrupted(error) || error.Contains(AudioLoopback.DeviceChangedMessage, StringComparison.Ordinal) || error.Contains(AudioLoopback.StreamStalledMessage, StringComparison.Ordinal)
        || error.Contains(AudioLoopback.MicrophoneChangedMessage, StringComparison.Ordinal) || error.Contains(AudioLoopback.MicrophoneStalledMessage, StringComparison.Ordinal)
        || error.Contains("selected audio device is disconnected", StringComparison.OrdinalIgnoreCase)
        || error.Contains("selected display is no longer connected", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Could not find a desktop capture adapter", StringComparison.OrdinalIgnoreCase)
        || error.Contains("0x88890004", StringComparison.OrdinalIgnoreCase) // AUDCLNT_E_DEVICE_INVALIDATED
        || error.Contains("0x88890010", StringComparison.OrdinalIgnoreCase); // AUDCLNT_E_SERVICE_NOT_RUNNING
    internal void ResetBudget() { }
    internal void Cancel() => cancellation?.Cancel();
    internal Task RunAsync(Func<CancellationToken, Task> restart, Action<int> status)
    {
        if (IsRunning) return Completion;
        cancellation?.Dispose(); cancellation = new(); IsRunning = true;
        return Completion = RunCoreAsync(restart, status, cancellation.Token);
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> restart, Action<int> status, CancellationToken token)
    {
        try
        {
            for (int attempt = 1; ; attempt = Math.Min(attempt + 1, 1000000))
            {
                token.ThrowIfCancellationRequested(); status(attempt);
                await delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt - 1, 4))), token);
                token.ThrowIfCancellationRequested();
                try { await restart(token); token.ThrowIfCancellationRequested(); return; }
                catch (Exception ex) when (IsRecoverable(ex.ToString()) && !token.IsCancellationRequested) { }
            }
        }
        finally { IsRunning = false; }
    }
}

