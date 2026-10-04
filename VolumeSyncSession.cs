using NAudio.CoreAudioApi;

namespace DualAudio;

internal interface IVolumeEndpoint : IDisposable
{
    float Volume { get; set; }
    bool Mute { get; set; }
}

internal sealed class WindowsVolumeEndpoint : IVolumeEndpoint
{
    private readonly MMDevice _device;
    public WindowsVolumeEndpoint(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(id);
    }
    public float Volume { get => _device.AudioEndpointVolume.MasterVolumeLevelScalar; set => _device.AudioEndpointVolume.MasterVolumeLevelScalar = value; }
    public bool Mute { get => _device.AudioEndpointVolume.Mute; set => _device.AudioEndpointVolume.Mute = value; }
    public void Dispose() => _device.Dispose();
}

// System state is owned by one sync session, never by persisted software coefficients.
internal sealed class VolumeSyncSession : IDisposable
{
    private readonly IVolumeEndpoint[] _endpoints;
    private readonly (float Volume, bool Mute)[] _original;
    private readonly double[] _last = new double[2];
    private readonly long[] _writes = new long[2];
    private readonly bool[] _restored = new bool[2];

    public VolumeSyncSession(IVolumeEndpoint source, IVolumeEndpoint target, VolumePolicy policy)
    {
        _endpoints = [source, target];
        // Read BOTH endpoints before changing either one.
        _original = [(source.Volume, source.Mute), (target.Volume, target.Mute)];
        try
        {
            foreach (var endpoint in _endpoints) endpoint.Mute = false;
            Apply(policy, true, true);
        }
        catch (Exception startError)
        {
            try { Restore(); }
            catch (Exception restoreError) { throw new AggregateException(startError, restoreError); }
            throw;
        }
    }

    public void Apply(VolumePolicy policy, bool source, bool target)
    {
        if (source) Write(0, policy.SourceOutput);
        if (target) Write(1, policy.TargetOutput);
    }

    private void Write(int index, double percent)
    {
        _endpoints[index].Volume = (float)(Math.Clamp(percent, 0, 100) / 100);
        _last[index] = _endpoints[index].Volume * 100d;
        _writes[index] = Environment.TickCount64;
    }

    public bool ReadExternalChanges(VolumePolicy policy)
    {
        var values = new[] { _endpoints[0].Volume * 100d, _endpoints[1].Volume * 100d };
        var changed = new bool[2];
        for (var i = 0; i < 2; i++)
        {
            var difference = Math.Abs(values[i] - _last[i]);
            // Ignore late hardware quantization of our own request, not genuine edits.
            changed[i] = difference > 0.01 && !(Environment.TickCount64 - _writes[i] < 1000 && difference < 0.5);
            _last[i] = values[i];
        }
        if (!changed[0] && !changed[1]) return false;
        if (policy.Master == 0)
        {
            // y>0 is impossible at z=0. Keep the user's global mute and remembered x.
            Apply(policy, changed[0], changed[1]);
            return false;
        }
        policy.ReadOutputs(values[0], values[1], changed[0], changed[1]);
        return true;
    }

    public void Restore()
    {
        var errors = new List<Exception>();
        for (var i = 0; i < 2; i++)
        {
            if (_restored[i]) continue;
            try
            {
                // If originally muted, restore mute before raising its saved volume.
                if (_original[i].Mute) _endpoints[i].Mute = true;
                _endpoints[i].Volume = _original[i].Volume;
                _endpoints[i].Mute = _original[i].Mute;
                _restored[i] = true;
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("部分设备的同步前音量或静音状态恢复失败", errors);
    }

    public void Dispose()
    {
        try { Restore(); }
        finally { foreach (var endpoint in _endpoints) endpoint.Dispose(); }
    }
}
