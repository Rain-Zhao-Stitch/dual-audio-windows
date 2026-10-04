using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio;

internal sealed record LiveAcousticCalibrationResult(double FirstMilliseconds, double SecondMilliseconds,
    int DelayMilliseconds, double ResidualMilliseconds, double SpreadMilliseconds, string MicrophoneName);

// Measures the currently running direct-device-1 / mirrored-device-2 path.
// The reference and microphone share WASAPI QPC time, not callback arrival time.
// Absolute reference offsets cancel within each uninterrupted recording.
internal static class LiveAcousticCalibrationEngine
{
    private sealed record Phase(string Name, double Start, double End);
    private sealed record AudioPacket(byte[] Bytes, double Time, bool Discontinuity);
    internal sealed record Peak(double Milliseconds, double Score);

    public static Task<LiveAcousticCalibrationResult> MeasureAsync(string firstId, string secondId,
        int originalDelay, Action<int> applyDelay, Func<bool> running,
        CancellationToken token, IProgress<string>? progress)
        => Task.Run(() => Measure(firstId, secondId, originalDelay, applyDelay, running, token, progress), token);

    private static LiveAcousticCalibrationResult Measure(string firstId, string secondId,
        int originalDelay, Action<int> applyDelay, Func<bool> running,
        CancellationToken token, IProgress<string>? progress)
    {
        if (originalDelay < 0) throw new InvalidOperationException("实播校准不会设置负延迟，请先选择设备 1 直出、设备 2 镜像的正向同步。");
        using var enumerator = new MMDeviceEnumerator();
        using var first = new WindowsVolumeEndpoint(firstId);
        using var second = new WindowsVolumeEndpoint(secondId);
        if (first.Volume < .01f || second.Volume < .01f)
            throw new InvalidOperationException("两台设备音量需足够让麦克风听到，校准不会自动调高音量。");
        var firstVolume = first.Volume;
        var secondVolume = second.Volume;
        using var muteScope = new AcousticCalibrationMuteScope(first, second);
        using var delayScope = new AcousticCalibrationDelayScope(originalDelay, applyDelay);
        using var microphone = GetMicrophone(enumerator);
        using var reference = new SystemAudioCapture();
        using var mic = new TimestampedMicrophoneCapture(microphone.ID);
        var packets = new List<AudioPacket>();
        Exception? referenceError = null;
        reference.TimestampedDataAvailable += (bytes, count, time, discontinuity) =>
        {
            if (!time.HasValue) { referenceError = new InvalidOperationException("原音轨时间戳无效，未应用校准。"); return; }
            packets.Add(new(bytes.AsSpan(0, count).ToArray(), time.Value, discontinuity));
        };
        reference.RecordingStopped += (_, e) => referenceError ??= e.Exception;
        reference.StartRecording();
        var phases = new List<Phase>();
        var report = new List<string>
        {
            "实播隔离校准：设备 1 单独出声 / 设备 2 单独出声 / 两台同时播放。",
            "原音轨与麦克风均按 WASAPI QPC 包时间对齐；原音轨的固定时间偏移不作为声学延迟补偿。",
            $"原追加延迟：{originalDelay} ms；只验证正向补偿，不切换负延迟。"
        };
        var initial = new List<(Peak First, Peak Second)>();

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (!running()) throw new InvalidOperationException("同步链路已停止，校准作废；没有保存新延迟。");
            if (Math.Abs(first.Volume - firstVolume) > .005f || Math.Abs(second.Volume - secondVolume) > .005f)
                throw new InvalidOperationException("校准期间设备音量改变，测量作废；没有保存新延迟。");
            if (Volatile.Read(ref referenceError) is Exception error) throw new InvalidOperationException("原音轨采集失败。", error);
        }

        Phase Record(string name, int mode, int number)
        {
            Check();
            muteScope.Select(mode);
            var start = Now();
            for (var secondIndex = 0; secondIndex < 8; secondIndex++)
            {
                progress?.Report($"{name}（阶段 {number}/9，{8 - secondIndex} 秒）：请持续播放音乐，勿调整音量…");
                for (var tick = 0; tick < 10; tick++)
                {
                    if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
                    Check();
                }
            }
            var phase = new Phase(name, start + 1.3, Now() - .5);
            phases.Add(phase);
            return phase;
        }

        // Analyse snapshots only after the producer threads have been stopped.
        // Each round uses fresh captures, avoiding concurrent List reads/writes.
        (Timeline Reference, Timeline Microphone) FinishCapture()
        {
            reference.StopRecording();
            var recordedMic = mic.Finish();
            Check();
            return (Timeline.Create(packets.Select(p => (p.Bytes, p.Time, p.Discontinuity)), reference.WaveFormat),
                Timeline.Create(recordedMic.Select(p => (p.Bytes, p.TimeSeconds, p.Discontinuity)), mic.Format));
        }

