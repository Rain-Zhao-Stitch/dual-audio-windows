using System.Text.Json;

namespace DualAudio;

internal sealed class AppSettings
{
    public string? SourceDeviceId { get; set; }
    public string? TargetDeviceId { get; set; }
    public int SourceVolume { get; set; } = 100;
    public int TargetVolume { get; set; } = 100;
    public int MasterVolume { get; set; } = 100;
    public bool HasProductVolumeSettings { get; set; }
    public double SourceCoefficient { get; set; } = 100;
    public double TargetCoefficient { get; set; } = 100;
    public double SourceOutput { get; set; }
    public double TargetOutput { get; set; }
    public int TargetDelayMilliseconds { get; set; }
    public string? CalibrationSourceDeviceId { get; set; }
    public string? CalibrationTargetDeviceId { get; set; }
    public bool PreferNegativeDirectionAtZero { get; set; }
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save() => Save(SettingsPath);

    internal void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
        }
        catch { }
    }
}
