using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TimecodeBridge.ArtNet;
using TimecodeBridge.Ltc;

namespace TimecodeBridge.Core;

public enum CheckLevel { Pass, Warn, Fail, Info }

public sealed record CheckResult(string Title, CheckLevel Level, string Detail);

/// <summary>Everything the troubleshooter needs that lives outside the engine.</summary>
public sealed class TroubleshootContext
{
    public Engine? Engine { get; init; }
    public string EngineStatusIfMissing { get; init; } = "";
    public required AppConfig Config { get; init; }
    /// <summary>Peak |sample| on the LTC channel over the last couple of seconds (0..1).</summary>
    public float RecentPeak { get; init; }
    public bool WatchdogArmed { get; init; }
}

/// <summary>
/// Walks the signal chain end to end — Dante/DVS → audio device → LTC decode → Art-Net
/// → network → console — and reports one verdict per link, with the fix for the most
/// likely cause of each failure. Read-only apart from an ArtPoll and a ping.
/// </summary>
public static class Troubleshooter
{
    /// <summary>Runs all checks, reporting each result as it completes. Awaits resume on the
    /// caller's synchronisation context, so <paramref name="report"/> is UI-thread-safe
    /// when called from the UI.</summary>
    public static async Task RunAsync(TroubleshootContext ctx, Action<CheckResult> report, CancellationToken ct = default)
    {
        var engine = ctx.Engine;
        var cfg = ctx.Config;

        // 1. Engine ------------------------------------------------------------
        if (engine == null || !engine.IsRunning)
        {
            report(new("Bridge engine", CheckLevel.Fail,
                (engine?.Status ?? ctx.EngineStatusIfMissing) is { Length: > 0 } s
                    ? s + " — fix the setting it names, or press Refresh."
                    : "The engine is not running. Press Refresh to restart it."));
            return; // nothing downstream can be evaluated
        }
        report(new("Bridge engine", CheckLevel.Pass, "Running"));

        // 2. Audio device -----------------------------------------------------
        var s1 = engine.Snapshot();
        if (!s1.InputOpen)
        {
            string hint = s1.Status.Contains("Dante", StringComparison.OrdinalIgnoreCase)
                ? "Start Dante Virtual Soundcard (open DVS and press Start), then this will reconnect by itself."
                : "Check the interface is connected and powered, then press Refresh.";
            report(new("Audio device", CheckLevel.Fail, $"{s1.Status}. {hint}"));
            report(new("Audio stream", CheckLevel.Fail, "No device — nothing to measure."));
            ReportOutputChecks(engine, s1, report);
            await ReportNetworkChecksAsync(engine, cfg, report, ct);
            return;
        }
        report(new("Audio device", CheckLevel.Pass, s1.InputDescription));

        // 3. Samples flowing --------------------------------------------------
        await Task.Delay(600, ct);
        var s2 = engine.Snapshot();
        long got = s2.SamplesReceived - s1.SamplesReceived;
        long expected = (long)(s2.SampleRate * 0.6);
        if (got <= 0)
            report(new("Audio stream", CheckLevel.Fail,
                "Device is open but delivering no audio. In Dante: is DVS started and are its receive channels " +
                "subscribed in Dante Controller? In Windows: is the device enabled and not in exclusive use?"));
        else if (expected > 0 && got < expected / 4)
            report(new("Audio stream", CheckLevel.Warn,
                $"Audio arriving intermittently — {got:N0} samples in 0.6 s, expected ~{expected:N0}. Dropouts likely; " +
                "check CPU load, USB/Dante network health, or try the WASAPI driver."));
        else
            report(new("Audio stream", CheckLevel.Pass,
                $"{s2.SampleRate / 1000.0:0.#} kHz, {got:N0} samples in 0.6 s" +
                (s2.Overruns > 0 ? $" — {s2.Overruns:N0} samples dropped since start (buffer overruns)" : "")));

        // 4. Level --------------------------------------------------------------
        float peak = ctx.RecentPeak;
        double db = peak > 0 ? 20 * Math.Log10(peak) : -120;
        bool silent = peak < 0.001f;
        if (got <= 0)
            report(new("Signal level", CheckLevel.Info, "Not measurable — no audio stream."));
        else if (silent)
            report(new("Signal level", CheckLevel.Fail,
                $"Silence on channel {cfg.Channel}. Either nothing is playing timecode right now (Playback stopped?) " +
                "or the Dante route is missing — in Dante Controller, subscribe the LTC transmitter to this DVS receive channel, " +
                "and check the Channel setting matches (1 = left, 2 = right of the pair)."));
        else if (peak < 0.02f)
            report(new("Signal level", CheckLevel.Warn,
                $"Very low level ({db:0} dBFS). The decoder copes down to about −60 dBFS, but this leaves little margin — raise the LTC send level."));
        else if (peak > 0.98f)
            report(new("Signal level", CheckLevel.Warn,
                "Clipping (0 dBFS). LTC decodes perfectly at −12 dBFS — lower the send level to avoid distortion of the transitions."));
        else
            report(new("Signal level", CheckLevel.Pass, $"{db:0.0} dBFS peak"));

        // 5. LTC detection ----------------------------------------------------
        if (s2.SignalPresent)
            report(new("LTC detected", CheckLevel.Pass, "Sync words arriving on the selected channel"));
        else if (silent || got <= 0)
            report(new("LTC detected", CheckLevel.Fail, "No LTC — there is no audio to decode (see above)."));
        else
            report(new("LTC detected", CheckLevel.Fail,
                "Audio is present but it is not LTC. The channel probably carries music, click or a guide track — " +
                "select the channel Playback sends timecode on (check its Outputs routing), or the wrong DVS receive pair is patched."));

        // 6. Lock / rate ------------------------------------------------------
        if (s2.Locked)
        {
            string detail = $"Locked — {s2.MeasuredFps:0.00} fps, sending as {RateName(s2.DetectedRate)}";
            var overrideRate = ParseRateOverride(cfg.Rate);
            if (overrideRate != null && LtcFrame.NominalFps(overrideRate.Value) != LtcFrame.NominalFps(s2.DetectedRate))
                report(new("Frame lock", CheckLevel.Warn,
                    detail + $". Rate is overridden to {RateName(overrideRate.Value)} which does not match the incoming " +
                    $"{RateName(s2.DetectedRate)} — MA3 will show the wrong frame rate. Set Rate to Auto."));
            else
                report(new("Frame lock", CheckLevel.Pass, detail));
        }
        else if (s2.SignalPresent)
            report(new("Frame lock", CheckLevel.Warn,
                "LTC seen but not locked. Lock needs three clean consecutive frames — a varispeed, scrubbing or very " +
                "distorted source prevents it. Check the level above and that Playback is running at a steady rate."));
        else
            report(new("Frame lock", CheckLevel.Fail, "Not locked (no LTC)."));

        ReportOutputChecks(engine, s2, report);
        await ReportNetworkChecksAsync(engine, cfg, report, ct);

        // Protection summary ---------------------------------------------------
        report(new("Protection", CheckLevel.Info,
            $"Freewheel {cfg.FreewheelFrames} frames · Generator fallback {(cfg.GeneratorFallback ? "on" : "off")} · " +
            $"Crash watchdog {(ctx.WatchdogArmed ? "armed" : "off")} · Settings {(cfg.Locked ? "locked" : "unlocked")}"));
    }

