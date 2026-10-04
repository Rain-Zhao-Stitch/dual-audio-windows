using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DualAudio;

// Only changes output routing. Never writes ISimpleAudioVolume or session mute.
// Policy ABI references: EarTrumpet's WindowsAudio AudioPolicyConfigService and
// IAudioPolicyConfigFactoryVariantFor21H2/Downlevel (File-New-Project/EarTrumpet).
internal sealed class PlaybackRoutingSession : IDisposable
{
    private sealed record SavedRoute(uint ProcessId, long ProcessStart, Role Role, string? Endpoint);
    private readonly Dictionary<Role, string> _defaults = [];
    private readonly Dictionary<(uint Process, Role Role), SavedRoute> _routes = [];
    private readonly Dictionary<uint, DateTime> _attempts = [];
    private readonly Dictionary<uint, DateTime> _conflicts = [];
    private string? _directId;
    private string? _delayedId;
    private bool _disposed;

    public PlaybackRoutingSession()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var role in Enum.GetValues<Role>())
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            _defaults.Add(role, endpoint.ID);
        }
    }

    public void Configure(string directId, string delayedId)
    {
        _directId = directId;
        _delayedId = delayedId;
        _attempts.Clear();
        _conflicts.Clear();
        DefaultAudioDeviceManager.SetDefaultRenderDevice(directId);
        // Give normal default-following streams time to migrate before touching
        // any per-app assignment; most applications need no explicit routing.
        Thread.Sleep(500);
        // Explicit per-app assignments do not necessarily follow a global default.
        RedirectSessions();
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 2500)
        {
            Thread.Sleep(100);
            if (GetForeignSessions(delayedId, audibleOnly: false).Count == 0) return;
        }
        var remaining = GetForeignSessions(delayedId, audibleOnly: false);
        if (remaining.Count != 0) ThrowRouteConflict(remaining);
    }

    public void CheckForNewStreams()
    {
        if (_disposed || _delayedId is null) return;
        RedirectSessions();
        var audible = GetForeignSessions(_delayedId, audibleOnly: true);
        var now = DateTime.UtcNow;
        foreach (var stale in _conflicts.Keys.Except(audible).ToArray()) _conflicts.Remove(stale);
        foreach (var process in audible)
        {
            if (!_conflicts.TryGetValue(process, out var since)) _conflicts.Add(process, now);
            else if ((now - since).TotalSeconds > 2) ThrowRouteConflict(audible);
        }
    }

    private void RedirectSessions()
    {
        if (_directId is null || _delayedId is null) return;
        var processes = GetForeignSessions(_delayedId, audibleOnly: false);
        if (processes.Count == 0) return;
        using var policy = new ApplicationOutputPolicy();
        foreach (var processId in processes)
        {
            if (_attempts.TryGetValue(processId, out var last) && (DateTime.UtcNow - last).TotalSeconds < 3) continue;
            _attempts[processId] = DateTime.UtcNow;
            long processStart;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processStart = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (ArgumentException) { continue; }
            foreach (var role in Enum.GetValues<Role>())
            {
                var key = (processId, role);
                if (!_routes.TryGetValue(key, out var saved) || saved.ProcessStart != processStart)
                    _routes[key] = new SavedRoute(processId, processStart, role, policy.Get(processId, role));
                policy.Set(processId, role, ApplicationOutputPolicy.Pack(_directId));
            }
        }
    }

    private static HashSet<uint> GetForeignSessions(string endpointId, bool audibleOnly)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(endpointId);
        var manager = endpoint.AudioSessionManager;
        try
        {
            var result = new HashSet<uint>();
            var sessions = manager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                // Capture sessions can be enumerated on rendering endpoints and
                // expose a nonzero peak. Only our explicitly marked passive
                // recordings are exempt; actual foreign playback remains guarded.
                if (PassiveCaptureSession.IsDiagnosticCapture(session)) continue;
                var processId = session.GetProcessID;
                if (processId == 0 || processId == (uint)Environment.ProcessId || session.IsSystemSoundsSession) continue;
                if (audibleOnly && (session.AudioMeterInformation?.MasterPeakValue ?? 0) < 0.0001f) continue;
                result.Add(processId);
            }
            return result;
        }
        finally { manager.Dispose(); }
    }

    private static void ThrowRouteConflict(IEnumerable<uint> processes)
    {
        var names = processes.Select(id =>
        {
            try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
            catch { return $"PID {id}"; }
        });
        throw new InvalidOperationException("以下应用仍直接向需要延迟的设备播放，会与镜像叠成双重声音：" +
            string.Join("、", names) + "。请将播放器的输出选择改成“系统默认”，或重新打开播放后再同步。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var errors = new List<Exception>();
        if (_routes.Count != 0)
        {
            try
            {
                using var policy = new ApplicationOutputPolicy();
                foreach (var saved in _routes.Values)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)saved.ProcessId);
                        if (process.StartTime.ToUniversalTime().Ticks != saved.ProcessStart)
                            throw new InvalidOperationException($"PID {saved.ProcessId} 已被另一个程序复用，未写入它的输出设置。");
                        policy.Set(saved.ProcessId, saved.Role, saved.Endpoint);
                    }
                    catch (Exception ex) { errors.Add(new InvalidOperationException($"无法恢复应用 {saved.ProcessId} 的输出路由。", ex)); }
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var (role, endpointId) in _defaults)
        {
            try { DefaultAudioDeviceManager.SetDefaultRenderDevice(endpointId, role); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException("部分输出路由未能恢复；请在 Windows 音量合成器检查应用的输出设备。", errors);
    }

    private sealed class ApplicationOutputPolicy : IDisposable
    {
        private IntPtr _factory;
        private readonly bool _uninitialize;
        private readonly SetEndpoint _set = null!;
        private readonly GetEndpoint _get = null!;

        public ApplicationOutputPolicy()
        {
            var hr = RoInitialize(1);
            Marshal.ThrowExceptionForHR(hr);
            _uninitialize = true;
            IntPtr name = IntPtr.Zero;
            try
            {
                const string runtimeClass = "Windows.Media.Internal.AudioPolicyConfig";
                Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out name));
                var iid = new Guid(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    ? "ab3d4648-e242-459f-b02f-541c70306324" : "2a59116d-6c4f-45e0-a74f-707e3fef9258");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out _factory));
                var table = Marshal.ReadIntPtr(_factory);
                // IInspectable has six slots; nineteen policy methods precede these two.
                _set = Marshal.GetDelegateForFunctionPointer<SetEndpoint>(Marshal.ReadIntPtr(table, 25 * IntPtr.Size));
                _get = Marshal.GetDelegateForFunctionPointer<GetEndpoint>(Marshal.ReadIntPtr(table, 26 * IntPtr.Size));
            }
            catch { Dispose(); throw; }
            finally { if (name != IntPtr.Zero) WindowsDeleteString(name); }
        }

        public static string Pack(string endpointId)
            => @"\\?\SWD#MMDEVAPI#" + endpointId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        public string? Get(uint processId, Role role)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                var hr = _get(_factory, processId, 0, (int)role, out value);
                if (hr == unchecked((int)0x80070490)) return null; // no saved application preference
                Marshal.ThrowExceptionForHR(hr);
                if (value == IntPtr.Zero) return null;
                var pointer = WindowsGetStringRawBuffer(value, out var length);
                return length == 0 ? null : Marshal.PtrToStringUni(pointer, (int)length);
            }
            finally { if (value != IntPtr.Zero) WindowsDeleteString(value); }
        }

        public void Set(uint processId, Role role, string? endpoint)
        {
            var value = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(endpoint))
                    Marshal.ThrowExceptionForHR(WindowsCreateString(endpoint, endpoint.Length, out value));
                Marshal.ThrowExceptionForHR(_set(_factory, processId, 0, (int)role, value));
            }
            finally { if (value != IntPtr.Zero) WindowsDeleteString(value); }
        }

        public void Dispose()
        {
            if (_factory != IntPtr.Zero) { Marshal.Release(_factory); _factory = IntPtr.Zero; }
            if (_uninitialize) RoUninitialize();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetEndpoint(IntPtr self, uint process, int flow, int role, IntPtr endpoint);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetEndpoint(IntPtr self, uint process, int flow, int role, out IntPtr endpoint);
        [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
        [DllImport("combase.dll")] private static extern void RoUninitialize();
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
    }
}
