using NAudio.CoreAudioApi;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DualAudio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--calibration-sound-source")
            return Task.Run(() => CalibrationSoundSource.RunChild(args[1], args[2])).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        if (args.Length >= 1 && args[0] == "--live-calibration-analysis-test")
            return LiveAcousticCalibrationChecks.Run(args.Skip(1).ToArray());
        if (args.Length == 1 && args[0] == "--media-key-regression-test")
            return MediaKeyRegressionChecks.Run();
        if (args.Length == 1 && args[0] == "--shortcut-flow-test")
            return RunShortcutFlowCheck();
        if (args.Length == 1 && args[0] == "--regression-test")
            return RegressionChecks.Run();
        if (args.Length == 1 && args[0] == "--capture-self-test")
            return RegressionChecks.CheckCapture();
        if (args.Length == 1 && args[0] == "--volume-lifecycle-test")
            return RunVolumeLifecycleCheck();
        if (args.Length == 1 && args[0] == "--product-volume-test")
        {
            return RegressionChecks.Run(audio: false);
        }
        if (args.Length == 1 && args[0] == "--volume-self-test")
        {
            using var form = new MainForm();
            var guarded = form.TestStoppedVolumeOverlay();
            form.ExitForDiagnostics();
            using var overlay = new VolumeOverlayForm();
            overlay.ShowVolume(150, 50);
            var bounded = overlay.CurrentValue == 100 && overlay.Visible;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 400)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            var hidden = !overlay.Visible;
            overlay.ShowVolume(-10);
            var muted = overlay.CurrentValue == 0 && overlay.Visible;
            overlay.Hide();
            return guarded && bounded && hidden && muted && !overlay.Visible ? 0 : 30;
        }
        if (args.Length == 2 && args[0] == "--render-volume")
        {
            VolumeOverlayForm.RenderPreview(args[1]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--self-test")
            return RunSelfTest();
        if (args.Length == 2 && args[0] == "--render-ui")
            return RenderUi(args[1]);
        if (args.Length == 1 && args[0] == "--tray-self-test")
            return RunTraySelfTest();
        if (args.Length == 1 && args[0] == "--endpoint-binding-self-test")
            return RunEndpointBindingSelfTest();
        if (args.Length == 1 && args[0] == "--delay-direction-self-test")
            return RunDelayDirectionSelfTest();
        if (args.Length == 1 && args[0] == "--calibration-self-test")
            return RunCalibrationSelfTest();
        if (args.Length == 1 && args[0] == "--default-switch-self-test")
            return RunDefaultSwitchSelfTest();
        if (args.Length == 2 && args[0] == "--listen-sync")
            return RunLiveSyncAnalysis(args[1]);
        if (args.Length == 3 && args[0] == "--build-icon")
            return BuildWindowsIcon(args[1], args[2]);

        Application.Run(new MainForm());
        return 0;
    }

    private static int RunSelfTest()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var target = devices.FirstOrDefault(device => device.ID != defaultDevice.ID);
            if (target is null) return 3;

            using (target)
            using (var engine = new AudioMirrorEngine())
            {
                var originalVolume = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                engine.Start(defaultDevice.ID, target.ID, originalVolume, 0f, 125);
                Thread.Sleep(400);
                engine.SetTargetDelay(750);
                Thread.Sleep(400);
                engine.SetTargetDelay(0);
                Thread.Sleep(400);
                engine.Stop();
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "DualAudio-engine-check.log"), ex.ToString());
            return 2;
        }
    }

    private static int RunVolumeLifecycleCheck()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "DualAudio-lifecycle-check.log"), "");
        var result = 63;
        using var form = new MainForm(diagnosticsMode: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
        form.SuppressTrayHintForDiagnostics();
        form.Shown += async (_, _) =>
        {
            try { result = await form.TestVolumeLifecycleAsync() ? 0 : 62; }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "DualAudio-lifecycle-check.log"), ex + "\n");
            }
            finally { form.ExitForDiagnostics(); }
        };
        Application.Run(form);
        return result;
    }

    private static int RunShortcutFlowCheck()
    {
        var log = Path.Combine(Path.GetTempPath(), "DualAudio-shortcut-flow.log");
        File.WriteAllText(log, "");
        using var form = new MainForm(diagnosticsMode: true, enableMediaKeysInDiagnostics: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000)
        };
        var result = 72;
        form.SuppressTrayHintForDiagnostics();
        form.Shown += async (_, _) =>
        {
            try { result = await form.TestShortcutVolumeFlowAsync() ? 0 : 71; }
            catch (Exception ex) { File.AppendAllText(log, ex + "\n"); }
            finally { form.ExitForDiagnostics(); }
        };
        Application.Run(form);
        return result;
    }

    private static int RenderUi(string outputPath)
    {
        try
        {
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000)
            };
            form.SuppressTrayHintForDiagnostics();
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            form.ExitForDiagnostics();
            return 0;
        }
        catch
        {
            return 4;
        }
    }

    private static int RunTraySelfTest()
    {
        try
        {
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000)
            };
            form.SuppressTrayHintForDiagnostics();
            form.Show();
            Application.DoEvents();
            form.Close();
            Application.DoEvents();
            var checks = new Dictionary<string, bool>
            {
                ["hidden-in-tray"] = form.IsHiddenInTrayForDiagnostics,
                ["tray-volume"] = form.TestTrayVolumeSynchronization(),
                ["signed-delay"] = form.TestSignedDelayInput(),
                ["media-adjustment"] = form.TestMediaKeyAdjustment(),
                ["global-media-key"] = form.TestGlobalMediaKeyInterception(),
                ["global-toggle-hotkey"] = form.TestGlobalToggleHotKey()
            };
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "DualAudio-tray-test.txt"),
                checks.Select(check => $"{check.Key}={check.Value}"));
            var passed = checks.Values.All(value => value);
            form.ExitForDiagnostics();
            return passed ? 0 : 5;
        }
        catch
        {
            return 6;
        }
    }

    private static int RunEndpointBindingSelfTest()
    {
        try
        {
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000)
            };
            form.SuppressTrayHintForDiagnostics();
            form.Show();
            Application.DoEvents();
            var passed = form.TestSystemEndpointBinding();
            form.ExitForDiagnostics();
            return passed ? 0 : 22;
        }
        catch { return 23; }
    }

    private static int RunDelayDirectionSelfTest()
    {
        try
        {
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000)
            };
            form.SuppressTrayHintForDiagnostics();
            form.Show();
            Application.DoEvents();
            var passed = form.TestDelayDirectionSwitch();
            form.ExitForDiagnostics();
            return passed ? 0 : 24;
        }
        catch { return 25; }
    }

    private static int RunCalibrationSelfTest()
    {
        try
        {
            var settings = AppSettings.Load();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(d => !d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var first = devices.FirstOrDefault(d => d.ID == settings.SourceDeviceId) ?? devices.FirstOrDefault();
            var second = devices.FirstOrDefault(d => d.ID == settings.TargetDeviceId && d.ID != first?.ID)
                         ?? devices.FirstOrDefault(d => d.ID != first?.ID);
            if (first is null || second is null) return 10;
            var result = LatencyCalibrationEngine.MeasureAsync(first.ID, second.ID).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "DualAudio-calibration-result.txt"),
                $"device1={result.Device1LatencyMilliseconds:F1} ms{Environment.NewLine}" +
                $"device2={result.Device2LatencyMilliseconds:F1} ms{Environment.NewLine}" +
                $"signedDelay={result.SignedDelayMilliseconds} ms{Environment.NewLine}" +
                $"spread={result.MeasurementSpreadMilliseconds:F1} ms{Environment.NewLine}" +
                $"trials={result.TrialCount}{Environment.NewLine}" +
                $"microphone={result.MicrophoneName}");
            var plausible = result.Device1LatencyMilliseconds is >= -200 and <= 1000
                            && result.Device2LatencyMilliseconds is >= -200 and <= 1000
                            && result.SignedDelayMilliseconds is >= -1000 and <= 1000;
            return plausible ? 0 : 11;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "DualAudio-calibration-error.txt"), ex.ToString()); }
            catch { }
            return 12;
        }
    }

    private static int RunDefaultSwitchSelfTest()
    {
        string? originalId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var original = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            originalId = original.ID;
            var other = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(device => device.ID != originalId);
            if (other is null) return 13;
            DefaultAudioDeviceManager.SetDefaultRenderDevice(other.ID);
            Thread.Sleep(350);
            using var changed = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return changed.ID == other.ID ? 0 : 14;
        }
        catch
        {
            return 15;
        }
        finally
        {
            if (originalId is not null)
            {
                try { DefaultAudioDeviceManager.SetDefaultRenderDevice(originalId); }
                catch { }
            }
        }
    }

    private static int RunLiveSyncAnalysis(string outputDirectory)
    {
        try
        {
            LiveSyncAnalyzer.CaptureAndAnalyzeAsync(outputDirectory).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "同步实听错误.txt"), ex.ToString());
            }
            catch { }
            return 19;
        }
    }

    private static int BuildWindowsIcon(string sourcePath, string outputPath)
    {
        try
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            using var source = new Bitmap(sourcePath);
            var images = new List<byte[]>(sizes.Length);
            foreach (var size in sizes)
            {
                using var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, size, size));
                }
                using var stream = new MemoryStream();
                resized.Save(stream, ImageFormat.Png);
                images.Add(stream.ToArray());
            }

            using var output = File.Create(outputPath);
            using var writer = new BinaryWriter(output);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            var dataOffset = 6 + sizes.Length * 16;
            for (var i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[i].Length);
                writer.Write(dataOffset);
                dataOffset += images[i].Length;
            }
            foreach (var image in images)
                writer.Write(image);
            return 0;
        }
        catch
        {
            return 7;
        }
    }
}