    static void ReportOutputChecks(Engine engine, EngineDiagnostics s, Action<CheckResult> report)
    {
        bool tx = (s.Flags & Engine.FlagTx) != 0;
        bool fw = (s.Flags & Engine.FlagFreewheel) != 0;
        bool gen = (s.Flags & Engine.FlagGenerator) != 0;

        if (gen)
            report(new("Timecode output", CheckLevel.Warn,
                "Internal generator is running — the console is receiving PC-clock timecode, NOT the incoming LTC. " +
                "Output will snap back to LTC the moment it returns."));
        else if (fw)
            report(new("Timecode output", CheckLevel.Warn,
                "Freewheeling — LTC just dropped and the bridge is coasting on the last known rate."));
        else if (tx)
            report(new("Timecode output", CheckLevel.Pass, $"Transmitting live LTC — {s.PacketsSent:N0} packets sent"));
        else
            report(new("Timecode output", CheckLevel.Fail,
                s.PacketsSent > 0 ? $"Nothing being sent now ({s.PacketsSent:N0} packets sent earlier)." : "Nothing has been sent yet."));

        var cfg = engine.Config;
        string route = $"{Describe(cfg.LocalAddress)} → {cfg.TargetAddress}:{cfg.Port}";
        if (s.SendErrors > 0)
            report(new("Art-Net socket", CheckLevel.Fail,
                $"{s.SendErrors:N0} send errors on {route}. {s.Status}. Usually the selected interface went down or has no IP — " +
                "check the cable/adapter, then press Refresh."));
        else
            report(new("Art-Net socket", CheckLevel.Pass, route));

        if (s.BackupConfigured)
            report(new("Backup output", s.BackupOpen ? CheckLevel.Pass : CheckLevel.Fail,
                s.BackupOpen ? $"{Describe(cfg.LocalAddress2 ?? cfg.LocalAddress)} → {cfg.TargetAddress2}:{cfg.Port}"
                             : $"Backup target {cfg.TargetAddress2} could not be opened — check the backup interface."));
    }

