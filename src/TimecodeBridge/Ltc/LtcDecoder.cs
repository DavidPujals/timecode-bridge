namespace TimecodeBridge.Ltc;

/// <summary>
/// Decodes SMPTE 12M linear timecode (LTC) from a mono audio sample stream.
///
/// Design goals for show-critical use:
///  - Zero heap allocation on the processing path.
///  - Sample-rate agnostic (anything from 8 kHz up; 44.1–192 kHz typical).
///  - Polarity insensitive (biphase-mark), tolerant of DC offset, low level and ±10% varispeed.
///  - A frame is only emitted after two consecutive, range-valid, contiguous frames have
///    been decoded, so a corrupted stream can never emit garbage timecode.
///
/// Not thread-safe: Process() must always be called from the same thread. Status
/// properties are safe to read from other threads (single-word volatile snapshots).
/// </summary>
public sealed class LtcDecoder
{
    /// <summary>Invoked synchronously from Process() for every confirmed frame.</summary>
    public Action<LtcFrame>? FrameDecoded;

    readonly double _sampleRate;
    readonly double _minBitPeriod;   // absolute plausibility bounds for one bit cell
    readonly double _maxBitPeriod;
    readonly double _minFrameSpan;   // absolute plausibility bounds for one frame
    readonly double _maxFrameSpan;
    readonly float _envDecay;

    // DC-blocking one-pole high-pass
    float _dcX, _dcY;
    // signal envelope follower + Schmitt trigger state
    float _envelope;
    bool _high;
    // transition timing
    long _sampleCount;
    long _lastTransition;
    double _bitPeriod;
    bool _pendingHalf;
    // 80-bit shift register: bit i of the LTC frame ends up at position i once the
    // sync word (frame bits 64..79) matches. _regHi holds bits 64..79.
    ulong _regLo;
    uint _regHi;
    // frame cadence tracking
    long _lastSync;
    double _avgSamplesPerFrame;
    bool _haveAvg;
    int _cadenceRejects;
    double _prevSpan; // last plausible sync-to-sync span; 0 = no candidate yet
    // output confirmation chain
    LtcFrame _lastFrame;
    bool _haveLast;
    int _consecutive;

    volatile bool _locked;
    float _peak;

    // Sync word (frame bits 64..79 = 0011111111111101) as it appears in _regHi.
    const uint SyncPattern = 0xBFFC;

