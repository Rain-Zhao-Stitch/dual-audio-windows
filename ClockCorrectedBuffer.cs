using NAudio.Dsp;
using NAudio.Wave;

namespace DualAudio;

// A bounded frame-aligned FIFO with continuous rate correction, not periodic
// overflow drops. Only the FIFO copy is locked; no capture callback needs the engine lock.
internal sealed class ClockCorrectedBuffer : ISampleProvider
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _inputRate;
    private readonly int _channels;
    private readonly int _reserveFrames;
    private readonly WdlResampler _resampler = new();
    private int _head;
    private int _frames;
    private bool _primed;
    private bool _resetRequired;
    private double _filteredError;
    private double _integral;
    private double _correction;
    private float _fade;
    private float[] _decoded = [];
    private double? _oldestTime;
    private double _presentationTime;
    private bool _receivedTimestampedAudio;
    public long StaleRecoveries { get; private set; }
    public double LastTransportMilliseconds { get; private set; }
    public double LastCaptureAgeMilliseconds { get; private set; }
    public long TimestampDiscontinuities { get; private set; }
    public long OverflowRecoveries { get; private set; }
    public long UnderflowRecoveries { get; private set; }
    public double Correction => _correction;
    public int BufferedFrames { get { lock (_gate) return _frames; } }

    public ClockCorrectedBuffer(int inputRate, int outputRate, int channels)
    {
        _inputRate = inputRate;
        _channels = channels;
        _reserveFrames = Math.Max(32, inputRate * 8 / 1000);
        _ring = new float[inputRate / 2 * channels];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, channels);
        _resampler.SetMode(true, 0, true, 32, 16);
        _resampler.SetFeedMode(false);
        _resampler.SetRates(inputRate, outputRate);
    }

    public WaveFormat WaveFormat { get; }

    public void SetPresentationTime(double seconds) => _presentationTime = seconds;

    public void AddTimestampedBytes(byte[] bytes, int count, double? time, bool discontinuity)
    {
        var samples = count / (sizeof(float) * _channels) * _channels;
        if (_decoded.Length < samples) _decoded = new float[samples];
        Buffer.BlockCopy(bytes, 0, _decoded, 0, samples * sizeof(float));
        lock (_gate)
        {
            _receivedTimestampedAudio = true;
            // Reject old capture backlog before it can refill the software FIFO.
            var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            LastCaptureAgeMilliseconds = time.HasValue ? (now - time.Value) * 1000 : double.NaN;
            if (time.HasValue && (time.Value > now + 0.100 || time.Value < now - 0.080))
            {
                StaleRecoveries++;
                _frames = 0;
                _oldestTime = null;
                _resetRequired = true;
                return;
            }
            if (discontinuity || (time.HasValue && _oldestTime.HasValue &&
                Math.Abs(time.Value - (_oldestTime.Value + _frames / (double)_inputRate)) > 0.015))
            {
                TimestampDiscontinuities++;
                _frames = 0;
                _oldestTime = null;
                _resetRequired = true;
            }
            if (_frames == 0) _oldestTime = time;
            else if (!time.HasValue) _oldestTime = null;
            AddSamples(_decoded, 0, samples);
        }
    }

    // Capture always supplies IEEE float32, with complete interleaved frames.
    public void AddBytes(byte[] bytes, int offset, int count)
    {
        var samples = count / (sizeof(float) * _channels) * _channels;
        if (_decoded.Length < samples) _decoded = new float[samples];
        Buffer.BlockCopy(bytes, offset, _decoded, 0, samples * sizeof(float));
        AddSamples(_decoded, 0, samples);
    }

    public void AddSamples(float[] samples, int offset, int count)
    {
        lock (_gate)
        {
            var capacity = _ring.Length / _channels;
            var incoming = count / _channels;
            if (incoming > capacity)
            {
                offset += (incoming - capacity) * _channels;
                incoming = capacity;
            }
            if (_frames + incoming > capacity)
            {
                // Only for a real stall: retain recent audio and re-prime with a fade.
                var keep = Math.Min(_frames, Math.Max(0, _reserveFrames - incoming));
                _head = (_head + _frames - keep) % capacity;
                if (_oldestTime.HasValue) _oldestTime += (_frames - keep) / (double)_inputRate;
                _frames = keep;
                _resetRequired = true;
                OverflowRecoveries++;
            }
            var tail = (_head + _frames) % capacity;
            var first = Math.Min(incoming, capacity - tail);
            Array.Copy(samples, offset, _ring, tail * _channels, first * _channels);
            Array.Copy(samples, offset + first * _channels, _ring, 0, (incoming - first) * _channels);
            _frames += incoming;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var outputFrames = count / _channels;
        var requested = outputFrames * _channels;
        var dt = (double)outputFrames / WaveFormat.SampleRate;
        int needed;
        float[] input;
        int inputOffset;
        lock (_gate)
        {
            if (_resetRequired)
            {
                ResetClock();
                _resetRequired = false;
            }
            var nominalBlock = (int)Math.Ceiling(outputFrames * (double)_inputRate / WaveFormat.SampleRate);
            if (_receivedTimestampedAudio)
            {
                // Keep jitter protection small. A real scheduling stall must not
                // turn into seconds of audible lag while PI correction catches up.
                var limit = _reserveFrames + nominalBlock + _inputRate * 30 / 1000;
                var stale = _oldestTime.HasValue && _presentationTime - _oldestTime.Value > 0.080;
                if (_frames > limit || (stale && _frames > _reserveFrames + nominalBlock + 32))
                {
                    var keep = Math.Min(_frames, _reserveFrames + nominalBlock + 32);
                    if (_oldestTime.HasValue) _oldestTime += (_frames - keep) / (double)_inputRate;
                    _head = (_head + _frames - keep) % (_ring.Length / _channels);
                    _frames = keep;
                    ResetClock();
                    StaleRecoveries++;
                }
                if (_oldestTime.HasValue)
                    LastTransportMilliseconds = Math.Max(0, (_presentationTime - _oldestTime.Value) * 1000);
            }
            if (!_primed)
            {
                if (_frames < _reserveFrames + nominalBlock + 32)
                {
                    Array.Clear(buffer, offset, requested);
                    return requested;
                }
                _primed = true;
            }
            // Measure the reservoir AFTER allowing for this output block. Smooth
            // callback jitter before the PI clock controller sees it.
            var errorSeconds = (double)(_frames - nominalBlock - _reserveFrames) / _inputRate;
            _filteredError += (errorSeconds - _filteredError) * dt / (0.4 + dt);
            _integral = Math.Clamp(_integral + _filteredError * dt * 0.06, -0.008, 0.008);
            var desired = Math.Clamp(_filteredError * 0.6 + _integral, -0.01, 0.01);
            _correction += Math.Clamp(desired - _correction, -dt * 0.002, dt * 0.002);
            _resampler.SetRates(_inputRate * (1 + _correction), WaveFormat.SampleRate);
            needed = _resampler.ResamplePrepare(outputFrames, _channels, out input, out inputOffset);
            if (_frames < needed)
            {
                // A stopped source / major scheduling stall is not clock drift.
                // Drain, fade the last samples, then re-prime instead of buzzing.
                needed = _frames;
                UnderflowRecoveries++;
                _primed = false;
                _resetRequired = true;
            }
            var first = Math.Min(needed, _ring.Length / _channels - _head);
            Array.Copy(_ring, _head * _channels, input, inputOffset, first * _channels);
            Array.Copy(_ring, 0, input, inputOffset + first * _channels, (needed - first) * _channels);
            _head = (_head + needed) % (_ring.Length / _channels);
            if (_oldestTime.HasValue) _oldestTime += needed / (double)_inputRate;
            _frames -= needed;
        }
        var written = _resampler.ResampleOut(buffer, offset, needed, outputFrames, _channels) * _channels;
        var fadeStep = 1f / Math.Max(1, WaveFormat.SampleRate / 100);
        for (var frame = 0; frame < written / _channels; frame++)
        {
            _fade = Math.Min(1, _fade + fadeStep);
            var gain = _fade;
            if (written < requested) gain *= 1f - (float)frame / Math.Max(1, written / _channels);
            for (var ch = 0; ch < _channels; ch++) buffer[offset + frame * _channels + ch] *= gain;
        }
        Array.Clear(buffer, offset + written, requested - written);
        return requested;
    }

    private void ResetClock()
    {
        _primed = false;
        _fade = 0;
        // Re-prime after a scheduling glitch without forgetting the learned
        // device-rate mismatch (otherwise small buffers repeatedly underflow).
        _filteredError = 0;
        _resampler.Reset();
    }
}
