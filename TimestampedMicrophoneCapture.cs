using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio;

// Packet timestamps come from WASAPI's QPC clock, not callback delivery time.
internal sealed class TimestampedMicrophoneCapture : IDisposable
{
    internal sealed record Packet(byte[] Bytes, double TimeSeconds, bool Discontinuity);
    private readonly ManualResetEvent _stop = new(false);
    private readonly List<Packet> _packets = [];
    private readonly Thread _thread;
    private Exception? _error;
    public WaveFormat Format { get; private set; } = null!;

    public TimestampedMicrophoneCapture(string deviceId)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                using var client = device.AudioClient;
                Format = client.MixFormat;
                using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                    1_000_000, 0, Format, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var capture = client.AudioCaptureClient;
                client.Start();
                started.TrySetResult();
                try
                {
                    while (WaitHandle.WaitAny([_stop, ready], 100) != 0)
                    {
                        while (!_stop.WaitOne(0) && capture.GetNextPacketSize() > 0)
                        {
                            var pointer = capture.GetBuffer(out var frames, out var flags, out _, out var qpc);
                            try
                            {
                                if (frames == 0) continue;
                                if (((int)flags & 4) != 0 || qpc <= 0)
                                    throw new InvalidOperationException("麦克风提供了无效时间戳，无法准确计时。请更换录音设备。");
                                var bytes = new byte[frames * Format.BlockAlign];
                                if ((flags & AudioClientBufferFlags.Silent) == 0)
                                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                                _packets.Add(new Packet(bytes, qpc / 10_000_000.0,
                                    (flags & AudioClientBufferFlags.DataDiscontinuity) != 0));
                            }
                            finally { if (frames > 0) capture.ReleaseBuffer(frames); }
                        }
                    }
                }
                finally { client.Stop(); }
            }
            catch (Exception ex) { _error = ex; started.TrySetException(ex); }
        }) { IsBackground = true, Name = "DualAudio microphone timestamps" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try { started.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public Packet[] Finish()
    {
        _stop.Set();
        _thread.Join();
        if (_error is not null) throw new InvalidOperationException("麦克风录音失败。", _error);
        return _packets.ToArray();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join();
        _stop.Dispose();
    }
}
