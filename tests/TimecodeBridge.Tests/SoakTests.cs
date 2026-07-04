using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TimecodeBridge.Core;
using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

/// <summary>
/// Long-running leak/robustness tests for show-critical confidence. These take tens
/// of seconds by design — they simulate hours of behaviour compressed into loops.
/// The process-wide handle/thread measurements require that nothing else runs
/// concurrently, so the whole suite is serialised into one collection.
/// </summary>
[CollectionDefinition("soak", DisableParallelization = true)]
public class SoakCollection { }

[Collection("soak")]
public class SoakTests
{
    static void ForceFullGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [Fact]
    public void Continuous_streaming_does_not_grow_memory_or_handles()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var drain = new Thread(() =>
        {
            try { IPEndPoint? r = null; while (true) listener.Receive(ref r); }
            catch (SocketException) { } catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        drain.Start();

        // 10 s of LTC looped forever — decoder relocks at each loop seam, which also
        // exercises the resync path continuously.
        var audio = LtcGenerator.Generate(new LtcFrame(1, 0, 0, 0), 250, TimecodeRate.Ebu25, 25.0, 48000);
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt, loop: true),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();

        Thread.Sleep(5000); // warm-up: JIT, buffers, first GC generation sizing
        ForceFullGc();
        long managed0 = GC.GetTotalMemory(true);
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        int handles0 = proc.HandleCount;
        int threads0 = proc.Threads.Count;
        long packets0 = engine.PacketsSent;

        Thread.Sleep(25000); // sustained run

        ForceFullGc();
        long managed1 = GC.GetTotalMemory(true);
        proc.Refresh();
        int handles1 = proc.HandleCount;
        int threads1 = proc.Threads.Count;
        long packets1 = engine.PacketsSent;

        engine.Stop();

        // It must actually have been working the whole time (~25 fps).
        Assert.True(packets1 - packets0 > 500, $"Only {packets1 - packets0} packets in 25 s");
        // Steady state: no managed growth beyond noise, no handle/thread creep.
        Assert.True(managed1 - managed0 < 512_000,
            $"Managed memory grew {managed1 - managed0} bytes over 25 s of streaming");
        Assert.True(handles1 - handles0 < 60, $"Handle count grew {handles0} -> {handles1}");
        Assert.True(threads1 - threads0 <= 4, $"Thread count grew {threads0} -> {threads1}");
    }

    [Fact]
    public void Repeated_engine_lifecycle_leaks_nothing()
    {
        var audio = LtcGenerator.Generate(new LtcFrame(2, 0, 0, 0), 15, TimecodeRate.Ebu25, 25.0, 48000);

        void Cycle()
        {
            var cfg = new EngineConfig
            {
                InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt),
                LocalAddress = IPAddress.Loopback,
                TargetAddress = IPAddress.Loopback,
                Port = 65431, // no listener on purpose: exercises the ICMP-unreachable send path too
            };
            using var engine = new Engine(cfg, _ => { });
            engine.Start();
            Thread.Sleep(60);
            engine.Stop();
        }

        for (int i = 0; i < 10; i++) Cycle(); // warm-up
        ForceFullGc();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        int handles0 = proc.HandleCount;
        int threads0 = proc.Threads.Count;
        long managed0 = GC.GetTotalMemory(true);

        for (int i = 0; i < 40; i++) Cycle();

        ForceFullGc();
        proc.Refresh();
        Assert.True(proc.HandleCount - handles0 < 80,
            $"Handles grew {handles0} -> {proc.HandleCount} over 40 engine restarts");
        Assert.True(proc.Threads.Count - threads0 <= 5,
            $"Threads grew {threads0} -> {proc.Threads.Count} over 40 engine restarts");
        long managed1 = GC.GetTotalMemory(true);
        Assert.True(managed1 - managed0 < 1_500_000,
            $"Managed memory grew {managed1 - managed0} bytes over 40 engine restarts");
    }

    [Fact]
    public void Decoder_survives_hostile_input_and_recovers()
    {
        var dec = new LtcDecoder(48000);
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);

        // NaN / Infinity / huge / denormal / random garbage — real drivers do emit
        // garbage buffers on glitches, unplug races and exclusive-mode fights.
        var hostile = new List<float[]>
        {
            Enumerable.Repeat(float.NaN, 4800).ToArray(),
            Enumerable.Repeat(float.PositiveInfinity, 2400).ToArray(),
            Enumerable.Repeat(float.NegativeInfinity, 2400).ToArray(),
            Enumerable.Repeat(1e30f, 2400).ToArray(),
            Enumerable.Repeat(-1e30f, 2400).ToArray(),
            Enumerable.Repeat(1e-40f, 4800).ToArray(), // denormals
            new float[0],
        };
        var rng = new Random(7);
        var noise = new float[9600];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2e6 - 1e6);
        hostile.Add(noise);

        foreach (var block in hostile)
        {
            // Feed in odd chunk sizes; must never throw.
            for (int i = 0; i < block.Length; i += 331)
                dec.Process(block.AsSpan(i, Math.Min(331, block.Length - i)));
        }
        Assert.Empty(frames); // garbage must never decode into timecode

        // The decoder must recover on its own once a clean signal returns.
        var clean = LtcGenerator.Generate(new LtcFrame(3, 0, 0, 0), 25, TimecodeRate.Ebu25, 25.0, 48000);
        for (int i = 0; i < clean.Length; i += 480)
            dec.Process(clean.AsSpan(i, Math.Min(480, clean.Length - i)));

        Assert.True(frames.Count >= 18,
            $"Decoder only produced {frames.Count} frames after hostile input — it did not recover");
        Assert.True(dec.Locked);
        Assert.All(frames, f => Assert.Equal(3, (int)f.Hours));
    }

    [Fact]
    public void Decoder_crosses_midnight()
    {
        var start = new LtcFrame(23, 59, 59, 10);
        var audio = LtcGenerator.Generate(start, 40, TimecodeRate.Ebu25, 25.0, 48000);
        var dec = new LtcDecoder(48000);
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);
        dec.Process(audio);

        Assert.True(frames.Count >= 35);
        Assert.Contains(frames, f => f.ToString() == "23:59:59:24");
        Assert.Contains(frames, f => f.ToString() == "00:00:00:00");
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].TimeEquals(frames[i - 1].AddFrames(1, TimecodeRate.Ebu25)));
    }
}
