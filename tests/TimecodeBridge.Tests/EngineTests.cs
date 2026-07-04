using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TimecodeBridge.Audio;
using TimecodeBridge.Core;
using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

/// <summary>Feeds pre-generated audio into the ring at ~real-time pace, then silence
/// (or loops the recording forever when <paramref name="loop"/> is set).</summary>
sealed class FakeAudioInput : IAudioInput
{
    readonly FloatRingBuffer _ring;
    readonly AutoResetEvent _signal;
    readonly float[] _audio;
    readonly bool _loop;
    Thread? _thread;
    volatile bool _run;

    public int SampleRate => 48000;
    public string Description => "Fake LTC source";
    public event Action<Exception?>? Stopped;

    public FakeAudioInput(float[] audio, FloatRingBuffer ring, AutoResetEvent signal, bool loop = false)
    {
        _audio = audio;
        _ring = ring;
        _signal = signal;
        _loop = loop;
    }

    public void Start()
    {
        _run = true;
        _thread = new Thread(() =>
        {
            const int chunk = 480; // 10 ms at 48 kHz
            int pos = 0;
            var sw = Stopwatch.StartNew();
            long sent = 0;
            while (_run)
            {
                // Pace to real time, feeding silence once the recording runs out.
                long due = sw.ElapsedMilliseconds * 48;
                while (sent < due && _run)
                {
                    if (pos >= _audio.Length && _loop) pos = 0;
                    if (pos < _audio.Length)
                    {
                        int n = Math.Min(chunk, _audio.Length - pos);
                        _ring.Write(_audio.AsSpan(pos, n));
                        pos += n;
                        sent += n;
                    }
                    else
                    {
                        _ring.Write(new float[chunk]);
                        sent += chunk;
                    }
                    _signal.Set();
                }
                Thread.Sleep(2);
            }
        }) { IsBackground = true };
        _thread.Start();
        _ = Stopped; // silence unused-event warning; the fake never dies
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(2000);
    }
}

public class EngineTests
{
    [Fact]
    public void Streams_frames_then_freewheels_then_stops()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        listener.Client.ReceiveTimeout = 500;

        // 2 s of LTC, then the fake input feeds silence forever.
        var audio = LtcGenerator.Generate(new LtcFrame(9, 0, 0, 0), 50, TimecodeRate.Ebu25, 25.0, 48000);
        const int freewheelFrames = 10;

        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            OffsetFrames = 1,
            FreewheelFrames = freewheelFrames,
        };

        using var engine = new Engine(cfg, _ => { });
        engine.Start();

        var packets = new List<byte[]>();
        var deadline = Stopwatch.StartNew();
        // Collect until the stream + freewheel has clearly finished (no packet for 800 ms).
        var lastPacket = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 10000 && lastPacket.ElapsedMilliseconds < 800)
        {
            try
            {
                IPEndPoint? remote = null;
                packets.Add(listener.Receive(ref remote));
                lastPacket.Restart();
            }
            catch (SocketException) { /* receive timeout — loop */ }
        }
        engine.Stop();

        // ~48 live frames (50 minus lock-up) + exactly freewheelFrames extra.
        Assert.InRange(packets.Count, 40, 48 + freewheelFrames);
        Assert.True(packets.Count >= 45, $"Expected live + freewheel packets, got {packets.Count}");

        // Every packet is a valid, strictly consecutive ArtTimeCode.
        LtcFrame? prev = null;
        foreach (var p in packets)
        {
            Assert.Equal(19, p.Length);
            Assert.Equal(0x97, p[9]);
            Assert.Equal(1, p[18]); // EBU 25
            var f = new LtcFrame(p[17], p[16], p[15], p[14]);
            if (prev is { } pf)
                Assert.True(f.TimeEquals(pf.AddFrames(1, TimecodeRate.Ebu25)),
                    $"Non-consecutive output: {pf} -> {f}");
            prev = f;
        }

        // Offset +1 applied: first packet is at least 09:00:00:02 (lock takes 2 frames).
        var first = packets[0];
        Assert.Equal(9, first[17]);
        Assert.True(first[14] >= 2);

        // After the freewheel window the engine must go silent (verified by the
        // 800 ms no-packet exit above) and report signal loss.
        Engine.UnpackState(engine.StateBits, out _, out _, out int flags);
        Assert.Equal(0, flags & Engine.FlagFreewheel);
    }
}
