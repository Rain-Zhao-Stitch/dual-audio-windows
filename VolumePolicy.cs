namespace DualAudio;

// Percent values. Keep full precision internally when reversing endpoint changes.
internal sealed class VolumePolicy
{
    public double Source { get; private set; } = 100;
    public double Target { get; private set; } = 100;
    public int Master { get; private set; } = 100;
    public double SourceOutput => Source * Master / 100;
    public double TargetOutput => Target * Master / 100;
    public int MasterLimit => (int)Math.Min(100, Math.Floor(10000 / Math.Max(1, Math.Max(Source, Target)) + 1e-8));
    public void Initialize(int master) => Master = Math.Clamp(master, 0, 100);
    public void SetSource(double value) => Source = double.IsFinite(value) ? Math.Clamp(value, 0, Master > 0 ? 10000d / Master : 10000) : Source;
    public void SetTarget(double value) => Target = double.IsFinite(value) ? Math.Clamp(value, 0, Master > 0 ? 10000d / Master : 10000) : Target;
    public void SetMaster(int value) => Master = Math.Clamp(value, 0, MasterLimit);
    public void ReadOutputs(double source, double target, bool sourceChanged = true, bool targetChanged = true)
    {
        source = Math.Clamp(source, 0, 100);
        target = Math.Clamp(target, 0, 100);
        // Division by zero has no inverse: never silently change z or lose x.
        if (Master == 0) return;
        if (sourceChanged) Source = source * 100 / Master;
        if (targetChanged) Target = target * 100 / Master;
    }
}
