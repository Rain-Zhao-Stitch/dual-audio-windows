using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DualAudio;

// Windows process-loopback captures the application mix, not a speaker's attenuated
// signal. Excluding our process tree also prevents mirrored audio feeding back.
internal sealed class SystemAudioCapture : IWaveIn
{
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private readonly ManualResetEvent _stop = new(false);
    private AudioClient? _client;
    private Thread? _thread;
    private byte[] _bytes = [];
    private readonly uint? _includedProcess;
    private readonly uint? _excludedProcess;
    private readonly bool _passiveDiagnostic;
    public SystemAudioCapture(uint? includedProcess = null, uint? excludedProcess = null, bool passiveDiagnostic = false)
    {
        if (includedProcess.HasValue && excludedProcess.HasValue)
            throw new ArgumentException("Capture must either include or exclude a process tree, not both.");
        _includedProcess = includedProcess;
        _excludedProcess = excludedProcess;
        _passiveDiagnostic = passiveDiagnostic;
    }
    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public event Action<byte[], int, double?, bool>? TimestampedDataAvailable;

    public void StartRecording()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new NotSupportedException("独立设备音量需要 Windows 11 或 Windows build 20348 以上的进程音频捕获支持。");
        if (_thread is not null) throw new InvalidOperationException("音频捕获已启动");
        _stop.Reset();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            Exception? error = null;
            var comResult = CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                InitializeClient();
                var capture = _client!.AudioCaptureClient;
                _client.Start();
                if (_passiveDiagnostic) PassiveCaptureSession.MarkOwnCaptureSessions();
                started.TrySetResult();
                CaptureLoop(capture);
            }
            catch (Exception ex) { error = ex; started.TrySetException(ex); }
            finally
            {
                try { _client?.Stop(); } catch (Exception ex) { error ??= ex; }
                try { _client?.Dispose(); } catch (Exception ex) { error ??= ex; }
                _client = null;
                if (comResult >= 0) CoUninitialize();
                RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
            }
        }) { IsBackground = true, Name = "DualAudio system mix" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try { started.Task.GetAwaiter().GetResult(); }
        catch { StopRecording(); throw; }
    }

    private void InitializeClient()
    {
        var request = new ActivationRequest(_includedProcess, _excludedProcess);
        var task = request.Begin();
        try { _client = task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
        catch
        {
            // Activation can complete after a timeout; release that client as well.
            _ = task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, TaskScheduler.Default);
            throw;
        }
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            0, 0, WaveFormat, Guid.Empty);
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
    }

    private void CaptureLoop(AudioCaptureClient capture)
    {
        var waits = new WaitHandle[] { _stop, _ready };
        while (WaitHandle.WaitAny(waits, 100) != 0)
        {
            while (!_stop.WaitOne(0) && capture.GetNextPacketSize() > 0)
            {
                var pointer = capture.GetBuffer(out var frames, out var flags, out _, out var qpc);
                try
                {
                    var length = frames * WaveFormat.BlockAlign;
                    if (_bytes.Length < length) _bytes = new byte[length];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_bytes, 0, length);
                    else Marshal.Copy(pointer, _bytes, 0, length);
                    var time = qpc > 0 && ((int)flags & 4) == 0 ? qpc / 10_000_000.0 : (double?)null;
                    TimestampedDataAvailable?.Invoke(_bytes, length, time,
                        (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(_bytes, length));
                }
                finally { capture.ReleaseBuffer(frames); }
            }
        }
    }

    public void StopRecording()
    {
        _stop.Set();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
        _thread = null;
    }

    public void Dispose()
    {
        StopRecording();
        _ready.Dispose();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct BlobVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationRequest : IActivateAudioInterfaceCompletionHandler, IAgileCallbackMarker
    {
        private readonly TaskCompletionSource<AudioClient> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr _parameters;
        private IntPtr _variant;
        private readonly uint? _includedProcess;
        private readonly uint? _excludedProcess;
        public ActivationRequest(uint? includedProcess, uint? excludedProcess)
        {
            _includedProcess = includedProcess;
            _excludedProcess = excludedProcess;
        }

        public Task<AudioClient> Begin()
        {
            if (IntPtr.Size != 8) throw new NotSupportedException("请使用 64 位程序");
            _parameters = Marshal.AllocHGlobal(12);
            // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK / EXCLUDE_TARGET_PROCESS_TREE.
            Marshal.WriteInt32(_parameters, 0, 1);
            Marshal.WriteInt32(_parameters, 4, unchecked((int)(_includedProcess ?? _excludedProcess ?? (uint)Environment.ProcessId)));
            Marshal.WriteInt32(_parameters, 8, _includedProcess.HasValue ? 0 : 1);
            _variant = Marshal.AllocHGlobal(24);
            Marshal.StructureToPtr(new BlobVariant { Type = 65, Size = 12, Data = _parameters }, _variant, false);
            try
            {
                var hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, _variant, this, out var operation);
                if (operation is not null) Marshal.ReleaseComObject(operation);
                Marshal.ThrowExceptionForHR(hr);
            }
            catch { ReleaseParameters(); throw; }
            return _completion.Task;
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? result = null;
            try
            {
                operation.GetActivateResult(out var hr, out result);
                Marshal.ThrowExceptionForHR(hr);
                var client = new AudioClient((IAudioClient)result);
                result = null; // Ownership passes to AudioClient.
                _completion.TrySetResult(client);
            }
            catch (Exception ex) { _completion.TrySetException(ex); }
            finally
            {
                if (result is not null && Marshal.IsComObject(result)) Marshal.ReleaseComObject(result);
                ReleaseParameters();
            }
        }

        private void ReleaseParameters()
        {
            var variant = Interlocked.Exchange(ref _variant, IntPtr.Zero);
            var parameters = Interlocked.Exchange(ref _parameters, IntPtr.Zero);
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (parameters != IntPtr.Zero) Marshal.FreeHGlobal(parameters);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, IntPtr activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}

[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileCallbackMarker { }
