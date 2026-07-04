using System.Net;
using System.Net.Sockets;
using TimecodeBridge.Ltc;

namespace TimecodeBridge.ArtNet;

/// <summary>
/// Sends Art-Net ArtTimeCode packets (OpCode 0x9700) over UDP.
/// The socket is bound to a chosen local interface and pre-connected to the target,
/// so each send is a single allocation-free syscall.
/// </summary>
public sealed class ArtNetTimecodeSender : IDisposable
{
    public const int ArtNetPort = 6454;
    public const int PacketLength = 19;

    readonly Socket _socket;
    readonly byte[] _packet;

    public ArtNetTimecodeSender(IPAddress? localAddress, IPAddress target, int port)
    {
        _packet = CreateTemplate();
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
            SendBufferSize = 1 << 16,
        };
        try
        {
            _socket.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, 0));
            _socket.Connect(new IPEndPoint(target, port));

            // On Windows a connected UDP socket surfaces ICMP port-unreachable as a
            // ConnectionReset on subsequent sends. A console that is rebooting or
            // briefly offline must not turn into a send-error storm — disable it.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); }
            catch (SocketException) { /* not supported on this stack — sends stay guarded anyway */ }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    static byte[] CreateTemplate()
    {
        var p = new byte[PacketLength];
        p[0] = (byte)'A'; p[1] = (byte)'r'; p[2] = (byte)'t'; p[3] = (byte)'-';
        p[4] = (byte)'N'; p[5] = (byte)'e'; p[6] = (byte)'t'; p[7] = 0;
        p[8] = 0x00; p[9] = 0x97;  // OpCode 0x9700 (ArtTimeCode), little-endian
        p[10] = 0; p[11] = 14;     // Protocol version 14
        p[12] = 0; p[13] = 0;      // Filler
        // p[14..18] = frames, seconds, minutes, hours, type — filled per send
        return p;
    }

    /// <summary>Builds a standalone packet (used by tests and diagnostics).</summary>
    public static byte[] BuildPacket(in LtcFrame f, TimecodeRate rate)
    {
        var p = CreateTemplate();
        p[14] = f.Frames;
        p[15] = f.Seconds;
        p[16] = f.Minutes;
        p[17] = f.Hours;
        p[18] = (byte)rate;
        return p;
    }

    /// <summary>Send one ArtTimeCode packet. Allocation-free; may throw SocketException.</summary>
    public void Send(in LtcFrame f, TimecodeRate rate)
    {
        _packet[14] = f.Frames;
        _packet[15] = f.Seconds;
        _packet[16] = f.Minutes;
        _packet[17] = f.Hours;
        _packet[18] = (byte)rate;
        _socket.Send(_packet);
    }

    public void Dispose() => _socket.Dispose();
}
