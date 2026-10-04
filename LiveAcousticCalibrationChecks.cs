using System.Text.Json;
using NAudio.Wave;

namespace DualAudio;

// Offline only: no microphone, endpoint writes, playback, or running-app control.
internal static class LiveAcousticCalibrationChecks
{
    public static int Run(string[] recordingDirectories)
    {
        try
        {
            var random = new Random(42);
            var reference = Enumerable.Range(0, 64000).Select(_ => random.NextDouble() * 2 - 1).ToArray();
            double[] Shift(int frames) => Enumerable.Range(0, reference.Length)
                .Select(i => (i >= frames ? reference[i - frames] * .7 : 0) + (random.NextDouble() - .5) * .02).ToArray();
            var first = LiveAcousticCalibrationEngine.SelectPeak(LiveAcousticCalibrationEngine.Correlate(reference, Shift(5120), 19200));
            var second = LiveAcousticCalibrationEngine.SelectPeak(LiveAcousticCalibrationEngine.Correlate(reference, Shift(3600), 19200));
            Require(Math.Abs(first.Milliseconds - 320) < .1 && Math.Abs(second.Milliseconds - 225) < .1, "FFT lag orientation / scaling");
            Require(LiveAcousticCalibrationEngine.CalculatePositiveDelay(0, [95.125, 95.5]) == 95, "observed positive compensation");
            Require(LiveAcousticCalibrationEngine.CalculatePositiveDelay(100, [-20.0, -20.5]) == 80, "reduce an existing positive delay");
            Require(LiveAcousticCalibrationEngine.CalculatePositiveDelay(0, [-1.0, 1.0]) == 0, "small zero residual");
            Reject(() => LiveAcousticCalibrationEngine.CalculatePositiveDelay(0, [-30.0, -31.0]), "negative delay forbidden");
            Reject(() => LiveAcousticCalibrationEngine.CalculatePositiveDelay(0, [40.0, 70.0]), "unstable rounds rejected");
            Reject(() => LiveAcousticCalibrationEngine.CalculatePositiveDelay(900, [150.0, 150.0]), "range overflow rejected");
            Reject(() => LiveAcousticCalibrationEngine.Correlate(reference, new double[reference.Length], 19200), "silence rejected");
            Reject(() => LiveAcousticCalibrationEngine.SelectPeak(new double[19201]), "no peak is not success");
            var invalid = (double[])reference.Clone(); invalid[0] = double.NaN;
            Reject(() => LiveAcousticCalibrationEngine.Correlate(invalid, reference, 19200), "NaN rejected");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { LiveAcousticCalibrationEngine.Correlate(reference, reference, 19200, cancellation.Token); throw new Exception("cancellation not observed"); }
                catch (OperationCanceledException) { }
            }
            var endpoint1 = new FakeEndpoint { Mute = true, Volume = .23f };
            var endpoint2 = new FakeEndpoint { Mute = false, Volume = .17f };
            using (var mutes = new AcousticCalibrationMuteScope(endpoint1, endpoint2))
            {
                mutes.Select(1); Require(!endpoint1.Mute && endpoint2.Mute, "first-only phase");
                mutes.Select(2); Require(endpoint1.Mute && !endpoint2.Mute, "second-only phase");
                mutes.Select(0); Require(!endpoint1.Mute && !endpoint2.Mute, "both phase");
            }
            Require(endpoint1.Mute && !endpoint2.Mute && endpoint1.VolumeWrites == 0 && endpoint2.VolumeWrites == 0, "mute snapshots restored, no volume writes");
            var delay = 17;
            try
            {
                using var lease = new AcousticCalibrationDelayScope(delay, value => delay = value);
                lease.Apply(96);
                throw new OperationCanceledException();
            }
            catch (OperationCanceledException) { }
            Require(delay == 17, "verification cancellation restores old delay");
            var failingFirst = new FakeEndpoint { Mute = true };
            var otherSecond = new FakeEndpoint { Mute = false };
            var restore = new AcousticCalibrationMuteScope(failingFirst, otherSecond);
            restore.Select(1); failingFirst.FailMuteWrite = true;
            try { restore.Dispose(); throw new Exception("restore failure not reported"); }
            catch (AggregateException) { }
            Require(!otherSecond.Mute, "second endpoint restored even when first fails");
            Console.WriteLine("Synthetic lag, positive-only bounds, silence, cancellation and snapshot restoration: PASS");

            foreach (var directory in recordingDirectories)
            {
                var source = Load(directory, "device1"); var microphone = Load(directory, "microphone");
                using var phases = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "acoustic-phases.json")));
                var windows = phases.RootElement.EnumerateArray().ToArray();
                var peaks = new List<LiveAcousticCalibrationEngine.Peak>();
                foreach (var (phase, index) in windows.Select((value, index) => (value, index)))
                {
                    var start = phase.GetProperty("QpcSeconds").GetDouble() + 1.3;
                    var end = index + 1 < windows.Length ? windows[index + 1].GetProperty("QpcSeconds").GetDouble() - .5
                        : Math.Min(source.End, microphone.End) - .8;
                    var correlations = LiveAcousticCalibrationEngine.AnalyseRecordedPhase(source.Packets, source.Format,
                        microphone.Packets, microphone.Format, start, end);
                    if (index < 2) peaks.Add(LiveAcousticCalibrationEngine.SelectPeak(correlations));
                    else LiveAcousticCalibrationEngine.ValidateBoth(correlations, peaks[0].Milliseconds, peaks[1].Milliseconds);
                }
                var difference = peaks[0].Milliseconds - peaks[1].Milliseconds;
                Require(Math.Abs(difference - 95) < 1, "saved live recording reproduces physical offset");
                Console.WriteLine($"{Path.GetFileName(directory)}: first={peaks[0].Milliseconds:F3} ms, second={peaks[1].Milliseconds:F3} ms, difference={difference:F3} ms, both-output verification PASS");
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 91; }
    }

    private static (List<(byte[] Bytes, double Time, bool Discontinuity)> Packets, WaveFormat Format, double End) Load(string directory, string name)
    {
        using var wave = new WaveFileReader(Path.Combine(directory, name + ".wav"));
        var bytes = new byte[(int)wave.Length];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = wave.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".json")));
        var packets = new List<(byte[] Bytes, double Time, bool Discontinuity)>();
        double end = 0;
        foreach (var packet in metadata.RootElement.GetProperty("Packets").EnumerateArray())
        {
            var flags = packet.GetProperty("Flags").GetInt32();
            if ((flags & 4) != 0) throw new Exception("Invalid recording timestamp");
            var offset = packet.GetProperty("StartFrame").GetInt32() * wave.WaveFormat.BlockAlign;
            var frames = packet.GetProperty("Frames").GetInt32();
            var time = packet.GetProperty("QpcSeconds").GetDouble();
            packets.Add((bytes.AsSpan(offset, frames * wave.WaveFormat.BlockAlign).ToArray(), time, (flags & 1) != 0));
            end = time + frames / (double)wave.WaveFormat.SampleRate;
        }
        return (packets, wave.WaveFormat, end);
    }

    private static void Require(bool value, string name) { if (!value) throw new Exception(name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception(name);
    }
    private sealed class FakeEndpoint : IVolumeEndpoint
    {
        private float _volume;
        private bool _mute;
        public int VolumeWrites { get; private set; } = -1; // Initial object initializer is not a diagnostic write.
        public bool FailMuteWrite { get; set; }
        public float Volume { get => _volume; set { _volume = value; VolumeWrites++; } }
        public bool Mute { get => _mute; set { if (FailMuteWrite) throw new IOException("injected restore failure"); _mute = value; } }
        public void Dispose() { }
    }
}
