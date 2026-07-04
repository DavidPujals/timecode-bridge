using System.Diagnostics;
using System.Net;
using TimecodeBridge.ArtNet;
using TimecodeBridge.Audio;
using TimecodeBridge.Ltc;

namespace TimecodeBridge.Core;

public sealed class EngineConfig
{
    /// <summary>Creates the audio input. Invoked on the engine worker thread.</summary>
    public required Func<FloatRingBuffer, AutoResetEvent, IAudioInput> InputFactory { get; init; }

    public IPAddress? LocalAddress { get; init; }
    public IPAddress TargetAddress { get; init; } = IPAddress.Broadcast;
    public int Port { get; init; } = ArtNetTimecodeSender.ArtNetPort;

    /// <summary>Optional redundant second target (e.g. a backup console); null = off.</summary>
    public IPAddress? TargetAddress2 { get; init; }
    public IPAddress? LocalAddress2 { get; init; }
    /// <summary>Port for the second target; null = same as <see cref="Port"/> (tests only).</summary>
    public int? BackupPort { get; init; }

    /// <summary>Answer ArtPoll so consoles/scanners can discover the bridge.</summary>
    public bool EnableDiscovery { get; init; } = true;
    public int DiscoveryPort { get; init; } = ArtNetTimecodeSender.ArtNetPort;

    /// <summary>After freewheel runs out (or if no LTC ever arrives), keep generating
    /// timecode from the PC clock indefinitely until real LTC returns.</summary>
    public bool GeneratorFallback { get; init; }

    /// <summary>Force an output rate; null = follow the incoming stream.</summary>
    public TimecodeRate? RateOverride { get; init; }

    /// <summary>Frames added to the decoded value before sending (default +1 compensates
    /// for LTC being fully readable only at the end of the frame it labels).</summary>
    public int OffsetFrames { get; init; } = 1;

    /// <summary>Frames to keep generating after signal loss before going silent.</summary>
    public int FreewheelFrames { get; init; } = 25;
}

/// <summary>
/// Real-time core: pulls samples from the ring buffer on a dedicated highest-priority
/// thread, decodes LTC, and sends one ArtTimeCode packet per frame. Handles freewheel
/// on signal loss and automatic reopening of a failed audio device.
///
/// All UI-visible state is published as single volatile words — the UI polls, no
/// cross-thread marshalling anywhere.
/// </summary>
public sealed class Engine : IDisposable
{
    public const int FlagRunning = 1;
    public const int FlagSignal = 2;
    public const int FlagLocked = 4;
    public const int FlagFreewheel = 8;
    public const int FlagTx = 16;
    public const int FlagGenerator = 32;

    readonly EngineConfig _cfg;
    readonly Action<string> _log;
    readonly FloatRingBuffer _ring = new(1 << 16);
    readonly AutoResetEvent _signal = new(false);

    Thread? _thread;
    volatile bool _run;

    IAudioInput? _input;
    LtcDecoder? _decoder;
    ArtNetTimecodeSender? _sender;
    ArtNetTimecodeSender? _sender2;
    ArtNetNode? _node;
    volatile bool _inputFailed;

    // Published state (packed: f | s<<8 | m<<16 | h<<24 | rate<<32 | flags<<40)
    long _stateBits;
    long _packets;
    long _sendErrors;
    volatile string _status = "Stopped";

    // Freewheel / generator state (worker thread only)
    long _lastRealTicks;
    long _lastTxTicks;
    long _nextFwTicks;
    int _fwCount;
    bool _freewheeling;
    bool _armed;              // a real frame has been sent since the last signal loss
    bool _generator;          // internal generator currently producing frames
    long _nextGenTicks;
    long _engineStartTicks;
    LtcFrame _lastSent;
    TimecodeRate _lastRate = TimecodeRate.Ebu25;
    double _framePeriodTicks;
    long _loggedOverruns;

