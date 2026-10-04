using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio;

internal sealed record LiveSyncResult(double ResidualDelayMilliseconds, double PrimaryCorrelation,
    double SecondaryCorrelation, bool TwoArrivalsDetected, string Report);

internal static class LiveSyncAnalyzer
{
    public static Task<LiveSyncResult> CaptureAndAnalyzeAsync(string outputDirectory, int seconds = 12)
        => Task.Run(() => CaptureAndAnalyze(outputDirectory, seconds));

    private static LiveSyncResult CaptureAndAnalyze(string outputDirectory, int seconds)
    {
        Directory.CreateDirectory(outputDirectory);
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        MMDevice microphone;
        try { microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
        using (microphone)
        using (var loopback = new WasapiLoopbackCapture(render))
        using (var micCapture = new WasapiCapture(microphone))
        using (var loopBytes = new MemoryStream())
        using (var micBytes = new MemoryStream())
        {
            var loopGate = new object();
            var micGate = new object();
            using var loopStopped = new ManualResetEventSlim();
            using var micStopped = new ManualResetEventSlim();
            Exception? loopError = null;
            Exception? micError = null;
            loopback.DataAvailable += (_, e) => { lock (loopGate) loopBytes.Write(e.Buffer, 0, e.BytesRecorded); };
            micCapture.DataAvailable += (_, e) => { lock (micGate) micBytes.Write(e.Buffer, 0, e.BytesRecorded); };
            loopback.RecordingStopped += (_, e) => { loopError = e.Exception; loopStopped.Set(); };
            micCapture.RecordingStopped += (_, e) => { micError = e.Exception; micStopped.Set(); };

            micCapture.StartRecording();
            loopback.StartRecording();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            loopback.StopRecording();
            micCapture.StopRecording();
            if (!loopStopped.Wait(TimeSpan.FromSeconds(3)) || !micStopped.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("等待实听录音结束超时。");
            if (loopError is not null) throw loopError;
            if (micError is not null) throw micError;

            byte[] loopData;
            byte[] micData;
            lock (loopGate) loopData = loopBytes.ToArray();
            lock (micGate) micData = micBytes.ToArray();
            SaveWave(Path.Combine(outputDirectory, "系统原始音轨.wav"), loopback.WaveFormat, loopData);
            SaveWave(Path.Combine(outputDirectory, "麦克风实录.wav"), micCapture.WaveFormat, micData);

            var loopMono = DecodeMono(loopData, loopback.WaveFormat, out var loopRate);
            var micMono = DecodeMono(micData, micCapture.WaveFormat, out var micRate);
            var sourceEnvelope = MakeEnvelope(loopMono, loopRate, 1000);
            var micEnvelope = MakeEnvelope(micMono, micRate, 1000);
            HighPassEnvelope(sourceEnvelope, 180);
            HighPassEnvelope(micEnvelope, 180);
            var correlations = Correlate(sourceEnvelope, micEnvelope, -100, 1200);
            var peaks = correlations
                .Where((item, index) => index > 0 && index < correlations.Count - 1
                    && item.Value >= correlations[index - 1].Value && item.Value >= correlations[index + 1].Value)
                .OrderByDescending(item => item.Value)
                .ToList();
            if (peaks.Count == 0) throw new InvalidOperationException("抖音音频变化太少，无法识别到达峰。请播放有人声或节奏变化的内容重试。");

            var primary = peaks[0];
            var secondary = peaks.FirstOrDefault(item => Math.Abs(item.Lag - primary.Lag) >= 8
                && Math.Abs(item.Lag - primary.Lag) <= 700 && item.Value >= Math.Max(0.06, primary.Value * 0.48));
            var twoArrivals = secondary != default;
            var residual = twoArrivals ? Math.Abs(primary.Lag - secondary.Lag) : 0;
            var verdict = !twoArrivals || residual <= 12
                ? "未发现明显双重到达，当前听感应当已经很接近同步。"
                : residual <= 25
                    ? "仍有轻微错位，近距离可能听到梳状感。"
                    : "仍存在明显前后声/回声，当前同步不准确。";
            var report = $"默认输出: {render.FriendlyName}{Environment.NewLine}" +
                         $"麦克风: {microphone.FriendlyName}{Environment.NewLine}" +
                         $"主到达峰: {primary.Lag} ms (相关度 {primary.Value:F3}){Environment.NewLine}" +
                         (twoArrivals
                             ? $"次到达峰: {secondary.Lag} ms (相关度 {secondary.Value:F3}){Environment.NewLine}残余错位: {residual:F0} ms{Environment.NewLine}"
                             : "没有检测到可信的第二到达峰。" + Environment.NewLine) + verdict;
            File.WriteAllText(Path.Combine(outputDirectory, "同步实听报告.txt"), report);
            return new LiveSyncResult(residual, primary.Value, secondary.Value, twoArrivals, report);
        }
    }

    private static void SaveWave(string path, WaveFormat format, byte[] data)
    {
        using var writer = new WaveFileWriter(path, format);
        writer.Write(data, 0, data.Length);
    }

    private static float[] DecodeMono(byte[] data, WaveFormat format, out int sampleRate)
    {
        var normalized = Normalize(format);
        sampleRate = normalized.SampleRate;
        using var stream = new RawSourceWaveStream(new MemoryStream(data), normalized);
        var provider = stream.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var buffer = new float[Math.Max(4096, sampleRate * channels)];
        var result = new List<float>();
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var frame = 0; frame < read / channels; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++) sum += buffer[frame * channels + channel];
                result.Add(sum / channels);
            }
        }
        return result.ToArray();
    }

    private static WaveFormat Normalize(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible) return format;
        var extensible = (WaveFormatExtensible)format;
        var ieee = new Guid("00000003-0000-0010-8000-00aa00389b71");
        var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (extensible.SubFormat == ieee) return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
        if (extensible.SubFormat == pcm) return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        throw new NotSupportedException($"不支持的录音格式：{extensible.SubFormat}");
    }

    private static double[] MakeEnvelope(float[] samples, int sampleRate, int envelopeRate)
    {
        var block = Math.Max(1, sampleRate / envelopeRate);
        var result = new double[samples.Length / block];
        for (var i = 0; i < result.Length; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < block; j++) sum += samples[i * block + j] * samples[i * block + j];
            result[i] = Math.Log10(1e-8 + Math.Sqrt(sum / block));
        }
        return result;
    }

    private static void HighPassEnvelope(double[] values, int window)
    {
        var prefix = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++) prefix[i + 1] = prefix[i] + values[i];
        var original = (double[])values.Clone();
        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - window / 2);
            var end = Math.Min(values.Length, i + window / 2 + 1);
            values[i] = original[i] - (prefix[end] - prefix[start]) / (end - start);
        }
    }

    private static List<(int Lag, double Value)> Correlate(double[] source, double[] microphone, int minLag, int maxLag)
    {
        var results = new List<(int, double)>();
        const int trim = 800;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var start = Math.Max(trim, -lag + trim);
            var end = Math.Min(source.Length - trim, microphone.Length - lag - trim);
            if (end - start < 3000) continue;
            var xy = 0.0; var xx = 0.0; var yy = 0.0;
            for (var i = start; i < end; i++)
            {
                var x = source[i]; var y = microphone[i + lag];
                xy += x * y; xx += x * x; yy += y * y;
            }
            results.Add((lag, xy / Math.Sqrt(Math.Max(1e-12, xx * yy))));
        }
        return results;
    }
}
