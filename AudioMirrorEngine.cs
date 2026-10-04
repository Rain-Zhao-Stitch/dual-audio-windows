using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DualAudio;

internal sealed class AudioMirrorEngine : IDisposable
{
    private readonly object _gate = new();
    private MMDevice? _sourceDevice;
    private MMDevice? _targetDevice;
    private SystemAudioCapture? _capture;
    private LowLatencyWasapiOutput? _output;
    private ClockCorrectedBuffer? _buffer;
    private GainSampleProvider? _targetVolume;
    private AdjustableDelaySampleProvider? _targetDelay;
    private bool _stopping;

    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;
    public event EventHandler<string>? StoppedUnexpectedly;

    public object GetTimingSnapshot()
    {
        lock (_gate)
            return new
            {
                TimeUtc = DateTime.UtcNow, IsRunning, ProcessId = Environment.ProcessId,
                SourceId = _sourceDevice?.ID, TargetId = _targetDevice?.ID,
                AddedDelayMilliseconds = _targetDelay?.DelayMilliseconds,
                _output?.DevicePeriodMilliseconds, _output?.QueueMilliseconds,
                _buffer?.LastCaptureAgeMilliseconds, _buffer?.LastTransportMilliseconds,
                _buffer?.BufferedFrames, _buffer?.Correction,
                _buffer?.UnderflowRecoveries, _buffer?.OverflowRecoveries,
                _buffer?.StaleRecoveries, _buffer?.TimestampDiscontinuities
            };
    }

    public void Start(string sourceDeviceId, string targetDeviceId, int targetDelayMilliseconds = 0)
        => StartCore(sourceDeviceId, targetDeviceId, 1f, targetDelayMilliseconds);

    public void StartCalibration(string sourceDeviceId, string targetDeviceId, uint includedProcess)
        => StartCore(sourceDeviceId, targetDeviceId, 1f, 0, includedProcess);

    // Retained for the built-in silent diagnostics; ordinary synchronization uses unity gain.
    public void Start(string sourceDeviceId, string targetDeviceId, float sourceVolume, float targetVolume,
        int targetDelayMilliseconds = 0)
        => StartCore(sourceDeviceId, targetDeviceId, targetVolume, targetDelayMilliseconds);