    public Engine(EngineConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public bool IsRunning => _run;
    public long StateBits => Volatile.Read(ref _stateBits);
    public long PacketsSent => Interlocked.Read(ref _packets);
    public string Status => _status;
    public double MeasuredFps => _decoder?.MeasuredFps ?? 0;
    public float ConsumePeak() => _decoder?.ConsumePeak() ?? 0f;

    public static void UnpackState(long bits, out LtcFrame frame, out TimecodeRate rate, out int flags)
    {
        bool df = (TimecodeRate)((bits >> 32) & 0xFF) == TimecodeRate.Df2997;
        frame = new LtcFrame((int)(bits >> 24) & 0xFF, (int)(bits >> 16) & 0xFF,
                             (int)(bits >> 8) & 0xFF, (int)bits & 0xFF, df);
        rate = (TimecodeRate)((bits >> 32) & 0xFF);
        flags = (int)(bits >> 40);
    }

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("Engine already started.");
        _run = true;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "tc-engine",
        };
        // ASIO drivers are conventionally apartment-threaded COM objects; the worker
        // creates, calls and disposes them, so give it an STA. WASAPI/WaveIn are fine
        // with either apartment.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    bool _workerAbandoned;

    public void Stop()
    {
        _run = false;
        _signal.Set();
        if (_thread != null && !_thread.Join(5000))
        {
            _workerAbandoned = true;
            _log("Engine worker did not exit within 5 s (driver hang?) — abandoning thread");
        }
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        // If the worker is stuck inside a driver call, leak the event handle rather
        // than risk it waking to a disposed wait handle later.
        if (!_workerAbandoned)
            _signal.Dispose();
    }

    void WorkerLoop()
    {
        _engineStartTicks = Stopwatch.GetTimestamp();
        try
        {
            _sender = new ArtNetTimecodeSender(_cfg.LocalAddress, _cfg.TargetAddress, _cfg.Port);
        }
        catch (Exception ex)
        {
            _status = "Art-Net socket error: " + ex.Message;
            _log("Art-Net socket error: " + ex);
            _run = false;
            PublishState();
            return;
        }

        if (_cfg.TargetAddress2 != null)
        {
            try
            {
                _sender2 = new ArtNetTimecodeSender(
                    _cfg.LocalAddress2 ?? _cfg.LocalAddress, _cfg.TargetAddress2, _cfg.BackupPort ?? _cfg.Port);
                _log($"Redundant output enabled → {_cfg.TargetAddress2}");
            }
            catch (Exception ex)
            {
                _log("Backup output failed to open (primary continues): " + ex.Message);
            }
        }

        if (_cfg.EnableDiscovery)
        {
            try { _node = new ArtNetNode(_cfg.LocalAddress, _cfg.TargetAddress, _cfg.DiscoveryPort); }
            catch (Exception ex) { _log("Art-Net discovery unavailable: " + ex.Message); }
        }

        var buf = new float[8192];
        long lastOpenAttempt = 0;
        long lastOverrunLog = 0;
        long lastIterationError = 0;

        while (_run)
        {
            // The worker must survive anything — a single escaped exception here
            // would silently kill timecode output for the rest of the show.
            try
            {
                // (Re)open the audio input if missing or failed, throttled to every 2 s.
                if (_input == null || _inputFailed)
                {
                    TearDownInput();
                    long now = Stopwatch.GetTimestamp();
                    if (lastOpenAttempt == 0 || now - lastOpenAttempt > 2 * Stopwatch.Frequency)
                    {
                        lastOpenAttempt = now;
                        TryOpenInput();
                    }
                    if (_input == null)
                    {
                        // Keep freewheel/generator ticking even while the device is
                        // down; while armed, poll fast so freewheel onset isn't
                        // delayed by a device that died mid-signal.
                        _signal.WaitOne(_freewheeling || _generator ? 2 : _armed ? 10 : 200);
                        CheckFreewheel();
                        CheckGenerator();
                        PublishState();
                        continue;
                    }
                }

                // Freewheel/generator pacing needs ~2 ms ticks; otherwise the audio
                // event wakes us the moment samples arrive, so a longer timeout
                // costs no latency.
                _signal.WaitOne(_freewheeling || _generator ? 2 : _armed ? 10 : 50);

                var decoder = _decoder!;
                int n;
                while ((n = _ring.Read(buf)) > 0)
                    decoder.Process(buf.AsSpan(0, n));

                long overruns = _ring.Overruns;
                if (overruns > _loggedOverruns &&
                    Stopwatch.GetTimestamp() - lastOverrunLog > 5 * Stopwatch.Frequency)
                {
                    _loggedOverruns = overruns;
                    lastOverrunLog = Stopwatch.GetTimestamp();
                    _log($"Audio ring buffer overrun (total {overruns} samples dropped)");
                }

                CheckFreewheel();
                CheckGenerator();
                PublishState();
            }
            catch (Exception ex)
            {
                if (Stopwatch.GetTimestamp() - lastIterationError > 5 * Stopwatch.Frequency)
                {
                    lastIterationError = Stopwatch.GetTimestamp();
                    _log("Engine iteration error (recovering): " + ex);
                }
                _status = "Recovering: " + ex.Message;
                _inputFailed = true; // rebuild input + decoder from scratch
                Thread.Sleep(50);
            }
        }

        TearDownInput();
        _node?.Dispose();
        _node = null;
        _sender2?.Dispose();
        _sender2 = null;
        _sender?.Dispose();
        _sender = null;
        _status = "Stopped";
        PublishState();
    }

