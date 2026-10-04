using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio;

// All WASAPI objects live on one MTA thread. Only queue two engine periods,
// even when a driver allocates a much larger endpoint buffer.
internal sealed class LowLatencyWasapiOutput : IDisposable
{
    private readonly string _deviceId;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private ISampleProvider? _provider;
    private Action<double>? _presentationTime;
    public double QueueMilliseconds { get; private set; }
    public double DevicePeriodMilliseconds { get; private set; }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public LowLatencyWasapiOutput(string deviceId) => _deviceId = deviceId;

    public void Init(ISampleProvider provider, Action<double>? presentationTime = null)
    {
        _provider = provider;
        _presentationTime = presentationTime;
    }

    public void Play()
    {
        if (_thread is not null) throw new InvalidOperationException("播放已启动。");
        var provider = _provider ?? throw new InvalidOperationException("播放源尚未初始化。");
        _stop.Reset();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            Exception? error = null;
            IntPtr mmcss = IntPtr.Zero;
            var comResult = CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(_deviceId);
                using var client = device.AudioClient;
                var format = provider.WaveFormat;
                var period = Math.Max(10_000, client.DefaultDevicePeriod);
                DevicePeriodMilliseconds = period / 10_000.0;
                using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                    2 * period, 0, format, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var periodFrames = Math.Max(1, (int)Math.Ceiling(period * format.SampleRate / 10_000_000.0));
                var queuedFrames = Math.Min(client.BufferSize, 2 * periodFrames);
                var block = new float[queuedFrames * format.Channels];
                var render = client.AudioRenderClient;
                AudioClockClient? clock = null;
                ulong frequency = 0;
                try { clock = client.AudioClockClient; frequency = clock.Frequency; }
                catch (COMException) { } // Queue bounds still work on devices without a usable clock.
                long submittedFrames = 0;
                mmcss = AvSetMmThreadCharacteristics("Pro Audio", out _);

                void Fill()
                {
                    var padding = client.CurrentPadding;
                    QueueMilliseconds = padding * 1000.0 / format.SampleRate;
                    var frames = Math.Min(client.BufferSize - padding, queuedFrames - padding);
                    if (frames <= 0) return;
                    var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    var presentation = now + padding / (double)format.SampleRate;
                    try
                    {
                        if (clock is not null && frequency > 0 && clock.GetPosition(out var position, out var qpc) && qpc > 0)
                        {
                            var playedSeconds = position / (double)frequency;
                            var pendingSeconds = submittedFrames / (double)format.SampleRate - playedSeconds;
                            // After an underrun the hardware clock may have advanced through silence.
                            if (pendingSeconds < 0 || pendingSeconds > (padding + 2 * periodFrames) / (double)format.SampleRate)
                                submittedFrames = (long)Math.Round(playedSeconds * format.SampleRate) + padding;
                            var predicted = qpc / 10_000_000.0 + submittedFrames / (double)format.SampleRate - playedSeconds;
                            if (predicted >= now - 0.010 && predicted <= now + 0.250)
                                presentation = Math.Max(now, predicted);
                        }
                    }
                    catch (COMException) { clock = null; } // Fall back to QPC + measured endpoint padding.
                    _presentationTime?.Invoke(presentation);
                    var samples = frames * format.Channels;
                    var read = provider.Read(block, 0, samples);
                    if (read < samples) Array.Clear(block, read, samples - read);
                    var pointer = render.GetBuffer(frames);
                    try { Marshal.Copy(block, 0, pointer, samples); }
                    finally { render.ReleaseBuffer(frames, AudioClientBufferFlags.None); }
                    submittedFrames += frames;
                }

                Fill();
                client.Start();
                started.TrySetResult();
                try
                {
                    var waits = new WaitHandle[] { _stop, ready };
                    var timeout = Math.Max(2, (int)Math.Ceiling(DevicePeriodMilliseconds));
                    while (WaitHandle.WaitAny(waits, timeout) != 0) Fill();
                }
                finally { client.Stop(); }
            }
            catch (Exception ex) { error = ex; started.TrySetException(ex); }
            finally
            {
                if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
                if (comResult >= 0) CoUninitialize();
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
            }
        }) { IsBackground = true, Name = "DualAudio low-latency render" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try { started.Task.GetAwaiter().GetResult(); }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        _stop.Set();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
        _thread = null;
    }

    public void Dispose() { Stop(); _stop.Dispose(); }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string task, out uint index);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