    public LtcDecoder(int sampleRate)
    {
        if (sampleRate < 8000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate too low to decode LTC.");
        _sampleRate = sampleRate;
        // 80 bit cells per frame; allow 21..34 fps to cover 24-30 fps plus varispeed.
        _minBitPeriod = sampleRate / (80.0 * 34.0);
        _maxBitPeriod = sampleRate / (80.0 * 21.0);
        _minFrameSpan = sampleRate / 40.0;
        _maxFrameSpan = sampleRate / 20.0;
        _bitPeriod = sampleRate / 2000.0; // bootstrap at 25 fps; adapts within a few bits
        _envDecay = (float)Math.Exp(-1.0 / (0.05 * sampleRate)); // ~50 ms envelope decay
        _lastSync = -sampleRate; // "no sync seen recently" at startup
        _lastTransition = 0;
    }

    public int SampleRate => (int)_sampleRate;

    /// <summary>True once two consecutive contiguous frames have been decoded.</summary>
    public bool Locked => _locked;

    /// <summary>A sync word was seen within the last 250 ms of audio.</summary>
    public bool SignalPresent => _sampleCount - Volatile.Read(ref _lastSync) < _sampleRate / 4;

    /// <summary>Smoothed measured samples per frame (0 until measured).</summary>
    public double AvgSamplesPerFrame => _haveAvg ? _avgSamplesPerFrame : 0;

    /// <summary>Measured incoming frame rate (0 until measured). Read cross-thread:
    /// guard against seeing the flag before the average is visible.</summary>
    public double MeasuredFps
    {
        get
        {
            double avg = _avgSamplesPerFrame;
            return _haveAvg && avg > 1 ? _sampleRate / avg : 0;
        }
    }

    /// <summary>Rate classification of the incoming stream (nominal fps + drop-frame flag).</summary>
    public TimecodeRate DetectedRate { get; private set; } = TimecodeRate.Ebu25;

    /// <summary>Returns the peak absolute input level since the last call, then resets it.</summary>
    public float ConsumePeak()
    {
        float p = Volatile.Read(ref _peak);
        Volatile.Write(ref _peak, 0f);
        return p;
    }

    public void Reset()
    {
        _dcX = _dcY = 0;
        _envelope = 0;
        _high = false;
        _lastTransition = _sampleCount;
        _bitPeriod = _sampleRate / 2000.0;
        _pendingHalf = false;
        _regLo = 0;
        _regHi = 0;
        _lastSync = _sampleCount - (long)_sampleRate;
        _avgSamplesPerFrame = 0;
        _haveAvg = false;
        _cadenceRejects = 0;
        _prevSpan = 0;
        _haveLast = false;
        _consecutive = 0;
        _locked = false;
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        float dcX = _dcX, dcY = _dcY, env = _envelope;
        bool high = _high;
        float chunkPeak = 0f;

        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            _sampleCount++;

            // Drivers can hand over garbage buffers (unplug races, glitches). A single
            // NaN would otherwise poison the recursive DC filter and envelope forever,
            // and huge finite values would deafen the envelope follower for seconds —
            // real audio never exceeds a few units of full scale, so clamp hard.
            if (!float.IsFinite(x)) x = 0f;
            else if (x > 16f) x = 16f;
            else if (x < -16f) x = -16f;

            float ax = Math.Abs(x);
            if (ax > chunkPeak) chunkPeak = ax;

            // DC removal (one-pole high-pass, fc well below the LTC band)
            float y = x - dcX + 0.995f * dcY;
            if (y is > -1e-25f and < 1e-25f) y = 0f; // flush denormals — they cost ~100× per op
            dcX = x;
            dcY = y;

            // Envelope follower drives an adaptive Schmitt threshold, so decode
            // works from full-scale down to very low line levels.
            env *= _envDecay;
            float ay = Math.Abs(y);
            if (ay > env) env = ay;
            // Floor ≈ -60 dBFS (threshold ≈ -70 dBFS). Any real LTC feed is far above
            // this; below it lives digital-silence junk — dither, driver glitches on
            // idle DVS/virtual inputs — which must never register as transitions.
            if (env < 1e-3f) env = 1e-3f;
            float threshold = env * 0.3f;

            if (!high)
            {
                if (y > threshold) { high = true; OnTransition(); }
            }
            else
            {
                if (y < -threshold) { high = false; OnTransition(); }
            }
        }

        _dcX = dcX;
        _dcY = dcY;
        _envelope = float.IsFinite(env) ? env : 1e-4f; // belt-and-braces vs poisoned state
        _high = high;
        if (chunkPeak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, chunkPeak);

        // Drop lock if the stream has gone quiet for several frames. The measured
        // rate is forgotten too: a latched fps would otherwise let any later stray
        // sync-looking blip light SIGNAL on an idle input (seen on silent DVS inputs).
        if (_locked || _haveLast || _haveAvg)
        {
            double frameSpan = _haveAvg ? _avgSamplesPerFrame : _sampleRate / 24.0;
            if (_sampleCount - _lastSync > 4 * frameSpan)
            {
                _locked = false;
                _haveLast = false;
                _consecutive = 0;
                _pendingHalf = false;
                _regLo = 0;
                _regHi = 0;
                _haveAvg = false;
                _avgSamplesPerFrame = 0;
                _cadenceRejects = 0;
                _prevSpan = 0;
            }
        }
    }

    void OnTransition()
    {
        long interval = _sampleCount - _lastTransition;
        double r = interval / _bitPeriod;

        if (r < 0.3)
            return; // noise glitch — merge into the next interval

        _lastTransition = _sampleCount;

        if (r < 0.75)
        {
            // Half cell. Two consecutive halves make a '1'.
            if (_pendingHalf)
            {
                _pendingHalf = false;
                UpdateBitPeriod(interval * 2.0);
                EmitBit(1);
            }
            else
            {
                _pendingHalf = true;
            }
        }
        else if (r <= 1.6)
        {
            // Full cell = '0'. A dangling half before it was a framing error; drop it.
            _pendingHalf = false;
            UpdateBitPeriod(interval);
            EmitBit(0);
        }
        else
        {
            // Gap / dropout — clear framing so stale bits can't fake a sync word.
            _pendingHalf = false;
            _regLo = 0;
            _regHi = 0;
        }
    }

