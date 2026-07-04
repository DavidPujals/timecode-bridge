using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.Asio;
using TimecodeBridge.Core;

namespace TimecodeBridge.Audio;

/// <summary>
/// ASIO capture of a single input channel. Lowest-latency path (typically 1–10 ms
/// device buffers). The driver callback does a straight sample-format conversion
/// into the ring buffer — no allocation, no locks.
///
/// The device's CURRENT sample rate is followed rather than imposed: clock-slaved
/// drivers like Dante Virtual Soundcard must never have their rate forced by a
/// client (the decoder is rate-agnostic anyway). Only drivers that report no valid
/// rate get one pushed from the preference list. A mid-run device rate change is
/// detected and surfaces as a Stopped event, so the engine reopens at the new rate.
/// </summary>
public sealed class AsioAudioInput : IAudioInput
{
    readonly AsioOut _asio;
    readonly AsioDriverExt? _driverExt; // NAudio keeps this private; see GetDriverExt
    readonly FloatRingBuffer _ring;
    readonly AutoResetEvent _signal;
    float[] _conv = Array.Empty<float>();
    volatile bool _formatErrorRaised;
    volatile bool _rateChangeRaised;
    int _rateCheckCountdown = RateCheckInterval;
    const int RateCheckInterval = 100; // callbacks between rate checks (~0.2–1 s)

    public int SampleRate { get; }
    public string Description { get; }
    public event Action<Exception?>? Stopped;

    public AsioAudioInput(string driverName, int channel, FloatRingBuffer ring, AutoResetEvent signal)
    {
        _ring = ring;
        _signal = signal;
        _asio = new AsioOut(driverName);
        try
        {
            int inputs = _asio.DriverInputChannelCount;
            if (inputs <= 0)
                throw new InvalidOperationException($"ASIO driver '{driverName}' reports no input channels.");
            channel = Math.Clamp(channel, 0, inputs - 1);

            _driverExt = GetDriverExt(_asio);
            double current = _driverExt?.Capabilities.SampleRate ?? 0;
            SampleRate = current is >= 8000 and <= 384000
                ? (int)Math.Round(current)          // follow the device's own clock
                : PickSampleRate(_asio);            // driver reports nothing usable — push one

            _asio.InputChannelOffset = channel;
            _asio.AudioAvailable += OnAudioAvailable;
            _asio.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
            // NAudio only calls SetSampleRate when the requested rate differs from
            // the driver's current one, so this leaves a followed device untouched.
            _asio.InitRecordAndPlayback(null, 1, SampleRate);

            Description = $"ASIO {driverName} · ch {channel + 1} · {SampleRate / 1000.0:0.#} kHz";
        }
        catch
        {
            _asio.Dispose();
            throw;
        }
    }

    /// <summary>AsioOut exposes no public current-rate accessor; reach its private
    /// AsioDriverExt (contract pinned by a unit test against the NAudio version).</summary>
    static AsioDriverExt? GetDriverExt(AsioOut asio)
    {
        try
        {
            var field = typeof(AsioOut).GetField("driver", BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(asio) as AsioDriverExt;
        }
        catch
        {
            return null;
        }
    }

    static int PickSampleRate(AsioOut asio)
    {
        foreach (int rate in stackalloc int[] { 48000, 44100, 96000, 88200, 192000, 32000 })
        {
            try
            {
                if (asio.IsSampleRateSupported(rate)) return rate;
            }
            catch
            {
                // Some drivers throw instead of returning false — try the next rate.
            }
        }
        throw new InvalidOperationException("ASIO driver accepts no standard sample rate.");
    }

    public void Start() => _asio.Play();

    unsafe void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        int n = e.SamplesPerBuffer;
        if (n <= 0 || e.InputBuffers.Length == 0) return;
        if (_conv.Length < n) _conv = new float[n]; // first callback only; size is fixed thereafter

        // The driver updates Capabilities.SampleRate via the ASIO rate-change
        // callback (e.g. DVS switched from 96 k to 48 k). Decoding at the wrong
        // rate misreads timecode timing — trigger a reopen at the new rate instead.
        if (--_rateCheckCountdown <= 0)
        {
            _rateCheckCountdown = RateCheckInterval;
            double now = _driverExt?.Capabilities.SampleRate ?? SampleRate;
            if (!_rateChangeRaised && now is >= 8000 and <= 384000 && Math.Abs(now - SampleRate) > 1)
            {
                _rateChangeRaised = true;
                Stopped?.Invoke(new InvalidOperationException(
                    $"ASIO device sample rate changed to {now / 1000.0:0.#} kHz — reopening"));
                return;
            }
        }

        void* src = (void*)e.InputBuffers[0];
        switch (e.AsioSampleType)
        {
            case AsioSampleType.Int32LSB:
            {
                int* p = (int*)src;
                for (int i = 0; i < n; i++) _conv[i] = p[i] * (1f / 2147483648f);
                break;
            }
            case AsioSampleType.Int16LSB:
            {
                short* p = (short*)src;
                for (int i = 0; i < n; i++) _conv[i] = p[i] * (1f / 32768f);
                break;
            }
            case AsioSampleType.Float32LSB:
            {
                float* p = (float*)src;
                for (int i = 0; i < n; i++) _conv[i] = p[i];
                break;
            }
            case AsioSampleType.Int24LSB:
            {
                byte* p = (byte*)src;
                for (int i = 0; i < n; i++)
                {
                    int v = (p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8;
                    _conv[i] = v * (1f / 8388608f);
                    p += 3;
                }
                break;
            }
            default:
                if (!_formatErrorRaised)
                {
                    _formatErrorRaised = true;
                    Stopped?.Invoke(new NotSupportedException($"Unsupported ASIO sample type: {e.AsioSampleType}"));
                }
                return;
        }

        _ring.Write(_conv.AsSpan(0, n));
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* engine torn down while a late callback was in flight */ }
    }

    public void Dispose()
    {
        try { _asio.Stop(); } catch { /* driver already gone */ }
        _asio.Dispose();
    }
}
