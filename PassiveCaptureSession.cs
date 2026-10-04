using NAudio.CoreAudioApi;

namespace DualAudio;

// Explicitly identifies ONLY capture sessions created by our passive diagnostics.
// Do not exempt a whole process name (for example dotnet), or foreign players.
internal static class PassiveCaptureSession
{
    internal static readonly Guid Group = new("39c16028-4572-44ec-a56e-c7809e6dc052");
    private const string Name = "Dual Audio passive timing capture";

    internal static void MarkOwnCaptureSessions()
    {
        // The virtual process-loopback client does not expose session control
        // through GetService. Its projected sessions can still be marked through
        // the endpoint manager. Call this ONLY in a capture-only diagnostic
        // process, never in the normal GUI or calibration sound-source process.
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            var manager = device.AudioSessionManager;
            try
            {
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var control = sessions[i];
                    if (control.GetProcessID != (uint)Environment.ProcessId) continue;
                    control.SetGroupingParam(Group, Guid.Empty);
                    control.DisplayName = Name;
                }
            }
            finally { manager.Dispose(); }
        }
    }

    internal static bool IsDiagnosticCapture(AudioSessionControl session)
        => session.GetGroupingParam() == Group && session.DisplayName == Name;
}
