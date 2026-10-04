using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio;

internal sealed record LatencyCalibrationResult(
    double Device1LatencyMilliseconds,
    double Device2LatencyMilliseconds,
    int SignedDelayMilliseconds,
    double MeasurementSpreadMilliseconds,
    int TrialCount,
    string MicrophoneName,
    double MirrorToDevice1ExtraMilliseconds = 0,
    double MirrorToDevice2ExtraMilliseconds = 0,
    bool PreferNegativeDirectionAtZero = false,
    double UncompensatedResidualMilliseconds = 0);

internal static class LatencyCalibrationEngine
{
    private const int ChipMilliseconds = 10;
    private static readonly int[] SignalCode =
        [1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1];
    private static int SignalDurationMilliseconds => SignalCode.Length * ChipMilliseconds;

    public static Task<LatencyCalibrationResult> MeasureAsync(
        string firstDeviceId, string secondDeviceId, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
        => Task.Run(() => Measure(firstDeviceId, secondDeviceId, cancellationToken, progress), cancellationToken);

    private static LatencyCalibrationResult Measure(
        string firstDeviceId, string secondDeviceId, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var microphone = GetDefaultMicrophone(enumerator);
        using var muteScope = new CalibrationMuteScope(firstDeviceId, secondDeviceId);
        using var firstOutput = new ContinuousCalibrationOutput(firstDeviceId);
        using var secondOutput = new ContinuousCalibrationOutput(secondDeviceId);

        progress?.Report("正在预热两个输出设备…");
        // Keep both streams alive so Bluetooth buffering stays stable throughout calibration.
        Wait(700, cancellationToken);
        firstOutput.Emit();
        Wait(650, cancellationToken);
        secondOutput.Emit();
        Wait(900, cancellationToken);

        using var capture = new TimestampedMicrophoneCapture(microphone.ID);
        Wait(500, cancellationToken);

        var trials = new List<(int Device, bool Mirrored, double PlayTime)>();
        var order = new[] { 1, 2, 2, 1, 2, 1, 1, 2 };
        foreach (var device in order)
        {
            progress?.Report($"设备 {device} 直出：发声计时（{trials.Count + 1}/16）…");
            var playTime = (device == 1 ? firstOutput : secondOutput).Emit();
            trials.Add((device, false, playTime));
            Wait(SignalDurationMilliseconds + 1400, cancellationToken);
        }
        foreach (var mirroredDevice in new[] { 2, 1 })
        {
            var directId = mirroredDevice == 2 ? firstDeviceId : secondDeviceId;
            var mirrorId = mirroredDevice == 2 ? secondDeviceId : firstDeviceId;
            // Only the direct endpoint is muted, so the microphone hears the
            // actual captured/resampled/rendered copy, not two overlapping tones.
            // Process-loopback is upstream of endpoint volume and includes only this child.
            muteScope.SetDirectMuted(mirroredDevice == 2 ? 1 : 2);
            using var soundSource = new CalibrationSoundSource(directId, cancellationToken);
            using var mirror = new AudioMirrorEngine();
            progress?.Report($"正在预热设备 {mirroredDevice} 的真实镜像路径…");
            mirror.StartCalibration(directId, mirrorId, soundSource.ProcessId);
            soundSource.Emit(cancellationToken);
            Wait(1700, cancellationToken);
            for (var round = 0; round < 4; round++)
            {
                if (!mirror.IsRunning) throw new InvalidOperationException("校准镜像路径意外停止，未应用延迟。");
                progress?.Report($"设备 {mirroredDevice} 镜像：发声计时（{trials.Count + 1}/16）…");
                trials.Add((mirroredDevice, true, soundSource.Emit(cancellationToken)));
                Wait(SignalDurationMilliseconds + 1400, cancellationToken);
            }
            mirror.Stop();
            muteScope.SetDirectMuted(null);
        }
        Wait(700, cancellationToken);

        progress?.Report("正在识别两台设备的响应并计算差值…");
        var packets = capture.Finish();
        var samples = new List<float>();
        var timestamps = new List<double>();
        var gaps = new List<(double Start, double End)>();
        double? previousEnd = null;
        foreach (var packet in packets)
        {
            var decoded = DecodeToMono(packet.Bytes, capture.Format, out var rate);
            if (previousEnd is double previous && packet.TimeSeconds < previous - 0.002)
                throw new InvalidOperationException("麦克风时间轴发生回退，未应用不可靠的校准值。");
            if (previousEnd is double end && (packet.Discontinuity || Math.Abs(packet.TimeSeconds - end) > 0.010))
                gaps.Add((end, packet.TimeSeconds));
            var firstFrame = previousEnd is double boundary
                ? Math.Max(0, (int)Math.Ceiling((boundary - packet.TimeSeconds) * rate)) : 0;
            for (var frame = firstFrame; frame < decoded.Length; frame++)
            {
                samples.Add(decoded[frame]);
                timestamps.Add(packet.TimeSeconds + frame / (double)rate);
            }
            previousEnd = packet.TimeSeconds + decoded.Length / (double)rate;
        }
        var mono = samples.ToArray();
        var sampleTimes = timestamps.ToArray();
        var sampleRate = capture.Format.SampleRate;
        var firstTrials = new List<double>();
        var secondTrials = new List<double>();
        var firstMirrorTrials = new List<double>();
        var secondMirrorTrials = new List<double>();
        var trialRecords = new List<string>();
        foreach (var trial in trials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gaps.Any(gap => gap.Start < trial.PlayTime + 1.6 && gap.End > trial.PlayTime))
            {
                trialRecords.Add($"设备 {trial.Device} {(trial.Mirrored ? "镜像" : "直出")}：命令 QPC {trial.PlayTime:F6} s，本轮录音中断，已剔除。");
                continue;
            }
            var (arrival, confidence) = FindArrival(mono, sampleTimes, sampleRate, trial.PlayTime);
            var latency = (arrival - trial.PlayTime) * 1000.0;
            if (confidence < 0.35 || latency < 0 || latency > 1200)
            {
                trialRecords.Add($"设备 {trial.Device} {(trial.Mirrored ? "镜像" : "直出")}：命令 QPC {trial.PlayTime:F6} s，未识别可靠响应，匹配度 {confidence:F3}，已剔除。");
                continue;
            }
            var measurements = trial.Mirrored
                ? (trial.Device == 1 ? firstMirrorTrials : secondMirrorTrials)
                : (trial.Device == 1 ? firstTrials : secondTrials);
            measurements.Add(latency);
            trialRecords.Add($"设备 {trial.Device} {(trial.Mirrored ? "镜像" : "直出")}：命令 QPC {trial.PlayTime:F6} s，响应 QPC {arrival:F6} s，耗时 {latency:F2} ms，匹配度 {confidence:F3}");
        }

        if (firstTrials.Count < 3 || secondTrials.Count < 3 || firstMirrorTrials.Count < 3 || secondMirrorTrials.Count < 3)
            throw new InvalidOperationException(
                $"有效校准声不足（直出 1/2：{firstTrials.Count}/4、{secondTrials.Count}/4；镜像 1/2：{firstMirrorTrials.Count}/4、{secondMirrorTrials.Count}/4）。" +
                "请保持安静、让两个设备靠近麦克风后重试。");

        var firstLatency = RobustMedian(firstTrials);
        var secondLatency = RobustMedian(secondTrials);
        var firstMirrorLatency = RobustMedian(firstMirrorTrials);
        var secondMirrorLatency = RobustMedian(secondMirrorTrials);
        var spread = new[] { MedianAbsoluteDeviation(firstTrials, firstLatency),
            MedianAbsoluteDeviation(secondTrials, secondLatency),
            MedianAbsoluteDeviation(firstMirrorTrials, firstMirrorLatency),
            MedianAbsoluteDeviation(secondMirrorTrials, secondMirrorLatency) }.Max();
        if (spread > 35)
            throw new InvalidOperationException($"多次测量波动过大（约 {spread:F0} ms）。请保持环境安静并让两个扬声器靠近麦克风后重试。");
        var rawDifference = firstLatency - secondLatency;
        var extraToFirst = firstMirrorLatency - firstLatency;
        var extraToSecond = secondMirrorLatency - secondLatency;
        if (extraToFirst < -10 || extraToSecond < -10)
            throw new InvalidOperationException("测得镜像比同设备直出更早，录音匹配不可靠，未应用延迟。");
        extraToFirst = Math.Max(0, extraToFirst);
        extraToSecond = Math.Max(0, extraToSecond);
        var positiveDelay = rawDifference - extraToSecond;
        var negativeDelay = -rawDifference - extraToFirst;
        var preferNegative = false;
        double residual = 0;
        int signedDelay;
        if (positiveDelay >= 0) signedDelay = (int)Math.Round(positiveDelay);
        else if (negativeDelay >= 0)
        {
            preferNegative = true;
            signedDelay = -(int)Math.Round(negativeDelay);
        }
        else
        {
            // With native direct playback we cannot delay that original stream
            // without a virtual audio endpoint or changing application volume.
            preferNegative = -negativeDelay < -positiveDelay;
            residual = preferNegative ? -negativeDelay : -positiveDelay;
            signedDelay = 0;
        }
        if (Math.Abs(signedDelay) > 1000)
            throw new InvalidOperationException("设备响应差超过可调的 1000 ms 范围，未应用不完整的校准值。");

        var reportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio");
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllLines(Path.Combine(reportDirectory, "last-calibration.txt"),
            new[] { "发声命令 → 麦克风首次识别响应；使用同一个 QPC 时间轴。",
                $"设备 1：{firstLatency:F2} ms；设备 2：{secondLatency:F2} ms。",
                $"原始差值 = 设备 1 - 设备 2 = {rawDifference:F2} ms。",
                $"镜像到设备 1 额外耗时：{extraToFirst:F2} ms；镜像到设备 2：{extraToSecond:F2} ms。",
                $"镜像补偿后的追加延迟：{signedDelay} ms；直出设备 {(preferNegative ? 2 : 1)}；无法消除的估计残差 {residual:F2} ms。",
                "镜像由独立声音源进程经实际捕获、时钟缓冲、重采样和低延迟输出测量；校准结束恢复原端点静音。" }.Concat(trialRecords));

        return new LatencyCalibrationResult(firstLatency, secondLatency, signedDelay, spread,
            firstTrials.Count + secondTrials.Count + firstMirrorTrials.Count + secondMirrorTrials.Count,
            microphone.FriendlyName, extraToFirst, extraToSecond, preferNegative, residual);
    }

