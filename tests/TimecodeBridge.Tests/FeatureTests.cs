using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TimecodeBridge.ArtNet;
using TimecodeBridge.Core;
using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

public class ArtNetNodeTests
{
    [Fact]
    public void PollReply_packet_matches_spec()
    {
        var ip = IPAddress.Parse("10.0.0.5");
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var p = ArtNetNode.BuildReply(ip, mac);

        Assert.Equal(239, p.Length);
        Assert.Equal("Art-Net\0"u8.ToArray(), p[..8]);
        Assert.Equal(0x00, p[8]);          // OpPollReply lo
        Assert.Equal(0x21, p[9]);          // OpPollReply hi (0x2100)
        Assert.Equal(new byte[] { 10, 0, 0, 5 }, p[10..14]);
        Assert.Equal(0x36, p[14]);         // port 6454 little-endian
        Assert.Equal(0x19, p[15]);
        Assert.StartsWith("Timecode Bridge", System.Text.Encoding.ASCII.GetString(p, 26, 18).TrimEnd('\0'));
        Assert.Equal(0, p[43]);            // short name NUL-terminated
        Assert.Equal(0x00, p[200]);        // Style: StNode
        Assert.Equal(mac, p[201..207]);
        Assert.Equal(new byte[] { 10, 0, 0, 5 }, p[207..211]); // BindIp
        Assert.Equal(1, p[211]);           // BindIndex
    }

    [Fact]
    public void Node_answers_ArtPoll()
    {
        const int port = 26454;
        using var node = new ArtNetNode(IPAddress.Loopback, IPAddress.Loopback, port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;

        var poll = new byte[14];
        "Art-Net\0"u8.CopyTo(poll);
        poll[8] = 0x00; poll[9] = 0x20; // OpPoll
        poll[10] = 0; poll[11] = 14;    // protocol version
        client.Send(poll, poll.Length, new IPEndPoint(IPAddress.Loopback, port));

        IPEndPoint? remote = null;
        var reply = client.Receive(ref remote);
        Assert.True(reply.Length >= 207);
        Assert.Equal(0x00, reply[8]);
        Assert.Equal(0x21, reply[9]);
    }
}

public class FeatureTests
{
    static UdpClient Listener(out int port)
    {
        var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        client.Client.ReceiveTimeout = 1000;
        return client;
    }

    static List<byte[]> Drain(UdpClient client, int maxMs)
    {
        var packets = new List<byte[]>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            try
            {
                IPEndPoint? r = null;
                packets.Add(client.Receive(ref r));
            }
            catch (SocketException) { /* timeout tick */ }
        }
        return packets;
    }

    [Fact]
    public void Dual_output_sends_identical_frames_to_both_targets()
    {
        using var primary = Listener(out int portA);
        using var backup = Listener(out int portB);

        var audio = LtcGenerator.Generate(new LtcFrame(4, 0, 0, 0), 40, TimecodeRate.Ebu25, 25.0, 48000);
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = portA,
            TargetAddress2 = IPAddress.Loopback,
            LocalAddress2 = IPAddress.Loopback,
            BackupPort = portB,
            EnableDiscovery = false,
            FreewheelFrames = 0,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();

        var a = Drain(primary, 2500);
        var b = Drain(backup, 500);
        engine.Stop();

        Assert.True(a.Count >= 25, $"Primary got only {a.Count} packets");
        Assert.True(b.Count >= 25, $"Backup got only {b.Count} packets");
        // Same stream on both outputs: compare the first common frames.
        var setA = a.Select(p => (p[17], p[16], p[15], p[14])).ToHashSet();
        int matched = b.Count(p => setA.Contains((p[17], p[16], p[15], p[14])));
        Assert.True(matched >= b.Count - 2, $"Backup stream diverged: {matched}/{b.Count} matched");
    }

    [Fact]
    public void Generator_takes_over_after_freewheel_and_keeps_counting()
    {
        using var listener = Listener(out int port);

        // 40 frames (1.6 s) of LTC, then silence forever. Freewheel of 5 would
        // normally end output ~1.8 s in; the generator must keep it running.
        var audio = LtcGenerator.Generate(new LtcFrame(6, 0, 0, 0), 40, TimecodeRate.Ebu25, 25.0, 48000);
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            FreewheelFrames = 5,
            GeneratorFallback = true,
            EnableDiscovery = false,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();

        var packets = Drain(listener, 5000);
        Engine.UnpackState(engine.StateBits, out _, out _, out int flags);
        engine.Stop();

        // ~38 live + 5 freewheel + ~3.4 s of generator ≈ 128 total; assert well past
        // the point where a non-generator run would have stopped (~43).
        Assert.True(packets.Count >= 90, $"Only {packets.Count} packets — generator did not take over");
        Assert.Equal(Engine.FlagGenerator, flags & Engine.FlagGenerator);

        // The whole stream — live, freewheel, generator — must be gapless.
        LtcFrame? prev = null;
        foreach (var p in packets)
        {
            var f = new LtcFrame(p[17], p[16], p[15], p[14]);
            if (prev is { } pf)
                Assert.True(f.TimeEquals(pf.AddFrames(1, TimecodeRate.Ebu25)),
                    $"Gap in handover: {pf} -> {f}");
            prev = f;
        }
    }

    [Fact]
    public void Generator_cold_starts_from_zero_when_no_ltc_exists()
    {
        using var listener = Listener(out int port);

        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(new float[4800], ring, evt), // silence only
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            GeneratorFallback = true,
            RateOverride = TimecodeRate.Ebu25,
            EnableDiscovery = false,
        };
        using var engine = new Engine(cfg, _ => { });
        var started = Stopwatch.StartNew();
        engine.Start();

        listener.Client.ReceiveTimeout = 8000;
        IPEndPoint? remote = null;
        var first = listener.Receive(ref remote);
        long firstMs = started.ElapsedMilliseconds;
        var rest = Drain(listener, 1000);
        engine.Stop();

        // Starts after the 3 s no-LTC grace, from 00:00:00:00, and keeps counting.
        Assert.InRange(firstMs, 2500, 7000);
        Assert.Equal(0, first[17]);
        Assert.Equal(0, first[16]);
        Assert.Equal(0, first[15]);
        Assert.Equal(0, first[14]);
        Assert.Equal(1, first[18]); // EBU 25
        Assert.True(rest.Count >= 20, $"Generator produced only {rest.Count} follow-on frames");
        var second = rest[0];
        Assert.Equal(1, second[14]); // 00:00:00:01
    }

    [Fact]
    public void Generator_never_runs_when_disabled()
    {
        using var listener = Listener(out int port);
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(new float[4800], ring, evt),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            GeneratorFallback = false,
            EnableDiscovery = false,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();
        var packets = Drain(listener, 4500);
        engine.Stop();
        Assert.Empty(packets);
    }
}