    private void StartCore(string sourceDeviceId, string targetDeviceId, float targetGain,
        int targetDelayMilliseconds, uint? includedProcess = null)
    {
        if (sourceDeviceId == targetDeviceId)
            throw new InvalidOperationException("两个设备不能相同。");

        lock (_gate)
        {
            StopCore();
            _stopping = false;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _sourceDevice = enumerator.GetDevice(sourceDeviceId);
                _targetDevice = enumerator.GetDevice(targetDeviceId);

                _capture = new SystemAudioCapture(includedProcess);
                using var formatClient = _targetDevice.AudioClient;
                var targetFormat = formatClient.MixFormat;
                _buffer = new ClockCorrectedBuffer(_capture.WaveFormat.SampleRate,
                    targetFormat.SampleRate, _capture.WaveFormat.Channels);
                ISampleProvider provider = _buffer;
                if (provider.WaveFormat.Channels != targetFormat.Channels)
                    provider = new ChannelAdapterSampleProvider(provider, targetFormat.Channels);

                _targetDelay = new AdjustableDelaySampleProvider(provider, targetDelayMilliseconds);
                _targetVolume = new GainSampleProvider(_targetDelay) { Gain = targetGain };
                _output = new LowLatencyWasapiOutput(targetDeviceId);
                var timingBuffer = _buffer;
                _output.Init(_targetVolume, timingBuffer.SetPresentationTime);

                _capture.TimestampedDataAvailable += CaptureOnTimestampedDataAvailable;
                _capture.RecordingStopped += CaptureOnRecordingStopped;
                _output.PlaybackStopped += OutputOnPlaybackStopped;
                _isRunning = true;
                _capture.StartRecording();
                _output.Play();
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    public void SetTargetDelay(int milliseconds)
    {
        lock (_gate)
        {
            if (_targetDelay is not null)
                _targetDelay.DelayMilliseconds = Math.Clamp(milliseconds, 0, 1000);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            StopCore();
            _stopping = false;
        }
    }

    private void CaptureOnTimestampedDataAvailable(byte[] bytes, int count, double? time, bool discontinuity)
    {
        // Stop disposes/join-waits capture under _gate. A callback must NEVER
        // acquire that same gate, otherwise stopping can deadlock.
        Volatile.Read(ref _buffer)?.AddTimestampedBytes(bytes, count, time, discontinuity);
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
        => HandleUnexpectedStop(sender, e.Exception, "音频来源已停止或断开");

    private void OutputOnPlaybackStopped(object? sender, StoppedEventArgs e)
        => HandleUnexpectedStop(sender, e.Exception, "第二个输出设备已停止或断开");

    private void HandleUnexpectedStop(object? sender, Exception? exception, string fallbackMessage)
    {
        var message = exception?.Message ?? fallbackMessage;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var shouldNotify = false;
            lock (_gate)
            {
                if (IsRunning && !_stopping && (ReferenceEquals(sender, _capture) || ReferenceEquals(sender, _output)))
                {
                    StopCore();
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
                StoppedUnexpectedly?.Invoke(this, message);
        });
    }

    private void StopCore()
    {
        _isRunning = false;

        if (_capture is not null)
        {
            _capture.TimestampedDataAvailable -= CaptureOnTimestampedDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
        }

        if (_output is not null)
            _output.PlaybackStopped -= OutputOnPlaybackStopped;

        _capture?.Dispose();
        if (_output is not null)
        {
            try { _output.Stop(); } catch { }
            _output.Dispose();
        }
        _sourceDevice?.Dispose();
        _targetDevice?.Dispose();
        _capture = null;
        _output = null;
        _buffer = null;
        _targetVolume = null;
        _targetDelay = null;
        _sourceDevice = null;
        _targetDevice = null;
    }

    public void Dispose() => Stop();

    private static WaveFormat NormalizeLoopbackFormat(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
            return format;

        if (format is not WaveFormatExtensible extensible)
            throw new NotSupportedException($"无法识别来源音频格式：{format}");

        // WASAPI generally exposes its shared-mode mix as WAVE_FORMAT_EXTENSIBLE.
        // NAudio's sample conversion expects the equivalent standard encoding.
        var ieeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (extensible.SubFormat == ieeeFloat && format.BitsPerSample == 32)
            return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
        if (extensible.SubFormat == pcm)
            return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);

        throw new NotSupportedException($"暂不支持此来源音频编码：{extensible.SubFormat}");
    }

    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _gain = 1f;

        public GainSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }
        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, Math.Clamp(value, 0f, 1f));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            var gain = Gain;
            if (gain != 1f)
            {
                for (var i = 0; i < read; i++)
                    buffer[offset + i] *= gain;
            }
            return read;
        }
    }

    private sealed class ChannelAdapterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private readonly int _targetChannels;
        private float[] _sourceBuffer = Array.Empty<float>();

        public ChannelAdapterSampleProvider(ISampleProvider source, int targetChannels)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            _targetChannels = targetChannels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / _targetChannels;
            var needed = requestedFrames * _sourceChannels;
            if (_sourceBuffer.Length < needed)
                _sourceBuffer = new float[needed];

            var sourceSamplesRead = _source.Read(_sourceBuffer, 0, needed);
            var framesRead = sourceSamplesRead / _sourceChannels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var sourceOffset = frame * _sourceChannels;
                var targetOffset = offset + frame * _targetChannels;
                if (_targetChannels == 1)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < _sourceChannels; channel++)
                        sum += _sourceBuffer[sourceOffset + channel];
                    buffer[targetOffset] = sum / _sourceChannels;
                }
                else if (_sourceChannels == 1)
                {
                    for (var channel = 0; channel < _targetChannels; channel++)
                        buffer[targetOffset + channel] = _sourceBuffer[sourceOffset];
                }
                else
                {
                    for (var channel = 0; channel < _targetChannels; channel++)
                        buffer[targetOffset + channel] = channel < _sourceChannels
                            ? _sourceBuffer[sourceOffset + channel]
                            : 0f;
                }
            }

            return framesRead * _targetChannels;
        }
    }

    private sealed class AdjustableDelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Queue<float> _queuedSamples = new();
        private float[] _readBuffer = Array.Empty<float>();
        private int _delayMilliseconds;

        public AdjustableDelaySampleProvider(ISampleProvider source, int delayMilliseconds)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            DelayMilliseconds = delayMilliseconds;
        }

        public WaveFormat WaveFormat { get; }

        public int DelayMilliseconds
        {
            get => Volatile.Read(ref _delayMilliseconds);
            set => Volatile.Write(ref _delayMilliseconds, Math.Clamp(value, 0, 1000));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_readBuffer.Length < count)
                _readBuffer = new float[count];

            var samplesRead = _source.Read(_readBuffer, 0, count);
            for (var i = 0; i < samplesRead; i++)
                _queuedSamples.Enqueue(_readBuffer[i]);

            var channels = WaveFormat.Channels;
            var desiredSamples = WaveFormat.SampleRate * DelayMilliseconds / 1000 * channels;
            desiredSamples -= desiredSamples % channels;

            while (_queuedSamples.Count > desiredSamples + samplesRead)
            {
                for (var channel = 0; channel < channels && _queuedSamples.Count > 0; channel++)
                    _queuedSamples.Dequeue();
            }

            var currentDelay = Math.Max(0, _queuedSamples.Count - samplesRead);
            var silenceSamples = Math.Min(samplesRead, Math.Max(0, desiredSamples - currentDelay));
            silenceSamples -= silenceSamples % channels;

            Array.Clear(buffer, offset, silenceSamples);
            var outputSamples = samplesRead - silenceSamples;
            for (var i = 0; i < outputSamples; i++)
                buffer[offset + silenceSamples + i] = _queuedSamples.Count > 0 ? _queuedSamples.Dequeue() : 0f;

            return samplesRead;
        }
    }
}
