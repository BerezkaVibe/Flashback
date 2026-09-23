using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Flashback;

internal sealed class AudioDeviceWatch(string deviceId, bool followDefault, DataFlow deviceFlow = DataFlow.Render, Role deviceRole = Role.Multimedia) : IMMNotificationClient
{
    private int changed;
    internal bool Changed => Volatile.Read(ref changed) != 0;
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (followDefault && flow == deviceFlow && role == deviceRole && !string.Equals(defaultDeviceId, deviceId, StringComparison.Ordinal))
            Interlocked.Exchange(ref changed, 1);
    }
    public void OnDeviceStateChanged(string id, DeviceState state)
    { if (id == deviceId && state != DeviceState.Active) Interlocked.Exchange(ref changed, 1); }
    public void OnDeviceRemoved(string id) => OnDeviceStateChanged(id, DeviceState.NotPresent);
    public void OnDeviceAdded(string id) { }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}
