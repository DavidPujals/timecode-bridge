using System.Net;
using System.Net.Sockets;
using TimecodeBridge.ArtNet;
using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

public class ArtNetTests
{
    [Fact]
    public void Packet_matches_ArtTimeCode_spec()
    {
        var frame = new LtcFrame(10, 20, 30, 15);
        var p = ArtNetTimecodeSender.BuildPacket(frame, TimecodeRate.Ebu25);

        Assert.Equal(19, p.Length);
        Assert.Equal("Art-Net\0"u8.ToArray(), p[..8]);
        Assert.Equal(0x00, p[8]);  // OpCode lo
        Assert.Equal(0x97, p[9]);  // OpCode hi (0x9700 ArtTimeCode)
        Assert.Equal(0, p[10]);    // ProtVerHi
        Assert.Equal(14, p[11]);   // ProtVerLo
        Assert.Equal(0, p[12]);
        Assert.Equal(0, p[13]);
        Assert.Equal(15, p[14]);   // frames
        Assert.Equal(30, p[15]);   // seconds
        Assert.Equal(20, p[16]);   // minutes
        Assert.Equal(10, p[17]);   // hours
        Assert.Equal(1, p[18]);    // type = EBU 25
    }

    [Theory]
    [InlineData(TimecodeRate.Film24, 0)]
    [InlineData(TimecodeRate.Ebu25, 1)]
    [InlineData(TimecodeRate.Df2997, 2)]
    [InlineData(TimecodeRate.Smpte30, 3)]
    public void Rate_maps_to_artnet_type(TimecodeRate rate, byte expected)
    {
        var p = ArtNetTimecodeSender.BuildPacket(default, rate);
        Assert.Equal(expected, p[18]);
    }

    [Fact]
    public void Sends_over_udp_loopback()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        listener.Client.ReceiveTimeout = 3000;

        using var sender = new ArtNetTimecodeSender(IPAddress.Loopback, IPAddress.Loopback, port);
        var frame = new LtcFrame(1, 2, 3, 4);
        sender.Send(frame, TimecodeRate.Smpte30);

        IPEndPoint? remote = null;
        var received = listener.Receive(ref remote);
        Assert.Equal(ArtNetTimecodeSender.BuildPacket(frame, TimecodeRate.Smpte30), received);
    }
}