        // Record two full rounds on one reference timeline before analysis.
        for (var round = 0; round < 2; round++)
        {
            Record($"第 {round + 1} 轮：仅设备 1 出声", 1, round * 3 + 1);
            Record($"第 {round + 1} 轮：仅设备 2 出声", 2, round * 3 + 2);
            Record($"第 {round + 1} 轮：双设备复核", 0, round * 3 + 3);
        }
        progress?.Report("正在对齐时间戳并计算两路实际出声差…");
        var timelines = FinishCapture();
        for (var round = 0; round < 2; round++)
        {
            Check();
            var a = Analyse(timelines.Reference, timelines.Microphone, phases[round * 3], token);
            var b = Analyse(timelines.Reference, timelines.Microphone, phases[round * 3 + 1], token);
            var both = Correlations(timelines.Reference, timelines.Microphone, phases[round * 3 + 2], token);
            ValidateBoth(both, a.Milliseconds, b.Milliseconds);
            initial.Add((a, b));
            report.Add($"第 {round + 1} 轮：设备 1 {a.Milliseconds:F2} ms（匹配 {a.Score:F3}），设备 2 {b.Milliseconds:F2} ms（匹配 {b.Score:F3}）；差值 {a.Milliseconds - b.Milliseconds:F2} ms；双设备波形复核通过。");
        }
        var differences = initial.Select(p => p.First.Milliseconds - p.Second.Milliseconds).ToArray();
        var candidate = CalculatePositiveDelay(originalDelay, differences);
        var spread = Math.Abs(differences[0] - differences[1]);