    private static MMDevice GetDefaultMicrophone(MMDeviceEnumerator enumerator)
    {
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static float[] DecodeToMono(byte[] bytes, WaveFormat sourceFormat, out int sampleRate)
    {
        var normalized = NormalizeFormat(sourceFormat);
        sampleRate = normalized.SampleRate;
        using var stream = new RawSourceWaveStream(new MemoryStream(bytes), normalized);
        var sampleProvider = stream.ToSampleProvider();
        var channels = sampleProvider.WaveFormat.Channels;
        var interleaved = new float[Math.Max(channels * Math.Min(sampleRate, 4096), 4096)];
        var all = new List<float>();
        int read;
        while ((read = sampleProvider.Read(interleaved, 0, interleaved.Length)) > 0)
        {
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var strongest = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = interleaved[frame * channels + channel];
                    if (Math.Abs(sample) > Math.Abs(strongest)) strongest = sample;
                }
                all.Add(strongest);
            }
        }
        return all.ToArray();
    }

    private static WaveFormat NormalizeFormat(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
            return format;
        if (format is not WaveFormatExtensible extensible)
            throw new NotSupportedException($"无法识别麦克风格式：{format}");

        var ieeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (extensible.SubFormat == ieeeFloat && format.BitsPerSample == 32)
            return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
        if (extensible.SubFormat == pcm)
            return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        throw new NotSupportedException($"暂不支持此麦克风编码：{extensible.SubFormat}");
    }

    private static (double Arrival, double Confidence) FindArrival(float[] samples, double[] sampleTimes, int sampleRate, double playTimeSeconds)
    {
        const int envelopeMilliseconds = 2;
        var blockSize = Math.Max(32, sampleRate * envelopeMilliseconds / 1000);
        var envelope = new double[(samples.Length + blockSize - 1) / blockSize];
        for (var i = 0; i < envelope.Length; i++)
            envelope[i] = Math.Log10(1e-8 + Rms(samples, i * blockSize, blockSize));

        var blocksPerChip = Math.Max(1, ChipMilliseconds / envelopeMilliseconds);
        var reference = new double[SignalCode.Length * blocksPerChip];
        for (var chip = 0; chip < SignalCode.Length; chip++)
            for (var block = 0; block < blocksPerChip; block++)
                reference[chip * blocksPerChip + block] = SignalCode[chip];

        var startIndex = Array.BinarySearch(sampleTimes, playTimeSeconds);
        if (startIndex < 0) startIndex = ~startIndex;
        var endIndex = Array.BinarySearch(sampleTimes, playTimeSeconds + 1.2);
        if (endIndex < 0) endIndex = ~endIndex;
        var searchStart = Math.Max(0, startIndex / blockSize);
        var searchEnd = Math.Min(envelope.Length - reference.Length - 1, endIndex / blockSize);
        if (searchEnd <= searchStart)
            throw new InvalidOperationException("麦克风录音长度不足，无法完成校准。");

        var bestCorrelation = double.NegativeInfinity;
        var bestIndex = searchStart;
        for (var candidate = searchStart; candidate <= searchEnd; candidate++)
        {
            var correlation = NormalizedCorrelation(envelope, candidate, reference);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestIndex = candidate;
            }
        }
        return (sampleTimes[bestIndex * blockSize], bestCorrelation);
    }

    private static double NormalizedCorrelation(double[] source, int offset, double[] reference)
    {
        var sourceMean = 0.0;
        var referenceMean = 0.0;
        for (var i = 0; i < reference.Length; i++)
        {
            sourceMean += source[offset + i];
            referenceMean += reference[i];
        }
        sourceMean /= reference.Length;
        referenceMean /= reference.Length;

        var numerator = 0.0;
        var sourcePower = 0.0;
        var referencePower = 0.0;
        for (var i = 0; i < reference.Length; i++)
        {
            var x = source[offset + i] - sourceMean;
            var y = reference[i] - referenceMean;
            numerator += x * y;
            sourcePower += x * x;
            referencePower += y * y;
        }
        return numerator / Math.Sqrt(Math.Max(1e-12, sourcePower * referencePower));
    }

    private static double RobustMedian(List<double> values)
    {
        var initial = Median(values);
        var mad = MedianAbsoluteDeviation(values, initial);
        var tolerance = Math.Max(12.0, mad * 3.5);
        var filtered = values.Where(value => Math.Abs(value - initial) <= tolerance).ToList();
        return Median(filtered.Count >= 2 ? filtered : values);
    }

    private static double MedianAbsoluteDeviation(List<double> values, double median)
        => Median(values.Select(value => Math.Abs(value - median)).ToList());

    private static double Median(List<double> values)
    {
        if (values.Count == 0) throw new InvalidOperationException("没有取得有效的延迟样本。");
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2.0 : sorted[middle];
    }

    private static double Rms(float[] samples, int offset, int count)
    {
        var sum = 0.0;
        var end = Math.Min(samples.Length, offset + count);
        for (var i = offset; i < end; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, end - offset));
    }

    internal sealed class ContinuousCalibrationOutput : IDisposable
    {
        private MMDevice _device = null!;
        private LowLatencyWasapiOutput _output = null!;
        private ContinuousCalibrationSignalProvider _provider = null!;
        private volatile Exception? _playbackException;

        public ContinuousCalibrationOutput(string deviceId)
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    _device = enumerator.GetDevice(deviceId);
                    using var formatClient = _device.AudioClient;
                    var format = formatClient.MixFormat;
                    _provider = new ContinuousCalibrationSignalProvider(format.SampleRate, format.Channels);
                    _output = new LowLatencyWasapiOutput(deviceId);
                    _output.PlaybackStopped += (_, e) => _playbackException = e.Exception
                        ?? new InvalidOperationException($"设备“{_device.FriendlyName}”的校准音频流意外停止。");
                    _output.Init(_provider);
                    _output.Play();
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    DisposePartial();
                    if (attempt < 2) Thread.Sleep(500);
                }
            }
            throw new InvalidOperationException("无法建立稳定的设备校准音频流。", lastError);
        }

        public double Emit()
        {
            if (_playbackException is not null)
                throw new InvalidOperationException($"设备“{_device.FriendlyName}”的校准音频流已断开。", _playbackException);
            return _provider.EnqueueSignal();
        }

        public void Dispose()
        {
            DisposePartial();
            GC.SuppressFinalize(this);
        }

        private void DisposePartial()
        {
            if (_output is not null)
            {
                try { _output.Stop(); } catch { }
                _output.Dispose();
            }
            _device?.Dispose();
            _output = null!;
            _device = null!;
        }
    }

    private sealed class CalibrationMuteScope : IDisposable
    {
        private readonly MMDevice _first;
        private readonly MMDevice _second;
        private readonly bool _firstMute;
        private readonly bool _secondMute;

        public CalibrationMuteScope(string firstId, string secondId)
        {
            using var enumerator = new MMDeviceEnumerator();
            _first = enumerator.GetDevice(firstId);
            try { _second = enumerator.GetDevice(secondId); }
            catch { _first.Dispose(); throw; }
            _firstMute = _first.AudioEndpointVolume.Mute;
            _secondMute = _second.AudioEndpointVolume.Mute;
            try { SetDirectMuted(null); }
            catch { Dispose(); throw; }
        }

        public void SetDirectMuted(int? device)
        {
            _first.AudioEndpointVolume.Mute = device == 1;
            _second.AudioEndpointVolume.Mute = device == 2;
        }

        public void Dispose()
        {
            var errors = new List<Exception>();
            try { _first.AudioEndpointVolume.Mute = _firstMute; } catch (Exception ex) { errors.Add(ex); }
            try { _second.AudioEndpointVolume.Mute = _secondMute; } catch (Exception ex) { errors.Add(ex); }
            _first.Dispose();
            _second.Dispose();
            if (errors.Count != 0) throw new AggregateException("校准后恢复设备静音失败。", errors);
        }
    }

    private sealed class ContinuousCalibrationSignalProvider : ISampleProvider
    {
        private readonly object _gate = new();
        private readonly Queue<float> _queued = new();
        private readonly int _channels;
        private readonly float[] _signal;

        public ContinuousCalibrationSignalProvider(int sampleRate, int channels)
        {
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _signal = CreateSignal();
        }

        public WaveFormat WaveFormat { get; }

        private float[] CreateSignal()
        {
            var totalFrames = WaveFormat.SampleRate * SignalDurationMilliseconds / 1000;
            var signal = new float[totalFrames * _channels];
            for (var frame = 0; frame < totalFrames; frame++)
            {
                var t = frame / (double)WaveFormat.SampleRate;
                var chip = Math.Min(SignalCode.Length - 1, (int)(t * 1000 / ChipMilliseconds));
                var chipPosition = t * 1000 % ChipMilliseconds / 1000.0;
                var chipDuration = ChipMilliseconds / 1000.0;
                var edge = Math.Min(1.0, Math.Min(chipPosition / 0.0015,
                    (chipDuration - chipPosition) / 0.0015));
                var frequency = 1350 + 2200 * t / (SignalDurationMilliseconds / 1000.0);
                var sample = SignalCode[chip] == 0
                    ? 0f
                    : (float)(0.52 * Math.Sin(2 * Math.PI * frequency * t) * Math.Max(0, edge));
                for (var channel = 0; channel < _channels; channel++)
                    signal[frame * _channels + channel] = sample;
            }
            return signal;
        }

        public double EnqueueSignal()
        {
            lock (_gate)
            {
                if (_queued.Count != 0) throw new InvalidOperationException("前一轮校准声尚未发送完毕。");
                var commandTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                foreach (var sample in _signal) _queued.Enqueue(sample);
                return commandTime;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                for (var i = 0; i < count; i++)
                    buffer[offset + i] = _queued.Count > 0 ? _queued.Dequeue() : 0f;
            }
            return count;
        }
    }
}
