using System.Net;
using System.Net.Sockets;
using TimecodeBridge.ArtNet;
using TimecodeBridge.Core;
using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

public class DiscoveryTests
{
    [Fact]
    public void Poll_packet_matches_spec()
    {
        var p = ArtNetNode.BuildPoll();
        Assert.Equal(14, p.Length);
        Assert.Equal("Art-Net\0"u8.ToArray(), p[..8]);
        Assert.Equal(0x00, p[8]);
        Assert.Equal(0x20, p[9]);  // OpPoll 0x2000
        Assert.Equal(14, p[11]);   // protocol version
    }

    [Fact]
    public void Node_records_ArtPollReply_from_a_console()
    {
        const int port = 26460;
        using var node = new ArtNetNode(IPAddress.Loopback, IPAddress.Loopback, port);

        // A "console" at 10.0.0.42 announces itself.
        var reply = ArtNetNode.BuildReply(IPAddress.Parse("10.0.0.42"), new byte[6]);
        // Overwrite the names so we can prove they were parsed.
        Array.Clear(reply, 26, 18);
        "grandMA3"u8.CopyTo(reply.AsSpan(26));
        using var console = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        console.Send(reply, reply.Length, new IPEndPoint(IPAddress.Loopback, port));

        DiscoveredNode? found = null;
        for (int i = 0; i < 40 && found == null; i++)
        {
            Thread.Sleep(50);
            found = node.Snapshot().FirstOrDefault(n => n.ShortName == "grandMA3");
        }
        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse("10.0.0.42"), found!.Address);
        Assert.False(found.IsSelf);

        node.ClearDiscovered();
        Assert.Empty(node.Snapshot());
    }

    [Fact]
    public void Node_marks_its_own_reply_as_self()
    {
        const int port = 26461;
        using var node = new ArtNetNode(IPAddress.Loopback, IPAddress.Loopback, port);
        // Polling ourselves: our reply comes back to our own socket and must be flagged.
        node.SendPoll(IPAddress.Loopback);
        DiscoveredNode? self = null;
        for (int i = 0; i < 40 && self == null; i++)
        {
            Thread.Sleep(50);
            self = node.Snapshot().FirstOrDefault(n => n.IsSelf);
        }
        Assert.NotNull(self);
        Assert.Equal("Timecode Bridge", self!.ShortName);
    }
}

public class TroubleshooterTests
{
    static AppConfig Config(string target) => new()
    {
        TargetIp = target, LocalIp = "127.0.0.1", Channel = 1, Rate = "Auto", FreewheelFrames = 25,
    };

    static List<CheckResult> Collect(Func<Action<CheckResult>, Task> run)
    {
        var results = new List<CheckResult>();
        run(r => results.Add(r)).GetAwaiter().GetResult();
        return results;
    }

    [Fact]
    public void Reports_engine_failure_first_and_stops()
    {
        var ctx = new TroubleshootContext
        {
            Engine = null, EngineStatusIfMissing = "Invalid target IP — output paused until corrected",
            Config = Config("not-an-ip"),
        };
        var results = Collect(report => Troubleshooter.RunAsync(ctx, report));
        Assert.Single(results);
        Assert.Equal("Bridge engine", results[0].Title);
        Assert.Equal(CheckLevel.Fail, results[0].Level);
        Assert.Contains("Invalid target IP", results[0].Detail);
    }

    [Fact]
    public void Healthy_chain_passes_every_audio_and_output_check()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        // 6 s of LTC looped so the chain is genuinely live for the whole run.
        var audio = LtcGenerator.Generate(new LtcFrame(1, 0, 0, 0), 150, TimecodeRate.Ebu25, 25.0, 48000);
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(audio, ring, evt, loop: true),
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            DiscoveryPort = 26462,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();
        Thread.Sleep(1500); // lock up

        var ctx = new TroubleshootContext
        {
            Engine = engine, Config = Config("127.0.0.1"), RecentPeak = 0.8f, WatchdogArmed = false,
        };
        var results = Collect(report => Troubleshooter.RunAsync(ctx, report));
        engine.Stop();

        string Level(string title) => results.Single(r => r.Title == title).Level.ToString();
        Assert.Equal("Pass", Level("Bridge engine"));
        Assert.Equal("Pass", Level("Audio device"));
        Assert.Equal("Pass", Level("Audio stream"));
        Assert.Equal("Pass", Level("Signal level"));
        Assert.Equal("Pass", Level("LTC detected"));
        Assert.Equal("Pass", Level("Frame lock"));
        Assert.Equal("Pass", Level("Timecode output"));
        Assert.Equal("Pass", Level("Art-Net socket"));
        Assert.Equal("Pass", Level("Network interface"));
        Assert.Equal("Pass", Level("Console reachable")); // loopback answers ping
        // Discovery: the only node on this private test port is ourselves (filtered
        // as self), so the honest answer is "nobody answered".
        Assert.Contains(results, r => r.Title == "Art-Net devices");
        Assert.Contains(results, r => r.Title == "Protection" && r.Level == CheckLevel.Info);
    }

    [Fact]
    public void Silent_input_is_diagnosed_as_missing_signal_not_a_dead_device()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => new FakeAudioInput(new float[4800], ring, evt), // silence forever
            LocalAddress = IPAddress.Loopback,
            TargetAddress = IPAddress.Loopback,
            Port = port,
            EnableDiscovery = false,
        };
        using var engine = new Engine(cfg, _ => { });
        engine.Start();
        Thread.Sleep(800);

        var ctx = new TroubleshootContext { Engine = engine, Config = Config("127.0.0.1"), RecentPeak = 0f };
        var results = Collect(report => Troubleshooter.RunAsync(ctx, report));
        engine.Stop();

        Assert.Equal(CheckLevel.Pass, results.Single(r => r.Title == "Audio device").Level);
        Assert.Equal(CheckLevel.Pass, results.Single(r => r.Title == "Audio stream").Level); // samples ARE flowing
        var level = results.Single(r => r.Title == "Signal level");
        Assert.Equal(CheckLevel.Fail, level.Level);
        Assert.Contains("Silence", level.Detail);
        Assert.DoesNotContain("Dante", level.Detail); // vendor-specific advice was removed on request
        Assert.Equal(CheckLevel.Fail, results.Single(r => r.Title == "LTC detected").Level);
        Assert.Equal(CheckLevel.Fail, results.Single(r => r.Title == "Timecode output").Level);
        // Discovery disabled → the troubleshooter must say so rather than crash or lie.
        Assert.Equal(CheckLevel.Warn, results.Single(r => r.Title == "Art-Net devices").Level);
    }
}
