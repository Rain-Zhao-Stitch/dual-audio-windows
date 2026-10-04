using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DualAudio;

internal static class MediaKeyRegressionChecks
{
    public static int Run()
    {
        var log = Path.Combine(Path.GetTempPath(), "DualAudio-media-key-check.log");
        File.WriteAllText(log, "");
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpoint = device.AudioEndpointVolume;
        var originalVolume = endpoint.MasterVolumeLevelScalar;
        var originalMute = endpoint.Mute;
        var keys = new ConcurrentQueue<MediaVolumeKey>();
        using var hook = new GlobalMediaKeyHook();
        void Check(bool condition, string step)
        {
            File.AppendAllText(log, $"{step}={condition}\n");
            if (!condition) throw new InvalidOperationException(step);
        }
        bool Unchanged() => endpoint.MasterVolumeLevelScalar == originalVolume && endpoint.Mute == originalMute;
        void WaitCount(int expected)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (keys.Count < expected && watch.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Check(keys.Count == expected, $"exactly {expected} notifications");
        }
        hook.KeyPressed += (_, key) => keys.Enqueue(key);
        try
        {
            hook.Start();
            Check(!hook.InterceptKeys, "idle passes volume keys through");
            hook.InterceptKeys = true;
            var generation = hook.Generation;
            // No Application.DoEvents on this STA caller: emulate a UI that is busy
            // for longer than Windows' maximum low-level hook timeout.
            Thread.Sleep(1500);
            for (var i = 0; i < 40; i++)
            {
                SendKey(0xAF); SendKey(0xAE);
            }
            WaitCount(80);
            Check(Unchanged(), "busy UI and 80 rapid presses do not reach system volume");
            hook.InterceptKeys = true; // Internal restart retains the same interception session.
            Check(hook.Generation == generation, "internal restart preserves interception generation");
            KeybdEvent(0xAD, 0, 0, UIntPtr.Zero);
            for (var i = 0; i < 20; i++) KeybdEvent(0xAD, 0, 0, UIntPtr.Zero);
            KeybdEvent(0xAD, 0, 2, UIntPtr.Zero);
            WaitCount(81);
            Check(Unchanged(), "held mute emits one toggle and never toggles system mute");
            // Simulate Windows silently removing WH_KEYBOARD_LL. The independently
            // registered hot keys must still own the keys without changing Windows y.
            hook.RemoveLowLevelHookForDiagnostics();
            for (var i = 0; i < 10; i++) { SendKey(0xAF); SendKey(0xAE); }
            WaitCount(101);
            Check(Unchanged(), "registered hot-key fallback works after hook removal");
            hook.InterceptKeys = false;
            var receivedBeforeStop = keys.Count;
            var up = 0xAF;
            SendKey((byte)up);
            Thread.Sleep(300);
            Check(keys.Count == receivedBeforeStop, "stopped mode no longer notifies software");
            // Avoid a volume ceiling (or zero) concealing Windows' restored behavior.
            endpoint.MasterVolumeLevelScalar = .30f;
            endpoint.Mute = false;
            var baseline = endpoint.MasterVolumeLevelScalar;
            SendKey(0xAF);
            Thread.Sleep(300);
            Check(Math.Abs(endpoint.MasterVolumeLevelScalar - baseline) > .001, "stopping restores Windows default volume control");
            File.AppendAllText(log, "PASSED\n");
            return 0;
        }
        catch (Exception ex) { File.AppendAllText(log, ex + "\n"); return 70; }
        finally
        {
            hook.InterceptKeys = false;
            if (originalMute) endpoint.Mute = true;
            endpoint.MasterVolumeLevelScalar = originalVolume;
            endpoint.Mute = originalMute;
        }
    }

    private static void SendKey(byte key)
    {
        KeybdEvent(key, 0, 0, UIntPtr.Zero);
        KeybdEvent(key, 0, 2, UIntPtr.Zero);
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
