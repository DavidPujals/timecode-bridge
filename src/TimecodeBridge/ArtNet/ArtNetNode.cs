using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TimecodeBridge.ArtNet;

/// <summary>
/// Minimal Art-Net node discovery responder: listens on the Art-Net port and answers
/// ArtPoll (0x2000) with ArtPollReply (0x2100), so consoles and network scanners can
/// see the bridge by name. Binds with SO_REUSEADDR to coexist with other Art-Net
/// software on the same machine.
/// </summary>
public sealed class ArtNetNode : IDisposable
{
    const int OpPoll = 0x2000;

    readonly Socket _socket;
    readonly byte[] _reply;
    readonly Thread _thread;
    volatile bool _run = true;

    public ArtNetNode(IPAddress? localAddress, IPAddress target, int port = ArtNetTimecodeSender.ArtNetPort)
    {
        var ownIp = ResolveLocalIp(localAddress, target);
        _reply = BuildReply(ownIp, GetMacFor(ownIp));

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;
        try
        {
            // Replies to pollers that vanished must not error the receive path.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); }
            catch (SocketException) { }
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "artnet-node" };
        _thread.Start();
    }

    static IPAddress ResolveLocalIp(IPAddress? localAddress, IPAddress target)
    {
        if (localAddress != null && !localAddress.Equals(IPAddress.Any))
            return localAddress;
        try
        {
            // The OS picks the interface that routes towards the target.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.EnableBroadcast = true;
            probe.Connect(new IPEndPoint(target, ArtNetTimecodeSender.ArtNetPort));
            if (probe.LocalEndPoint is IPEndPoint ep && !ep.Address.Equals(IPAddress.Any))
                return ep.Address;
        }
        catch { /* fall through */ }
        return IPAddress.Any;
    }

    static byte[] GetMacFor(IPAddress ip)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.Equals(ip))
                    {
                        var mac = nic.GetPhysicalAddress().GetAddressBytes();
                        if (mac.Length == 6) return mac;
                    }
                }
            }
        }
        catch { /* MAC is informational only */ }
        return new byte[6];
    }

    /// <summary>ArtPollReply per the Art-Net 4 spec (239 bytes). Public for tests.</summary>
    public static byte[] BuildReply(IPAddress ip, byte[] mac)
    {
        var p = new byte[239];
        WriteAscii(p, 0, 8, "Art-Net");           // includes the required NUL
        p[8] = 0x00; p[9] = 0x21;                  // OpPollReply, little-endian
        ip.GetAddressBytes().CopyTo(p, 10);        // node IP
        p[14] = 0x36; p[15] = 0x19;                // port 6454, little-endian
        p[16] = 1; p[17] = 0;                      // firmware version
        p[18] = 0; p[19] = 0;                      // NetSwitch, SubSwitch
        p[20] = 0x00; p[21] = 0xFF;                // OEM: unknown
        p[22] = 0;                                 // Ubea
        p[23] = 0x00;                              // Status1
        p[24] = 0; p[25] = 0;                      // ESTA manufacturer
        WriteAscii(p, 26, 18, "Timecode Bridge");
        WriteAscii(p, 44, 64, "Timecode Bridge - SMPTE LTC to Art-Net timecode");
        WriteAscii(p, 108, 64, "#0001 [0000] LTC bridge online");
        // 172..199: no DMX ports — all zero
        p[200] = 0x00;                             // Style: StNode
        mac.CopyTo(p, 201);
        ip.GetAddressBytes().CopyTo(p, 207);       // BindIp
        p[211] = 1;                                // BindIndex
        p[212] = 0x00;                             // Status2
        return p;
    }

    static void WriteAscii(byte[] buf, int offset, int length, string text)
    {
        for (int i = 0; i < length - 1 && i < text.Length; i++)
            buf[offset + i] = text[i] < 128 ? (byte)text[i] : (byte)'?';
        // remainder stays zero (spec requires NUL termination/padding)
    }

    void ReceiveLoop()
    {
        var buf = new byte[1500];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_run)
        {
            try
            {
                int n = _socket.ReceiveFrom(buf, ref remote);
                if (n < 10) continue;
                if (buf[0] != 'A' || buf[1] != 'r' || buf[2] != 't' || buf[3] != '-' ||
                    buf[4] != 'N' || buf[5] != 'e' || buf[6] != 't' || buf[7] != 0)
                    continue;
                int opCode = buf[8] | buf[9] << 8;
                if (opCode == OpPoll)
                    _socket.SendTo(_reply, remote);
            }
            catch (SocketException)
            {
                if (!_run) return; // socket closed by Dispose
                // Transient receive error — keep listening, but never busy-spin if
                // the failure is persistent (e.g. NIC reset).
                Thread.Sleep(100);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _run = false;
        _socket.Dispose(); // unblocks ReceiveFrom
        _thread.Join(1000);
    }
}
