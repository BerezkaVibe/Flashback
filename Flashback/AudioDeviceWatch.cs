using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Flashback;

internal sealed class AudioDeviceWatch(string deviceId, bool followDefault, DataFlow deviceFlow = DataFlow.Render, Role deviceRole = Role.Multimedia) : IMMNotificationClient
{
    private int changed, lost, returned;
    internal bool Changed => Volatile.Read(ref changed) != 0;
    // A held (locked) device reports loss and return instead of forcing a reconnect.
    internal bool Hold { get; set; }
    internal bool FollowDefault { get; set; } = followDefault;
    internal bool TakeLost() => Interlocked.Exchange(ref lost, 0) != 0;
    internal bool TakeReturned() => Interlocked.Exchange(ref returned, 0) != 0;
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (FollowDefault && !Hold && flow == deviceFlow && role == deviceRole && !string.Equals(defaultDeviceId, deviceId, StringComparison.Ordinal))
            Interlocked.Exchange(ref changed, 1);
    }
    public void OnDeviceStateChanged(string id, DeviceState state)
    {
        if (id != deviceId) return;
        if (Hold) { if (state == DeviceState.Active) Interlocked.Exchange(ref returned, 1); else Interlocked.Exchange(ref lost, 1); }
        else if (state != DeviceState.Active) Interlocked.Exchange(ref changed, 1);
    }
    public void OnDeviceRemoved(string id) => OnDeviceStateChanged(id, DeviceState.NotPresent);
    public void OnDeviceAdded(string id) { if (Hold && id == deviceId) Interlocked.Exchange(ref returned, 1); }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}