    void TryOpenInput()
    {
        try
        {
            _inputFailed = false;
            var input = _cfg.InputFactory(_ring, _signal);
            var decoder = new LtcDecoder(input.SampleRate) { FrameDecoded = OnFrameDecoded };
            // Discard anything a previous input generation left behind — stale
            // samples at a different rate would feed the fresh decoder mis-timed bits.
            _ring.Clear();
            input.Stopped += ex =>
            {
                // A stale event from an already-replaced input must not tear down
                // its healthy successor.
                if (!ReferenceEquals(input, _input)) return;
                _inputFailed = true;
                if (ex != null) _log("Audio input stopped: " + ex.Message);
                try { _signal.Set(); }
                catch (ObjectDisposedException) { /* engine already disposed */ }
            };
            _decoder = decoder;
            _input = input;
            input.Start();
            _openFailures = 0;
            _status = "Listening — " + input.Description;
            _log("Input started: " + input.Description);
        }
        catch (Exception ex)
        {
            TearDownInput();
            _status = "Input error (retrying): " + ex.Message;
            // A missing device retries every 2 s for potentially hours — log the
            // first failure and then only every 50th (~100 s apart).
            if (_openFailures++ % 50 == 0)
                _log($"Input open failed (attempt {_openFailures}): {ex.Message}");
        }
    }

    int _openFailures;

    void TearDownInput()
    {
        if (_input == null) return;
        try { _input.Dispose(); }
        catch (Exception ex) { _log("Input dispose error: " + ex.Message); }
        _input = null;
        _inputFailed = false;
        // A frozen decoder would latch SignalPresent/Locked forever (its sample
        // counter stops advancing) — the UI must show the truth: no input, no signal.
        _decoder = null;
    }

    void OnFrameDecoded(LtcFrame frame)
    {
        var decoder = _decoder!;
        var rate = _cfg.RateOverride ?? decoder.DetectedRate;
        var output = _cfg.OffsetFrames == 0 ? frame : frame.AddFrames(_cfg.OffsetFrames, rate);

        if (_generator)
        {
            _generator = false;
            _log("LTC restored — internal generator stopped");
        }
        Send(output, rate);
        _lastRealTicks = Stopwatch.GetTimestamp();
        _freewheeling = false;
        _fwCount = 0;
        _armed = true;

        double spf = decoder.AvgSamplesPerFrame;
        _framePeriodTicks = spf > 0
            ? spf / decoder.SampleRate * Stopwatch.Frequency
            : Stopwatch.Frequency / (double)LtcFrame.NominalFps(rate);
    }

    void Send(in LtcFrame frame, TimecodeRate rate)
    {
        if (_sender2 != null)
        {
            try { _sender2.Send(frame, rate); }
            catch (Exception ex)
            {
                // Separate rate-limiter from the primary — backup chatter must never
                // mask the log record of a primary output failure.
                long t = Stopwatch.GetTimestamp();
                if (t - _lastSendErrorLog2 > 10 * Stopwatch.Frequency)
                {
                    _lastSendErrorLog2 = t;
                    _log("Backup output send error: " + ex.Message);
                }
            }
        }
        try
        {
            _sender!.Send(frame, rate);
            Interlocked.Increment(ref _packets);
            _lastSent = frame;
            _lastRate = rate;
            _lastTxTicks = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            // Rate-limited, not capped: an outage on day 12 must still reach the log.
            long now = Stopwatch.GetTimestamp();
            if (now - _lastSendErrorLog > 10 * Stopwatch.Frequency)
            {
                _lastSendErrorLog = now;
                _log($"Art-Net send error (total {Interlocked.Read(ref _sendErrors)}): {ex.Message}");
            }
            _status = "Art-Net send error: " + ex.Message;
        }
    }

    long _lastSendErrorLog;
    long _lastSendErrorLog2;