    void UpdateBitPeriod(double measured)
    {
        if (measured < _minBitPeriod || measured > _maxBitPeriod) return;
        _bitPeriod += (measured - _bitPeriod) * 0.15;
    }

    void EmitBit(int bit)
    {
        _regLo = (_regLo >> 1) | ((ulong)(_regHi & 1) << 63);
        _regHi = (_regHi >> 1) | ((uint)bit << 15);
        if (_regHi == SyncPattern)
            OnSyncWord();
    }

    void OnSyncWord()
    {
        long span = _sampleCount - _lastSync;

        // A sync pattern less than ~0.75 of a frame after the previous one can only be
        // user-bit data mimicking the sync word — ignore it outright.
        if (span < _minFrameSpan)
            return;

        Volatile.Write(ref _lastSync, _sampleCount);

        bool cadenceOk = span <= _maxFrameSpan;
        if (cadenceOk)
        {
            if (!_haveAvg)
            {
                // Trust a frame rate only after two CONSISTENT consecutive spans
                // (i.e. three evenly spaced sync words). A coincidental pair of
                // noise-made sync patterns must not establish a rate.
                if (_prevSpan > 0 && Math.Abs(span - _prevSpan) < 0.1 * _prevSpan)
                {
                    _avgSamplesPerFrame = span;
                    _haveAvg = true;
                    _cadenceRejects = 0;
                }
                _prevSpan = span;
            }
            else if (Math.Abs(span - _avgSamplesPerFrame) < 0.1 * _avgSamplesPerFrame)
            {
                _avgSamplesPerFrame += (span - _avgSamplesPerFrame) * 0.1;
                _cadenceRejects = 0;
            }
            else if (++_cadenceRejects >= 4)
            {
                // Rate genuinely changed (e.g. different source plugged in) — re-seed.
                _avgSamplesPerFrame = span;
                _cadenceRejects = 0;
                _consecutive = 0;
            }
        }
        else
        {
            _prevSpan = 0; // a gap breaks the consistency chain
        }

        // Extract BCD fields from frame bits 0..63 (now sitting in _regLo).
        ulong d = _regLo;
        int fu = (int)(d & 0xF);
        int ft = (int)(d >> 8) & 0x3;
        bool dfBit = ((d >> 10) & 1) != 0;
        int su = (int)(d >> 16) & 0xF;
        int st = (int)(d >> 24) & 0x7;
        int mu = (int)(d >> 32) & 0xF;
        int mt = (int)(d >> 40) & 0x7;
        int hu = (int)(d >> 48) & 0xF;
        int ht = (int)(d >> 56) & 0x3;

        int frames = ft * 10 + fu;
        int seconds = st * 10 + su;
        int minutes = mt * 10 + mu;
        int hours = ht * 10 + hu;

        // Range validation. Invalid BCD (corruption or a false sync inside user bits)
        // is skipped without disturbing the confirmation chain.
        if (fu > 9 || su > 9 || mu > 9 || hu > 9 ||
            frames > 29 || seconds > 59 || minutes > 59 || hours > 23)
            return;

        // Classify the rate. The drop-frame bit is only meaningful in the 30 fps family.
        int nominal = NominalFromAverage();
        bool df = dfBit && nominal == 30;
        if (_haveAvg)
        {
            DetectedRate = df ? TimecodeRate.Df2997
                : nominal == 24 ? TimecodeRate.Film24
                : nominal == 25 ? TimecodeRate.Ebu25
                : TimecodeRate.Smpte30;
        }

        var frame = new LtcFrame(hours, minutes, seconds, frames, df);

        if (frames >= LtcFrame.NominalFps(DetectedRate) && _haveAvg)
            return; // frame count impossible for the measured rate

        if (_haveLast && _haveAvg && frame.TimeEquals(_lastFrame.AddFrames(1, DetectedRate)))
            _consecutive++;
        else
            _consecutive = 0;

        _lastFrame = frame;
        _haveLast = true;

        if (_consecutive >= 1)
        {
            _locked = true;
            FrameDecoded?.Invoke(frame);
        }
    }

    int NominalFromAverage()
    {
        if (!_haveAvg) return 25;
        double fps = _sampleRate / _avgSamplesPerFrame;
        if (fps < 24.5) return 24;
        if (fps < 27.5) return 25;
        return 30;
    }
}
