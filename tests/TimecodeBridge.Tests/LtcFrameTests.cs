using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

public class LtcFrameTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 59, 29, 1799)]
    [InlineData(0, 1, 0, 2, 1800)]      // first label after the minute drop
    [InlineData(0, 10, 0, 0, 17982)]    // tenth minute keeps all frames
    [InlineData(1, 0, 0, 0, 107892)]
    [InlineData(23, 59, 59, 29, 2589407)]
    public void Drop_frame_number_known_values(int h, int m, int s, int f, long expected)
    {
        var frame = new LtcFrame(h, m, s, f, dropFrame: true);
        Assert.Equal(expected, frame.ToFrameNumber(TimecodeRate.Df2997));
        var back = LtcFrame.FromFrameNumber(expected, TimecodeRate.Df2997);
        Assert.True(frame.TimeEquals(back), $"{frame} != {back}");
    }

    [Theory]
    [InlineData(TimecodeRate.Film24)]
    [InlineData(TimecodeRate.Ebu25)]
    [InlineData(TimecodeRate.Df2997)]
    [InlineData(TimecodeRate.Smpte30)]
    public void Frame_number_roundtrip_full_day(TimecodeRate rate)
    {
        long perDay = new LtcFrame(23, 59, 59, LtcFrame.NominalFps(rate) - 1).ToFrameNumber(rate) + 1;
        for (long n = 0; n < perDay; n += 997) // prime stride covers all field combinations
        {
            var f = LtcFrame.FromFrameNumber(n, rate);
            Assert.Equal(n, f.ToFrameNumber(rate));
        }
        // wrap-around and negatives
        Assert.Equal(0, LtcFrame.FromFrameNumber(perDay, rate).ToFrameNumber(rate));
        Assert.Equal(perDay - 1, LtcFrame.FromFrameNumber(-1, rate).ToFrameNumber(rate));
    }

    [Fact]
    public void Drop_frame_increment_skips_dropped_labels()
    {
        var f = new LtcFrame(0, 0, 59, 29, dropFrame: true);
        var next = f.AddFrames(1, TimecodeRate.Df2997);
        Assert.Equal("00:01:00;02", next.ToString());

        var tenth = new LtcFrame(0, 9, 59, 29, dropFrame: true).AddFrames(1, TimecodeRate.Df2997);
        Assert.Equal("00:10:00;00", tenth.ToString());
    }

    [Fact]
    public void Non_drop_increment_rolls_over_midnight()
    {
        var f = new LtcFrame(23, 59, 59, 24);
        Assert.Equal("00:00:00:00", f.AddFrames(1, TimecodeRate.Ebu25).ToString());
    }

    [Fact]
    public void Offset_can_be_negative()
    {
        var f = new LtcFrame(0, 0, 0, 0);
        Assert.Equal("23:59:59:29", f.AddFrames(-1, TimecodeRate.Smpte30).ToString());
    }
}