        // Verify using the SAME still-running mirror, with the candidate applied.
        // Both the delay and mutes are restored by scopes before returning.
        delayScope.Apply(candidate);
        using var verifyReference = new SystemAudioCapture();
        using var verifyMic = new TimestampedMicrophoneCapture(microphone.ID);
        var verifyPackets = new List<AudioPacket>();
        Exception? verifyError = null;
        verifyReference.TimestampedDataAvailable += (bytes, count, time, discontinuity) =>
        {
            if (!time.HasValue) { verifyError = new InvalidOperationException("复测原音轨时间戳无效。"); return; }
            verifyPackets.Add(new(bytes.AsSpan(0, count).ToArray(), time.Value, discontinuity));
        };
        verifyReference.RecordingStopped += (_, e) => verifyError ??= e.Exception;
        verifyReference.StartRecording();
        var verifyA = Record($"正向 +{candidate} ms 验证：仅设备 1", 1, 7);
        var verifyB = Record($"正向 +{candidate} ms 验证：仅设备 2", 2, 8);
        var verifyBoth = Record($"正向 +{candidate} ms 验证：两台同时出声", 0, 9);
        verifyReference.StopRecording();
        var verifyRecordedMic = verifyMic.Finish();
        Check();
        if (verifyError is not null) throw new InvalidOperationException("复测原音轨采集失败。", verifyError);
        var verifyTimeline = Timeline.Create(verifyPackets.Select(p => (p.Bytes, p.Time, p.Discontinuity)), verifyReference.WaveFormat);
        var verifyMicrophone = Timeline.Create(verifyRecordedMic.Select(p => (p.Bytes, p.TimeSeconds, p.Discontinuity)), verifyMic.Format);
        var verifiedFirst = Analyse(verifyTimeline, verifyMicrophone, verifyA, token);
        var verifiedSecond = Analyse(verifyTimeline, verifyMicrophone, verifyB, token);
        var residual = verifiedFirst.Milliseconds - verifiedSecond.Milliseconds;
        if (Math.Abs(residual) > 8)
            throw new InvalidOperationException($"正向补偿复测仍有约 {Math.Abs(residual):F1} ms 差值，未保存；原延迟已恢复。请保持播放和环境稳定后重试。");
        ValidateBoth(Correlations(verifyTimeline, verifyMicrophone, verifyBoth, token),
            verifiedFirst.Milliseconds, verifiedSecond.Milliseconds);
        report.Add($"候选设备 2 正向追加延迟：{candidate} ms；复测设备 1 {verifiedFirst.Milliseconds:F2} ms、设备 2 {verifiedSecond.Milliseconds:F2} ms；残差 {residual:F2} ms，双设备复核通过。");
        report.Add($"两轮差值波动：{spread:F2} ms；麦克风：{microphone.FriendlyName}。绝对耗时受参考捕获固定偏移影响，只使用同轮相对差值。");
        report.Add("临时静音与原追加延迟由 finally 恢复；校准成功返回后界面才提交验证通过的正向数值。未修改任何应用合成器音量或静音。");
        delayScope.Dispose();
        muteScope.Dispose();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "last-calibration.txt"), report);
        return new(initial.Average(p => p.First.Milliseconds), initial.Average(p => p.Second.Milliseconds),
            candidate, residual, spread, microphone.FriendlyName);
    }

    internal static int CalculatePositiveDelay(int currentDelay, IReadOnlyList<double> differences)
    {
        if (currentDelay < 0 || differences.Count < 2 || differences.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("有效的正向校准数据不足。");
        if (differences.Max() - differences.Min() > 15)
            throw new InvalidOperationException("两轮实际出声差波动超过 15 ms，未保存校准值。");
        var delay = currentDelay + differences.Average();
        if (delay < -3)
            throw new InvalidOperationException("当前方向需要负向补偿才能对齐；按要求不会设置负延迟，也不会交换设备。原设置保持不变。");
        if (delay > 1000) throw new InvalidOperationException("测得延迟超过 1000 ms 可调范围，未保存不完整的补偿。");
        return (int)Math.Round(Math.Max(0, delay));
    }

    private static MMDevice GetMicrophone(MMDeviceEnumerator enumerator)
    {
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
    }
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static Peak Analyse(Timeline reference, Timeline microphone, Phase phase, CancellationToken token)
        => SelectPeak(Correlations(reference, microphone, phase, token));

    internal static Peak SelectPeak(double[] correlations)
    {
        // Match the positive waveform peak used by the successful live test.
        // Taking absolute correlation can select an inverted carrier cycle
        // several milliseconds earlier and change the measured compensation.
        var best = Enumerable.Range(0, correlations.Length).MaxBy(i => correlations[i]);
        var score = correlations[best];
        var competing = Enumerable.Range(0, correlations.Length).Where(i => Math.Abs(i - best) >= 640)
            .Select(i => correlations[i]).DefaultIfEmpty(0).Max();
        if (!double.IsFinite(score) || score < .14 || score < competing * 1.15)
            throw new InvalidOperationException("没有辨认出可信的单设备波形响应。请持续播放有人声或节奏变化的音乐，让麦克风听到两台设备。");
        return new(best / 16.0, score);
    }

    internal static void ValidateBoth(double[] correlations, double firstMs, double secondMs)
    {
        foreach (var expected in new[] { firstMs, secondMs })
        {
            var begin = Math.Max(0, (int)Math.Round((expected - 12) * 16));
            var end = Math.Min(correlations.Length - 1, (int)Math.Round((expected + 12) * 16));
            if (end < begin || Enumerable.Range(begin, end - begin + 1).Max(i => correlations[i]) < .075)
                throw new InvalidOperationException("双设备同时播放复核未找到预期响应；不把相邻小波峰当成同步成功，原延迟保持不变。");
        }
    }

    private static double[] Correlations(Timeline reference, Timeline microphone, Phase phase, CancellationToken token)
    {
        const int rate = 16000;
        var count = (int)((phase.End - phase.Start) * rate);
        if (count < rate * 3) throw new InvalidOperationException("有效实播录音长度不足。");
        var x = reference.Resample(phase.Start, count, rate);
        var y = microphone.Resample(phase.Start, count, rate);
        token.ThrowIfCancellationRequested();
        return Correlate(x, y, rate * 1200 / 1000, token);
    }

    internal static double[] AnalyseRecordedPhase(
        IEnumerable<(byte[] Bytes, double Time, bool Discontinuity)> reference, WaveFormat referenceFormat,
        IEnumerable<(byte[] Bytes, double Time, bool Discontinuity)> microphone, WaveFormat microphoneFormat,
        double start, double end)
        => Correlations(Timeline.Create(reference, referenceFormat), Timeline.Create(microphone, microphoneFormat),
            new Phase("recorded regression", start, end), CancellationToken.None);

    internal static double[] Correlate(double[] x, double[] y, int maxLag, CancellationToken token = default)
    {
        if (x.Length != y.Length || x.Length < 2) throw new ArgumentException("Reference and microphone lengths differ.");
        if (x.Any(v => !double.IsFinite(v)) || y.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("音轨包含无效采样值，未应用校准。");
        var length = 1;
        while (length < x.Length * 2) length <<= 1;
        var a = new Complex[length];
        var b = new Complex[length];
        var mx = x.Average(); var my = y.Average();
        double px = 0, py = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var vx = x[i] - mx; var vy = y[i] - my;
            a[i] = vx; b[i] = vy; px += vx * vx; py += vy * vy;
        }
        if (px < 1e-10 || py < 1e-10) throw new InvalidOperationException("录音或原音轨为静音，未应用校准。");
        Fft(a, false, token); Fft(b, false, token);
        for (var i = 0; i < length; i++) b[i] *= Complex.Conjugate(a[i]);
        Fft(b, true, token);
        var scale = Math.Sqrt(px * py);
        return Enumerable.Range(0, Math.Min(maxLag, x.Length - 1) + 1).Select(i => b[i].Real / scale).ToArray();
    }

    private static void Fft(Complex[] data, bool inverse, CancellationToken token)
    {
        for (int i = 1, j = 0; i < data.Length; i++)
        {
            var bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var size = 2; size <= data.Length; size <<= 1)
        {
            token.ThrowIfCancellationRequested();
            var step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / size);
            for (var start = 0; start < data.Length; start += size)
            {
                var factor = Complex.One;
                for (var k = 0; k < size / 2; k++)
                {
                    var even = data[start + k]; var odd = data[start + k + size / 2] * factor;
                    data[start + k] = even + odd; data[start + k + size / 2] = even - odd;
                    factor *= step;
                }
            }
        }
        if (inverse) for (var i = 0; i < data.Length; i++) data[i] /= data.Length;
    }

    private sealed class Timeline(List<(double Time, float[] Samples)> packets, int sampleRate)
    {
        public static Timeline Create(IEnumerable<(byte[] Bytes, double Time, bool Discontinuity)> source, WaveFormat format)
        {
            var packets = new List<(double Time, float[] Samples)>();
            double previous = double.NegativeInfinity;
            foreach (var packet in source)
            {
                if (!double.IsFinite(packet.Time) || packet.Time <= previous)
                    throw new InvalidOperationException("录音时间轴发生回退，校准作废。");
                packets.Add((packet.Time, DecodeMono(packet.Bytes, format)));
                previous = packet.Time;
            }
            if (packets.Count == 0) throw new InvalidOperationException("没有取得有效录音数据。");
            return new(packets, format.SampleRate);
        }

        public double[] Resample(double start, int count, int rate)
        {
            var result = new double[count];
            var packetIndex = 0;
            var missing = 0;
            for (var i = 0; i < count; i++)
            {
                var time = start + i / (double)rate;
                while (packetIndex + 1 < packets.Count && packets[packetIndex + 1].Time <= time) packetIndex++;
                var packet = packets[packetIndex];
                var frame = (time - packet.Time) * sampleRate;
                if (frame < 0 || frame >= packet.Samples.Length) { missing++; continue; }
                var index = (int)frame;
                var next = Math.Min(index + 1, packet.Samples.Length - 1);
                result[i] = packet.Samples[index] + (packet.Samples[next] - packet.Samples[index]) * (frame - index);
            }
            if (missing > rate / 100) throw new InvalidOperationException("有效测量窗口中有超过 10 ms 的录音缺口，未应用校准。");
            return result;
        }
    }

    private static float[] DecodeMono(byte[] data, WaveFormat format)
    {
        var floatEncoding = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        var width = format.BitsPerSample / 8;
        var frames = data.Length / format.BlockAlign;
        var result = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * width;
                sum += floatEncoding ? format.BitsPerSample switch
                {
                    32 => BitConverter.ToSingle(data, offset), 64 => BitConverter.ToDouble(data, offset),
                    _ => throw new NotSupportedException("不支持此浮点麦克风格式。")
                } : format.BitsPerSample switch
                {
                    16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768.0,
                    24 => ((data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8) / 8388608.0,
                    32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) / 2147483648.0,
                    _ => throw new NotSupportedException("不支持此 PCM 麦克风格式。")
                };
            }
            result[frame] = (float)(sum / format.Channels);
        }
        return result;
    }
}

