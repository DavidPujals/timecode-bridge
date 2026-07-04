using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TimecodeBridge.Core;

namespace TimecodeBridge.Audio;

/// <summary>
/// Classic Windows audio (WDM/MME) capture via waveIn. Compatibility fallback for
/// devices without a usable WASAPI/ASIO path.
///
/// Captures at the endpoint's native mix rate where it can be discovered (waveIn
/// itself never changes the device's shared-mode format — Windows would otherwise
/// resample to whatever we request, and that conversion plus shallow MME buffering
/// is exactly what a 96 kHz Dante/DVS receive channel doesn't need). Buffering is
/// kept deep (8 × 10 ms) because MME scheduling is jittery under load.
/// </summary>
public sealed class WaveInAudioInput : IAudioInput
{
    readonly WaveInEvent _waveIn;
    readonly FloatRingBuffer _ring;
    readonly AutoResetEvent _signal;
    readonly int _channels;
    readonly int _channel;
    float[] _conv = Array.Empty<float>();

    public int SampleRate { get; }
    public string Description { get; }
    public event Action<Exception?>? Stopped;

    public WaveInAudioInput(int deviceNumber, int channel, FloatRingBuffer ring, AutoResetEvent signal)
    {
        _ring = ring;
        _signal = signal;

        var caps = WaveInEvent.GetCapabilities(deviceNumber);
        _channels = Math.Clamp(caps.Channels, 1, 2);
        _channel = Math.Clamp(channel, 0, _channels - 1);
        SampleRate = GuessEndpointRate(caps.ProductName) ?? 48000;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, _channels),
            BufferMilliseconds = 10,
            NumberOfBuffers = 8,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);

        Description = $"WDM {caps.ProductName} · ch {_channel + 1} · {SampleRate / 1000.0:0.#} kHz";
    }

    /// <summary>Finds the WASAPI endpoint behind a waveIn device (waveIn names are
    /// the endpoint name truncated to 31 chars) and returns its native mix rate,
    /// so capture runs without a sample-rate conversion in the path.</summary>
    static int? GuessEndpointRate(string waveInName)
    {
        try
        {
            string prefix = waveInName.Trim();
            if (prefix.Length == 0) return null;
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    if (device.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        int rate = device.AudioClient.MixFormat.SampleRate;
                        if (rate is >= 8000 and <= 384000) return rate;
                    }
                }
            }
        }
        catch { /* endpoint lookup is best-effort; 48 kHz fallback still decodes */ }
        return null;
    }

    public void Start() => _waveIn.StartRecording();

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var src = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
        int frames = src.Length / _channels;
        if (frames <= 0) return;
        if (_conv.Length < frames) _conv = new float[frames];

        for (int i = 0; i < frames; i++)
            _conv[i] = src[i * _channels + _channel] * (1f / 32768f);

        _ring.Write(_conv.AsSpan(0, frames));
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* engine torn down while a late callback was in flight */ }
    }

    public void Dispose()
    {
        try { _waveIn.StopRecording(); } catch { /* device already gone */ }
        _waveIn.Dispose();
    }
}
