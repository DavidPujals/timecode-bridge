using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TimecodeBridge.Core;

namespace TimecodeBridge.Audio;

/// <summary>
/// WASAPI shared-mode capture (event-driven, 10 ms buffers) of one channel from any
/// Windows recording endpoint. Runs at the endpoint's mix format and rate.
/// </summary>
public sealed class WasapiAudioInput : IAudioInput
{
    readonly MMDeviceEnumerator _enumerator;
    readonly MMDevice _device;
    readonly WasapiCapture _capture;
    readonly FloatRingBuffer _ring;
    readonly AutoResetEvent _signal;
    readonly int _channels;
    readonly int _channel;
    readonly int _blockAlign;
    readonly WaveFormatEncoding _encoding;
    readonly int _bits;
    float[] _conv = Array.Empty<float>();

    public int SampleRate { get; }
    public string Description { get; }
    public event Action<Exception?>? Stopped;

    public WasapiAudioInput(string deviceId, int channel, FloatRingBuffer ring, AutoResetEvent signal)
    {
        _ring = ring;
        _signal = signal;
        _enumerator = new MMDeviceEnumerator();
        try
        {
            _device = _enumerator.GetDevice(deviceId);
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }

        WasapiCapture? capture = null;
        try
        {
            capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 10);
            _capture = capture;
            var wf = _capture.WaveFormat;
            var std = (wf as WaveFormatExtensible)?.ToStandardWaveFormat() ?? wf;
            _encoding = std.Encoding;
            _bits = std.BitsPerSample;
            if (_encoding != WaveFormatEncoding.IeeeFloat && _encoding != WaveFormatEncoding.Pcm)
                throw new NotSupportedException($"Unsupported WASAPI capture format: {wf.Encoding}");
            if (_encoding == WaveFormatEncoding.Pcm && _bits != 16 && _bits != 24 && _bits != 32)
                throw new NotSupportedException($"Unsupported WASAPI PCM depth: {_bits} bit");

            SampleRate = wf.SampleRate;
            _channels = wf.Channels;
            _blockAlign = wf.BlockAlign;
            _channel = Math.Clamp(channel, 0, _channels - 1);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);

            Description = $"WASAPI {_device.FriendlyName} · ch {_channel + 1} · {SampleRate / 1000.0:0.#} kHz";
        }
        catch
        {
            capture?.Dispose();
            _device.Dispose();
            _enumerator.Dispose();
            throw;
        }
    }

    public void Start() => _capture.StartRecording();

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frames = e.BytesRecorded / _blockAlign;
        if (frames <= 0) return;
        if (_conv.Length < frames) _conv = new float[frames];

        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);
        if (_encoding == WaveFormatEncoding.IeeeFloat)
        {
            var src = MemoryMarshal.Cast<byte, float>(bytes);
            for (int i = 0; i < frames; i++) _conv[i] = src[i * _channels + _channel];
        }
        else if (_bits == 16)
        {
            var src = MemoryMarshal.Cast<byte, short>(bytes);
            for (int i = 0; i < frames; i++) _conv[i] = src[i * _channels + _channel] * (1f / 32768f);
        }
        else if (_bits == 32)
        {
            var src = MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < frames; i++) _conv[i] = src[i * _channels + _channel] * (1f / 2147483648f);
        }
        else // 24-bit packed
        {
            for (int i = 0; i < frames; i++)
            {
                int o = i * _blockAlign + _channel * 3;
                int v = (bytes[o] << 8 | bytes[o + 1] << 16 | bytes[o + 2] << 24) >> 8;
                _conv[i] = v * (1f / 8388608f);
            }
        }

        _ring.Write(_conv.AsSpan(0, frames));
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* engine torn down while a late callback was in flight */ }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { /* device already gone */ }
        _capture.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}
