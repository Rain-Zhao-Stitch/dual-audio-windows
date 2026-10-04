using NAudio.Wave;

namespace DualAudio;

internal static class RegressionChecks
{
    private static readonly List<string> Results = [];
    public static int Run(bool audio = true)
    {
        Results.Clear();
        try
        {
            CheckVolume();
            CheckSession();
            CheckSettings();
            if (audio)
            {
                CheckClock(1000, 1000, 0.003, 3600, false);
                CheckClock(1000, 1000, -0.003, 3600, false);
                CheckClock(1000, 1100, 0.005, 3600, true);
                CheckClock(48000, 44100, -0.001, 60, false);
                CheckStallRecovery();
            }
            Results.Add("ALL PASSED");
            return 0;
        }
        catch (Exception ex) { Results.Add(ex.ToString()); return 60; }
        finally { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "DualAudio-regression.log"), Results); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static bool Near(double a, double b, double tolerance = 0.001) => Math.Abs(a - b) <= tolerance;

    private static void CheckVolume()
    {
        var model = new VolumePolicy();
        model.Initialize(50);
        model.SetSource(80);
        model.SetTarget(60);
        Require(Near(model.SourceOutput, 40) && Near(model.TargetOutput, 30), "y=z*x");
        model.SetSource(20);
        Require(model.Master == 50 && model.Target == 60 && Near(model.TargetOutput, 30), "changing x1 affected x2/z");
        model.SetTarget(40);
        Require(model.Master == 50 && model.Source == 20, "changing x2 affected x1/z");
        model.SetMaster(25);
        Require(model.Source == 20 && model.Target == 40 && model.SourceOutput == 5 && model.TargetOutput == 10, "changing z affected x");
        model.ReadOutputs(30, 10, true, false);
        Require(model.Source == 120 && model.Target == 40 && model.Master == 25, "external y1 affected x2/z");
        model.ReadOutputs(30, 20, false, true);
        Require(model.Target == 80 && model.Source == 120 && model.Master == 25, "external y2 affected x1/z");
        model.SetMaster(100);
        Require(model.Source == 120 && model.Target == 80 && model.Master == 83 && model.SourceOutput <= 100, "hardware ceiling changed x");
        model.SetMaster(0);
        model.ReadOutputs(30, 40);
        Require(model.Master == 0 && model.Source == 120 && model.Target == 80, "z=0 lost remembered coefficients");
        Results.Add("Volume: independent x1/x2/z, inverse y updates, 100% ceiling, zero-master preservation passed");
    }

    private static void CheckSession()
    {
        var model = new VolumePolicy();
        model.Initialize(40); model.SetSource(70); model.SetTarget(30);
        var source = new FakeEndpoint { Volume = 0.77f, Mute = true };
        var target = new FakeEndpoint { Volume = 0.52f, Mute = false };
        using (var session = new VolumeSyncSession(source, target, model))
        {
            Require(!source.Mute && !target.Mute && Near(source.Volume, .28) && Near(target.Volume, .12), "start snapshot/unmute/product");
            for (var i = 0; i < 10000; i++) Require(!session.ReadExternalChanges(model), "own write moved slider");
            Require(model.Source == 70 && model.Target == 30, "poll moved coefficients");
            source.Volume = 0.32f;
            Require(session.ReadExternalChanges(model) && Near(model.Source, 80) && model.Target == 30 && model.Master == 40, "external source binding");
            target.Volume = 0.20f;
            Require(session.ReadExternalChanges(model) && Near(model.Target, 50) && Near(model.Source, 80) && model.Master == 40, "external target binding");
            model.SetMaster(0); session.Apply(model, true, true);
            source.Volume = .9f; session.ReadExternalChanges(model);
            Require(source.Volume == 0 && model.Master == 0 && Near(model.Source, 80), "zero-master boundary");
            target.Mute = true;
        }
        Require(source.Volume == .77f && source.Mute && target.Volume == .52f && !target.Mute, "stop failed to restore volume/mute");
        var failedSource = new FakeEndpoint { Volume = .7f, Mute = true };
        var failedTarget = new FakeEndpoint { Volume = .6f, Mute = true, FailNextWrite = true };
        try { _ = new VolumeSyncSession(failedSource, failedTarget, model); throw new InvalidOperationException("missing failure"); }
        catch (IOException) { }
        Require(failedSource.Volume == .7f && failedSource.Mute && failedTarget.Volume == .6f && failedTarget.Mute, "partial startup rollback");
        Results.Add("Session: snapshot, unmute, polling, isolated external changes, mute restoration, partial-start rollback passed");
    }

