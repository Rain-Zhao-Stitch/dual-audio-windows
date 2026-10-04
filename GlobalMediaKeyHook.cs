using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace DualAudio;

internal enum MediaVolumeKey { VolumeUp, VolumeDown, Mute }

internal sealed class GlobalMediaKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;
    private const int VkInsert = 0x2D;
    private const int LlkhfAltDown = 0x20;
    private const uint WmHotKey = 0x0312;
    private const uint WmSetMode = 0x8001;
    private const uint WmRemoveHookForCheck = 0x8002;
    private const uint WmQuit = 0x0012;
    private const int HotKeyBase = 0x5100;

    private readonly HookProc _callback;
    private readonly object _lifetimeGate = new();
    private readonly ConcurrentQueue<ModeRequest> _requests = new();
    private readonly Channel<Notification> _notifications = Channel.CreateUnbounded<Notification>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly bool[] _held = new bool[3];
    private readonly bool[] _registered = new bool[3];
    private Thread? _thread;
    private TaskCompletionSource? _started;
    private uint _threadId;
    private IntPtr _hook;
    private volatile bool _interceptKeys;
    private volatile bool _disposed;
    private int _generation;
    private bool _insertHeld;

    public GlobalMediaKeyHook()
    {
        _callback = HookCallback;
        _ = Task.Run(DispatchNotificationsAsync);
    }

    public bool InterceptKeys
    {
        get => _interceptKeys;
        set
        {
            if (_disposed) return;
            if (value) Start();
            lock (_lifetimeGate)
            {
                if (_thread is null) return;
                if (!_thread.IsAlive)
                {
                    if (value) throw new InvalidOperationException("音量键监听线程已停止");
                    _interceptKeys = false;
                    return;
                }
                var request = new ModeRequest(value);
                _requests.Enqueue(request);
                if (!PostThreadMessage(_threadId, WmSetMode, UIntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
        }
    }

    public int Generation => Volatile.Read(ref _generation);

    public event EventHandler<MediaVolumeKey>? KeyPressed;
    public event EventHandler? ToggleRequested;

    public void Start()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "DualAudio media-key listener" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            _started!.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
    }

    private void RunMessageLoop()
    {
        Exception? failure = null;
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create this thread's native message queue.
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装系统音量键钩子。");
            _started!.TrySetResult();
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                if (message.Message == WmSetMode)
                {
                    while (_requests.TryDequeue(out var request))
                    {
                        try { SetModeOnListener(request.Enabled); request.Completion.TrySetResult(); }
                        catch (Exception ex) { request.Completion.TrySetException(ex); }
                    }
                }
                else if (message.Message == WmHotKey && _interceptKeys)
                {
                    var index = (int)message.WParam.ToUInt64() - HotKeyBase;
                    if (index is >= 0 and < 3) QueueKey((MediaVolumeKey)index);
                }
                else if (message.Message == WmRemoveHookForCheck)
                {
                    if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { failure = ex; _started!.TrySetException(ex); }
        finally
        {
            _interceptKeys = false;
            Interlocked.Increment(ref _generation);
            ReleaseHotKeys();
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            while (_requests.TryDequeue(out var request))
                request.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(GlobalMediaKeyHook)));
        }
    }

    private void SetModeOnListener(bool enabled)
    {
        if (_interceptKeys == enabled) return;
        if (enabled)
        {
            // Registration is an independent fallback if Windows ever removes the
            // low-level hook. It lives on the same always-pumping listener thread.
            var keys = new[] { VkVolumeUp, VkVolumeDown, VkVolumeMute };
            try
            {
                for (var i = 0; i < keys.Length; i++)
                {
                    _registered[i] = RegisterHotKey(IntPtr.Zero, HotKeyBase + i,
                        i == (int)MediaVolumeKey.Mute ? 0x4000u : 0, (uint)keys[i]);
                    if (!_registered[i]) throw new Win32Exception(Marshal.GetLastWin32Error(), "音量快捷键被其他程序占用，无法可靠接管。");
                }
            }
            catch { ReleaseHotKeys(); throw; }
        }
        else ReleaseHotKeys();
        Interlocked.Increment(ref _generation);
        _interceptKeys = enabled;
    }

    private void ReleaseHotKeys()
    {
        for (var i = 0; i < _registered.Length; i++)
        {
            if (_registered[i]) UnregisterHotKey(IntPtr.Zero, HotKeyBase + i);
            _registered[i] = false;
        }
    }

    private void QueueKey(MediaVolumeKey key)
        => _notifications.Writer.TryWrite(new Notification(key, Generation));

    private async Task DispatchNotificationsAsync()
    {
        await foreach (var notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) continue;
            try
            {
                if (notification.Key is { } key)
                {
                    if (_interceptKeys && notification.Generation == Generation) KeyPressed?.Invoke(this, key);
                }
                else ToggleRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception) { /* A subscriber must never break or stall the native hook. */ }
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var keyCode = Marshal.ReadInt32(data);
            var kind = message.ToInt32();
            if (keyCode == VkInsert)
            {
                var altDown = (Marshal.ReadInt32(data, 8) & LlkhfAltDown) != 0;
                if (kind is WmKeyDown or WmSysKeyDown && altDown)
                {
                    if (!_insertHeld) _notifications.Writer.TryWrite(new Notification(null, Generation));
                    _insertHeld = true;
                    return new IntPtr(1);
                }
                if (kind is WmKeyUp or WmSysKeyUp && _insertHeld)
                {
                    _insertHeld = false;
                    return new IntPtr(1);
                }
            }

            if (TryMapKey(keyCode, out var key))
            {
                var index = (int)key;
                if (kind is WmKeyUp or WmSysKeyUp)
                {
                    var consumedDown = _held[index];
                    _held[index] = false;
                    if (consumedDown || _interceptKeys) return new IntPtr(1);
                }
                else if (_interceptKeys && kind is WmKeyDown or WmSysKeyDown)
                {
                    // Mute toggles once per press; volume up/down retain auto-repeat.
                    if (key != MediaVolumeKey.Mute || !_held[index]) QueueKey(key);
                    _held[index] = true;
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool TryMapKey(int keyCode, out MediaVolumeKey key)
    {
        key = keyCode switch
        {
            VkVolumeUp => MediaVolumeKey.VolumeUp,
            VkVolumeDown => MediaVolumeKey.VolumeDown,
            VkVolumeMute => MediaVolumeKey.Mute,
            _ => default
        };
        return keyCode is VkVolumeUp or VkVolumeDown or VkVolumeMute;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _interceptKeys = false;
        Interlocked.Increment(ref _generation);
        lock (_lifetimeGate)
        {
            if (_thread?.IsAlive == true)
            {
                PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                if (Thread.CurrentThread != _thread) _thread.Join();
            }
        }
        _notifications.Writer.TryComplete();
    }

    internal void RemoveLowLevelHookForDiagnostics()
    {
        PostThreadMessage(_threadId, WmRemoveHookForCheck, UIntPtr.Zero, IntPtr.Zero);
        // A following mode request is acknowledged only after removal is processed.
        InterceptKeys = true;
    }

    private sealed record Notification(MediaVolumeKey? Key, int Generation);
    private sealed record ModeRequest(bool Enabled)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