    void CheckFreewheel()
    {
        if (!_armed || _framePeriodTicks <= 0) return;

        long now = Stopwatch.GetTimestamp();
        if (!_freewheeling)
        {
            if (now - _lastRealTicks <= _framePeriodTicks * 1.75) return;
            if (_cfg.FreewheelFrames <= 0)
            {
                // Freewheel disabled: hand straight to the generator (or go silent).
                _armed = false;
                if (_cfg.GeneratorFallback)
                {
                    _generator = true;
                    _nextGenTicks = _lastRealTicks + (long)(2 * _framePeriodTicks);
                    _status = "LTC lost — internal generator running";
                    _log("Signal lost — internal generator running (freewheel disabled)");
                }
                else
                {
                    _status = "Signal lost";
                    _log("Signal lost (freewheel disabled)");
                }
                return;
            }
            _freewheeling = true;
            _fwCount = 0;
            _nextFwTicks = _lastRealTicks + (long)(2 * _framePeriodTicks);
            _log("Signal dropped — freewheeling");
        }

        while (_freewheeling && now >= _nextFwTicks)
        {
            if (_fwCount >= _cfg.FreewheelFrames)
            {
                _freewheeling = false;
                _armed = false;
                if (_cfg.GeneratorFallback)
                {
                    // Hand over to the internal generator without a gap in cadence.
                    _generator = true;
                    _nextGenTicks = _nextFwTicks;
                    _status = "LTC lost — internal generator running";
                    _log($"Freewheel ended after {_fwCount} frames — internal generator running");
                }
                else
                {
                    _status = "Signal lost (freewheel ended)";
                    _log($"Signal lost — freewheel ended after {_fwCount} frames");
                }
                return;
            }
            _lastSent = _lastSent.AddFrames(1, _lastRate);
            Send(_lastSent, _lastRate);
            _fwCount++;
            _nextFwTicks += (long)_framePeriodTicks;
        }
    }

    void CheckGenerator()
    {
        if (!_cfg.GeneratorFallback) return;
        long now = Stopwatch.GetTimestamp();

        if (!_generator)
        {
            // Cold start: nothing was ever sent and no LTC is present 3 s after
            // engine start — begin free-running from 00:00:00:00.
            if (_armed || _freewheeling) return;
            if (_lastTxTicks != 0) return; // ended freewheel without generator opt-in handled above
            if (now - _engineStartTicks < 3 * Stopwatch.Frequency) return;
            if (_decoder?.SignalPresent == true) return;

            var rate = _cfg.RateOverride ?? TimecodeRate.Ebu25;
            _lastRate = rate;
            _lastSent = LtcFrame.FromFrameNumber(-1, rate); // first send is 00:00:00:00
            _framePeriodTicks = rate == TimecodeRate.Df2997
                ? Stopwatch.Frequency * 1.001 / 30.0
                : Stopwatch.Frequency / (double)LtcFrame.NominalFps(rate);
            _nextGenTicks = now;
            _generator = true;
            _status = "No LTC — internal generator running from 00:00:00:00";
            _log("Internal generator started (no LTC present)");
        }

        // After a system suspend or a long stall, resync rather than bursting
        // thousands of catch-up frames at the receiver.
        if (now - _nextGenTicks > 2 * Stopwatch.Frequency)
            _nextGenTicks = now;

        while (_generator && now >= _nextGenTicks)
        {
            _lastSent = _lastSent.AddFrames(1, _lastRate);
            Send(_lastSent, _lastRate);
            _nextGenTicks += (long)_framePeriodTicks;
        }
    }

    void PublishState()
    {
        var decoder = _decoder;
        long now = Stopwatch.GetTimestamp();
        int flags = 0;
        if (_run) flags |= FlagRunning;
        if (decoder?.SignalPresent == true) flags |= FlagSignal;
        if (decoder?.Locked == true) flags |= FlagLocked;
        if (_freewheeling) flags |= FlagFreewheel;
        if (_generator) flags |= FlagGenerator;
        if (now - _lastTxTicks < Stopwatch.Frequency / 2 && _lastTxTicks != 0) flags |= FlagTx;

        long bits = _lastSent.Frames
                    | (long)_lastSent.Seconds << 8
                    | (long)_lastSent.Minutes << 16
                    | (long)_lastSent.Hours << 24
                    | (long)(byte)_lastRate << 32
                    | (long)flags << 40;
        Volatile.Write(ref _stateBits, bits);
    }
}