    private static void CheckSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "DualAudio-regression-settings.json");
        var settings = new AppSettings { HasProductVolumeSettings = true, SourceCoefficient = 27.125, TargetCoefficient = 73.875, MasterVolume = 30 };
        settings.Save(path);
        var loaded = AppSettings.Load(path);
        Require(loaded.HasProductVolumeSettings && loaded.SourceCoefficient == settings.SourceCoefficient
            && loaded.TargetCoefficient == settings.TargetCoefficient && loaded.MasterVolume == settings.MasterVolume, "persisted coefficients lost precision");
        Results.Add("Settings: fractional x1/x2 and z survive save/reload passed");
    }

    private static void CheckClock(int inputRate, int outputRate, double drift, int seconds, bool jitter)
    {
        var fifo = new ClockCorrectedBuffer(inputRate, outputRate, 2);
        var input = new float[inputRate / 5 * 2];
        var output = new float[outputRate / 100 * 2];
        long generated = 0;
        void Feed(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                var sample = .7f * (float)Math.Sin((generated + i) * 2 * Math.PI * 10 / inputRate);
                input[i * 2] = sample; input[i * 2 + 1] = -sample;
            }
            generated += frames;
            fifo.AddSamples(input, 0, frames * 2);
        }
        Feed(inputRate * 120 / 1000);
        double production = 0;
        var pending = 0;
        var maxFrames = 0;
        double energy = 0;
        float previous = 0;
        for (var tick = 0; tick < seconds * 100; tick++)
        {
            production += inputRate * .01 * (1 + drift);
            var frames = (int)production; production -= frames;
            pending += frames;
            if (!jitter || tick % 3 == 0) { Feed(pending); pending = 0; }
            Require(fifo.Read(output, 0, output.Length) == output.Length, "short clock read");
            maxFrames = Math.Max(maxFrames, fifo.BufferedFrames);
            for (var i = 0; i < output.Length; i += 2)
            {
                Require(float.IsFinite(output[i]) && Math.Abs(output[i]) < .8f && Near(output[i], -output[i + 1], 1e-5), "distortion or stereo misalignment");
                if (tick > 100) Require(Math.Abs(output[i] - previous) < .2, "clock correction made a click");
                previous = output[i]; energy += output[i] * output[i];
            }
        }
        Require(fifo.OverflowRecoveries == 0 && fifo.UnderflowRecoveries == 0, $"clock drift dropped/starved frames: {fifo.OverflowRecoveries}/{fifo.UnderflowRecoveries}");
        Require(maxFrames < inputRate / 4 && Math.Abs(fifo.Correction - drift) < .001, $"unbounded latency or incorrect correction {fifo.Correction}");
        Require(energy / (seconds * outputRate) > .1, "long-running output went silent");
        Results.Add($"Clock: {seconds}s simulated, {inputRate}->{outputRate}Hz, drift={drift:P2}, jitter={jitter}, correction={fifo.Correction:F6}, maxFIFO={maxFrames}, overflows=0 underflows=0 passed");
    }

    private static void CheckStallRecovery()
    {
        var fifo = new ClockCorrectedBuffer(1000, 1000, 2);
        var input = Enumerable.Repeat(.25f, 500 * 2).ToArray();
        var output = new float[20];
        fifo.AddSamples(input, 0, 240);
        for (var i = 0; i < 30; i++) fifo.Read(output, 0, 20);
        Require(fifo.UnderflowRecoveries > 0 && output.All(x => x == 0), "source silence/stall recovery");
        fifo.AddSamples(input, 0, input.Length);
        fifo.AddSamples(input, 0, 240);
        for (var i = 0; i < 20; i++)
        {
            fifo.AddSamples(input, 0, 20);
            fifo.Read(output, 0, 20);
        }
        Require(fifo.OverflowRecoveries > 0 && output.All(float.IsFinite) && output.Any(x => Math.Abs(x) > .1), "overflow failed to recover");
        Results.Add("Audio: underflow silence, bounded overflow and fade-in recovery passed");
    }

    public static int CheckCapture()
    {
        var path = Path.Combine(Path.GetTempPath(), "DualAudio-capture-check.log");
        File.WriteAllText(path, "");
        try
        {
            Task.Run(() =>
            {
                for (var trial = 0; trial < 3; trial++)
                {
                    using var capture = new SystemAudioCapture();
                    long bytes = 0;
                    Exception? error = null;
                    capture.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
                    capture.RecordingStopped += (_, e) => error = e.Exception;
                    capture.StartRecording();
                    Thread.Sleep(400);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    capture.StopRecording();
                    Require(watch.ElapsedMilliseconds < 2000 && error is null, "capture stopped with error or deadlock: " + error);
                    File.AppendAllText(path, $"trial={trial}, bytes={bytes}, stop={watch.ElapsedMilliseconds}ms\n");
                }
            }).GetAwaiter().GetResult();
            File.AppendAllText(path, "PASSED\n");
            return 0;
        }
        catch (Exception ex) { File.AppendAllText(path, ex + "\n"); return 61; }
    }

    private sealed class FakeEndpoint : IVolumeEndpoint
    {
        private float _volume;
        public bool FailNextWrite { get; set; }
        public float Volume
        {
            get => _volume;
            set
            {
                if (FailNextWrite) { FailNextWrite = false; throw new IOException("simulated disconnected device"); }
                _volume = value;
            }
        }
        public bool Mute { get; set; }
        public void Dispose() { }
    }
}
