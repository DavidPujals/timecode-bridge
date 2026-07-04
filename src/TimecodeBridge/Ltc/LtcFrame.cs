namespace TimecodeBridge.Ltc;

/// <summary>
/// Timecode rate. Enum values match the Art-Net ArtTimeCode "Type" field exactly.
/// </summary>
public enum TimecodeRate : byte
{
    Film24 = 0,
    Ebu25 = 1,
    Df2997 = 2,
    Smpte30 = 3,
}

/// <summary>
/// A single timecode value. Immutable 5-byte struct — safe to copy across threads.
/// </summary>
public readonly struct LtcFrame : IEquatable<LtcFrame>
{
    public readonly byte Hours;
    public readonly byte Minutes;
    public readonly byte Seconds;
    public readonly byte Frames;
    public readonly bool DropFrame;

    public LtcFrame(int hours, int minutes, int seconds, int frames, bool dropFrame = false)
    {
        Hours = (byte)hours;
        Minutes = (byte)minutes;
        Seconds = (byte)seconds;
        Frames = (byte)frames;
        DropFrame = dropFrame;
    }

    public static int NominalFps(TimecodeRate rate) => rate switch
    {
        TimecodeRate.Film24 => 24,
        TimecodeRate.Ebu25 => 25,
        _ => 30,
    };

    /// <summary>Absolute frame count since 00:00:00:00, honouring drop-frame numbering.</summary>
    public long ToFrameNumber(TimecodeRate rate)
    {
        int fps = NominalFps(rate);
        long total = ((Hours * 60L + Minutes) * 60 + Seconds) * fps + Frames;
        if (rate == TimecodeRate.Df2997)
        {
            long totalMinutes = Hours * 60L + Minutes;
            total -= 2 * (totalMinutes - totalMinutes / 10);
        }
        return total;
    }

    /// <summary>Inverse of <see cref="ToFrameNumber"/>. Wraps at 24 hours, accepts negatives.</summary>
    public static LtcFrame FromFrameNumber(long frameNumber, TimecodeRate rate)
    {
        int fps = NominalFps(rate);
        bool df = rate == TimecodeRate.Df2997;
        long perDay = 24L * 3600 * fps - (df ? 2 * (24 * 60 - 24 * 6) : 0);
        frameNumber %= perDay;
        if (frameNumber < 0) frameNumber += perDay;

        if (!df)
        {
            int fr = (int)(frameNumber % fps);
            long secs = frameNumber / fps;
            return new LtcFrame((int)(secs / 3600), (int)(secs / 60 % 60), (int)(secs % 60), fr);
        }

        // Drop-frame: 17982 true frames per 10-minute block; the first minute of each
        // block keeps all 1800 labels, the other nine minutes have 1798 (frames 2..29).
        const int per10Min = 17982;
        const int perMin = 1798;
        long ten = frameNumber / per10Min;
        int rem = (int)(frameNumber % per10Min);
        int minutes, idx;
        if (rem < 1800)
        {
            minutes = (int)(ten * 10);
            idx = rem;
        }
        else
        {
            rem -= 1800;
            minutes = (int)(ten * 10) + 1 + rem / perMin;
            idx = rem % perMin + 2;
        }
        return new LtcFrame(minutes / 60, minutes % 60, idx / 30, idx % 30, dropFrame: true);
    }

    public LtcFrame AddFrames(long n, TimecodeRate rate) => FromFrameNumber(ToFrameNumber(rate) + n, rate);

    /// <summary>Compares the time fields only, ignoring the drop-frame flag.</summary>
    public bool TimeEquals(in LtcFrame other) =>
        Hours == other.Hours && Minutes == other.Minutes && Seconds == other.Seconds && Frames == other.Frames;

    public bool Equals(LtcFrame other) => TimeEquals(other) && DropFrame == other.DropFrame;
    public override bool Equals(object? obj) => obj is LtcFrame f && Equals(f);
    public override int GetHashCode() => Hours << 24 | Minutes << 16 | Seconds << 8 | Frames;

    public override string ToString() =>
        $"{Hours:00}:{Minutes:00}:{Seconds:00}{(DropFrame ? ';' : ':')}{Frames:00}";
}