    static async Task ReportNetworkChecksAsync(Engine engine, AppConfig cfg, Action<CheckResult> report, CancellationToken ct)
    {
        var ecfg = engine.Config;
        var target = ecfg.TargetAddress;

        // Interface -------------------------------------------------------------
        IPAddress? localIp = ecfg.LocalAddress;
        UnicastIPAddressInformation? localInfo = null;
        NetworkInterface? localNic = null;
        if (localIp != null && !localIp.Equals(IPAddress.Any))
        {
            (localNic, localInfo) = FindNic(localIp);
            if (localNic == null)
            {
                report(new("Network interface", CheckLevel.Fail,
                    $"The selected interface ({localIp}) no longer exists or is down — cable unplugged, adapter disabled, " +
                    "or its IP changed. Open Settings and pick the interface again."));
            }
            else
                report(new("Network interface", CheckLevel.Pass, $"{localIp} — {localNic.Name}{SpeedOf(localNic)}"));
        }
        else
        {
            localIp = RouteSource(target);
            if (localIp == null)
                report(new("Network interface", CheckLevel.Fail,
                    $"Windows has no route to {target} — no connected adapter can reach it. Plug into the lighting network " +
                    "or select the right interface in Settings."));
            else
            {
                (localNic, localInfo) = FindNic(localIp);
                report(new("Network interface", CheckLevel.Info,
                    $"Any interface — Windows is sending via {localIp}{(localNic != null ? " (" + localNic.Name + ")" : "")}. " +
                    "On a multi-network PC, selecting the lighting-network interface explicitly is safer."));
            }
        }

        // Subnet sanity for unicast targets -----------------------------------
        bool broadcast = IsBroadcast(target, localInfo);
        if (!broadcast && localIp != null && localInfo?.IPv4Mask != null)
        {
            if (!SameSubnet(localIp, target, localInfo.IPv4Mask))
                report(new("Console address", CheckLevel.Warn,
                    $"{target} is not on {localIp}'s subnet (mask {localInfo.IPv4Mask}). Art-Net normally stays on one subnet — " +
                    "either the console IP is mistyped, or the PC is on the wrong network. Typical MA3 addressing is 2.x.x.x/8 or 10.x.x.x/8."));
            else
                report(new("Console address", CheckLevel.Pass, $"{target} is on the same subnet as {localIp}"));
        }

        // Reachability ----------------------------------------------------------
        if (broadcast)
            report(new("Console reachable", CheckLevel.Info,
                $"Broadcast target ({target}) — cannot ping; relying on Art-Net discovery below."));
        else
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target, 1500);
                if (reply.Status == IPStatus.Success)
                    report(new("Console reachable", CheckLevel.Pass, $"{target} answered ping in {reply.RoundtripTime} ms"));
                else
                    report(new("Console reachable", CheckLevel.Warn,
                        $"No ping reply from {target} ({reply.Status}). grandMA3 normally answers ping — check the console's IP " +
                        "(Menu → Network), that both are on the same switch/VLAN, and the cabling."));
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
            {
                report(new("Console reachable", CheckLevel.Warn, $"Ping to {target} failed: {ex.InnerException?.Message ?? ex.Message}"));
            }
        }

        // Art-Net discovery -----------------------------------------------------
        if (!engine.PollArtNet())
        {
            report(new("Art-Net devices", CheckLevel.Warn,
                "The discovery listener is unavailable (another program holds UDP 6454 exclusively), so consoles can't be polled. " +
                "Timecode sending itself is unaffected."));
            return;
        }
        await Task.Delay(1000, ct);
        engine.PollArtNet(); // second poll catches anything that missed the first
        await Task.Delay(1200, ct);

        var nodes = engine.DiscoveredNodes.Where(n => !n.IsSelf).ToList();
        var hit = nodes.FirstOrDefault(n => n.Address.Equals(target));
        var hit2 = ecfg.TargetAddress2 != null ? nodes.FirstOrDefault(n => n.Address.Equals(ecfg.TargetAddress2)) : null;
        string list = nodes.Count == 0 ? "" : string.Join(", ", nodes.Take(6).Select(n => $"{n.ShortName} ({n.Address})"))
                                              + (nodes.Count > 6 ? $" +{nodes.Count - 6} more" : "");

        if (!broadcast && hit != null)
            report(new("Art-Net devices", CheckLevel.Pass,
                $"Console answered ArtPoll: {hit.ShortName}{(hit.LongName.Length > 0 && hit.LongName != hit.ShortName ? " — " + hit.LongName : "")} at {hit.Address}" +
                (hit2 != null ? $"; backup {hit2.ShortName} at {hit2.Address}" : "")));
        else if (!broadcast && nodes.Count > 0)
            report(new("Art-Net devices", CheckLevel.Warn,
                $"{target} did not answer ArtPoll, but these did: {list}. Check the console's IP, and on grandMA3 that " +
                "Menu → Network → Art-Net has a node enabled on the interface facing this PC."));
        else if (broadcast && nodes.Count > 0)
            report(new("Art-Net devices", CheckLevel.Pass, $"Answering ArtPoll: {list}"));
        else
            report(new("Art-Net devices", CheckLevel.Fail,
                "No Art-Net device answered. On grandMA3: Menu → Network → Art-Net — add/enable a node with Input on this " +
                "network, and Menu → In & Out → Timecode — set the slot source to Art-Net. If the console is definitely " +
                "configured, Windows Firewall may be blocking inbound UDP 6454 replies to this PC."));
    }

    // --------------------------------------------------------------- helpers

    static (NetworkInterface?, UnicastIPAddressInformation?) FindNic(IPAddress ip)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.Equals(ip)) return (nic, ua);
            }
        }
        catch { }
        return (null, null);
    }

    static string SpeedOf(NetworkInterface nic)
    {
        try
        {
            long bps = nic.Speed;
            return bps > 0 ? $", {bps / 1_000_000} Mbit/s" : "";
        }
        catch { return ""; }
    }

    static IPAddress? RouteSource(IPAddress target)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.EnableBroadcast = true;
            probe.Connect(new IPEndPoint(target, ArtNetTimecodeSender.ArtNetPort));
            return probe.LocalEndPoint is IPEndPoint ep && !ep.Address.Equals(IPAddress.Any) ? ep.Address : null;
        }
        catch { return null; }
    }

    static bool IsBroadcast(IPAddress target, UnicastIPAddressInformation? local)
    {
        if (target.Equals(IPAddress.Broadcast)) return true;
        var t = target.GetAddressBytes();
        if (local?.IPv4Mask != null)
        {
            var a = local.Address.GetAddressBytes();
            var m = local.IPv4Mask.GetAddressBytes();
            bool isSubnetBroadcast = true;
            for (int i = 0; i < 4; i++)
                if (t[i] != ((a[i] & m[i]) | (byte)~m[i])) { isSubnetBroadcast = false; break; }
            if (isSubnetBroadcast) return true;
        }
        return t[3] == 255; // conventional /24 directed broadcast
    }

    static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    static string Describe(IPAddress? local) => local == null || local.Equals(IPAddress.Any) ? "any interface" : local.ToString();

    static TimecodeRate? ParseRateOverride(string rate) => rate switch
    {
        "24" => TimecodeRate.Film24,
        "25" => TimecodeRate.Ebu25,
        "29.97 DF" => TimecodeRate.Df2997,
        "30" => TimecodeRate.Smpte30,
        _ => null,
    };

    static string RateName(TimecodeRate rate) => rate switch
    {
        TimecodeRate.Film24 => "24 fps (Film)",
        TimecodeRate.Ebu25 => "25 fps (EBU)",
        TimecodeRate.Df2997 => "29.97 fps drop-frame",
        _ => "30 fps (SMPTE)",
    };
}