internal sealed class AcousticCalibrationMuteScope : IDisposable
{
    private readonly IVolumeEndpoint _first, _second;
    private readonly bool _firstMute, _secondMute;
    private bool _changed, _firstRestored, _secondRestored;
    public AcousticCalibrationMuteScope(IVolumeEndpoint first, IVolumeEndpoint second)
    {
        _first = first; _second = second;
        _firstMute = first.Mute; _secondMute = second.Mute;
    }
    public void Select(int onlyDevice)
    {
        if (onlyDevice is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(onlyDevice));
        _changed = true;
        _first.Mute = onlyDevice == 2;
        _second.Mute = onlyDevice == 1;
    }
    public void Dispose()
    {
        if (!_changed) return;
        var errors = new List<Exception>();
        try { if (!_firstRestored) { _first.Mute = _firstMute; _firstRestored = true; } } catch (Exception e) { errors.Add(e); }
        try { if (!_secondRestored) { _second.Mute = _secondMute; _secondRestored = true; } } catch (Exception e) { errors.Add(e); }
        if (errors.Count != 0) throw new AggregateException("无法恢复校准前的设备静音状态。", errors);
    }
}

internal sealed class AcousticCalibrationDelayScope(int original, Action<int> apply) : IDisposable
{
    private bool _changed;
    public void Apply(int delay) { _changed = true; apply(delay); }
    public void Dispose() { if (_changed) { apply(original); _changed = false; } }
}
