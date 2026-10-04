using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace DualAudio;

// A separate sound-source process lets the actual mirror capture only the
// calibration sound, without capturing its own output or unrelated applications.
internal sealed class CalibrationSoundSource : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    public uint ProcessId => (uint)_process.Id;

    public CalibrationSoundSource(string deviceId, CancellationToken token)
    {
        var name = "DualAudio-calibration-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序路径。"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--calibration-sound-source");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(deviceId);
        try { _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动校准声音源。"); }
        catch { _pipe.Dispose(); throw; }
        try
        {
            _pipe.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token).GetAwaiter().GetResult();
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var ready = ReadLine(token);
            if (ready != "ready") throw new InvalidOperationException("校准声音源启动失败：" + ready);
        }
        catch { Dispose(); throw; }
    }

    private string ReadLine(CancellationToken token)
        => _reader!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token).GetAwaiter().GetResult()
           ?? throw new IOException("校准声音源已断开。");

    public double Emit(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _writer!.WriteLine("emit");
        var response = ReadLine(token);
        if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
            throw new InvalidOperationException("声音源无法发送校准声：" + response);
        return timestamp;
    }

    public void Dispose()
    {
        try { _writer?.WriteLine("exit"); } catch { }
        try { _reader?.Dispose(); } catch (IOException) { }
        try { _writer?.Dispose(); } catch (IOException) { }
        _pipe.Dispose();
        try
        {
            if (!_process.WaitForExit(1500)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally { _process.Dispose(); }
    }

    public static int RunChild(string pipeName, string deviceId)
    {
        try { return RunConnectedChild(pipeName, deviceId); }
        catch { return 1; } // A canceled parent must not leave an unhandled-error dialog.
    }

    private static int RunConnectedChild(string pipeName, string deviceId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(10_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            using var output = new LatencyCalibrationEngine.ContinuousCalibrationOutput(deviceId);
            writer.WriteLine("ready");
            string? command;
            while ((command = reader.ReadLine()) is not null && command != "exit")
            {
                if (command == "emit") writer.WriteLine(output.Emit().ToString("R", CultureInfo.InvariantCulture));
            }
            return 0;
        }
        catch (Exception ex)
        {
            try { writer.WriteLine("error: " + ex.Message.Replace('\n', ' ').Replace('\r', ' ')); } catch { }
            return 1;
        }
    }
}
