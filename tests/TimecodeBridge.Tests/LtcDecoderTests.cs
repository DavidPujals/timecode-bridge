using TimecodeBridge.Ltc;
using Xunit;

namespace TimecodeBridge.Tests;

public class LtcDecoderTests
{
    static List<LtcFrame> Decode(float[] samples, int sampleRate, int chunkSize = 480, LtcDecoder? decoder = null)
    {
        var dec = decoder ?? new LtcDecoder(sampleRate);
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);
        for (int i = 0; i < samples.Length; i += chunkSize)
            dec.Process(samples.AsSpan(i, Math.Min(chunkSize, samples.Length - i)));
        return frames;
    }

    static void AssertConsecutive(List<LtcFrame> frames, TimecodeRate rate)
    {
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].TimeEquals(frames[i - 1].AddFrames(1, rate)),
                $"Frame {i} not consecutive: {frames[i - 1]} -> {frames[i]}");
    }

    [Theory]
    [InlineData(48000, 25.0, TimecodeRate.Ebu25)]
    [InlineData(48000, 24.0, TimecodeRate.Film24)]
    [InlineData(48000, 30.0, TimecodeRate.Smpte30)]
    [InlineData(44100, 25.0, TimecodeRate.Ebu25)]
    [InlineData(44100, 24.0, TimecodeRate.Film24)]
    [InlineData(96000, 30.0, TimecodeRate.Smpte30)]
    [InlineData(192000, 25.0, TimecodeRate.Ebu25)]
    public void Decodes_clean_signal_at_all_rates(int sampleRate, double fps, TimecodeRate rate)
    {
        var start = new LtcFrame(0, 59, 58, 20 % LtcFrame.NominalFps(rate));
        var audio = LtcGenerator.Generate(start, 60, rate, fps, sampleRate);
        var frames = Decode(audio, sampleRate);

        // Lock requires two frames, and the last generated frame is undecodable by
        // definition (its final bit completes only at the next frame's first
        // transition), so 60 generated frames yield up to 58 emissions.
        Assert.InRange(frames.Count, 55, 58);
        AssertConsecutive(frames, rate);
        // Locked within the first few frames, and the gapless run crosses the hour
        // rollover to land on the expected final frame.
        long firstDelta = frames[0].ToFrameNumber(rate) - start.ToFrameNumber(rate);
        Assert.InRange(firstDelta, 1, 4);
        Assert.Equal(start.AddFrames(firstDelta + frames.Count - 1, rate).ToString(), frames[^1].ToString());
        Assert.Equal(start.AddFrames(58, rate).ToString(), frames[^1].ToString());
    }

    [Fact]
    public void Decodes_drop_frame_and_flags_it()
    {
        var start = new LtcFrame(0, 0, 59, 20, dropFrame: true);
        var audio = LtcGenerator.Generate(start, 40, TimecodeRate.Df2997, 29.97, 48000);
        var dec = new LtcDecoder(48000);
        var frames = Decode(audio, 48000, decoder: dec);

        Assert.True(frames.Count >= 36);
        AssertConsecutive(frames, TimecodeRate.Df2997);
        Assert.All(frames, f => Assert.True(f.DropFrame));
        Assert.Equal(TimecodeRate.Df2997, dec.DetectedRate);
        // The minute boundary must skip frames 0 and 1: ...59;29 -> 00;02
        Assert.Contains(frames, f => f.Minutes == 1 && f.Seconds == 0 && f.Frames == 2);
        Assert.DoesNotContain(frames, f => f.Minutes == 1 && f.Seconds == 0 && f.Frames < 2);
    }

    [Fact]
    public void Survives_low_level_dc_offset_noise_and_inversion()
    {
        var start = new LtcFrame(10, 20, 30, 0);
        var audio = LtcGenerator.Generate(start, 60, TimecodeRate.Ebu25, 25.0, 48000,
            amplitude: 0.08f, invert: true);
        LtcGenerator.AddDcOffset(audio, 0.35f);
        LtcGenerator.AddNoise(audio, 0.02f);

        var frames = Decode(audio, 48000);
        Assert.True(frames.Count >= 50, $"Only {frames.Count} frames decoded");
        AssertConsecutive(frames, TimecodeRate.Ebu25);
    }

    [Fact]
    public void Survives_varispeed()
    {
        var start = new LtcFrame(1, 0, 0, 0);
        var audio = LtcGenerator.Generate(start, 60, TimecodeRate.Ebu25, 25.0 * 1.06, 48000);
        var frames = Decode(audio, 48000);
        Assert.True(frames.Count >= 50);
        AssertConsecutive(frames, TimecodeRate.Ebu25);
    }

    [Fact]
    public void Chunk_size_does_not_change_output()
    {
        var start = new LtcFrame(5, 0, 0, 0);
        var audio = LtcGenerator.Generate(start, 30, TimecodeRate.Smpte30, 30.0, 48000);

        var a = Decode(audio, 48000, chunkSize: 1);
        var b = Decode(audio, 48000, chunkSize: 7);
        var c = Decode(audio, 48000, chunkSize: 4096);

        Assert.Equal(a.Select(f => f.ToString()), b.Select(f => f.ToString()));
        Assert.Equal(a.Select(f => f.ToString()), c.Select(f => f.ToString()));
    }

    [Fact]
    public void Adversarial_user_bits_do_not_produce_output()
    {
        // All-ones user bits maximise the chance of a fake sync word inside the frame.
        var start = new LtcFrame(2, 0, 0, 0);
        var audio = LtcGenerator.Generate(start, 60, TimecodeRate.Ebu25, 25.0, 48000,
            userBits: 0xFFFFFFFF);
        var frames = Decode(audio, 48000);

        Assert.True(frames.Count >= 50, $"Only {frames.Count} frames decoded with hostile user bits");
        AssertConsecutive(frames, TimecodeRate.Ebu25);
    }

    [Fact]
    public void Silence_and_noise_produce_no_frames()
    {
        var silence = new float[48000 * 2];
        Assert.Empty(Decode(silence, 48000));

        var noise = new float[48000 * 2];
        LtcGenerator.AddNoise(noise, 0.5f);
        Assert.Empty(Decode(noise, 48000));
    }

    [Fact]
    public void Relocks_after_dropout_and_reports_signal_state()
    {
        var dec = new LtcDecoder(48000);
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);

        var part1 = LtcGenerator.Generate(new LtcFrame(1, 0, 0, 0), 20, TimecodeRate.Ebu25, 25.0, 48000);
        dec.Process(part1);
        Assert.True(dec.Locked);
        Assert.True(dec.SignalPresent);
        int before = frames.Count;

        dec.Process(new float[48000]); // 1 s of silence
        Assert.False(dec.Locked);
        Assert.False(dec.SignalPresent);
        Assert.Equal(before, frames.Count);

        var part2 = LtcGenerator.Generate(new LtcFrame(3, 0, 0, 0), 20, TimecodeRate.Ebu25, 25.0, 48000);
        dec.Process(part2);
        Assert.True(dec.Locked);
        Assert.True(frames.Count > before + 10);
        Assert.Equal(3, frames[^1].Hours);
    }

    [Fact]
    public void Digital_silence_dither_never_reports_a_rate()
    {
        // ±1 LSB 16-bit dither — what an idle Dante Virtual Soundcard input looks
        // like. The envelope floor must keep this below the transition threshold,
        // so no sync, no rate, no frames — ever.
        var rng = new Random(20260713);
        const float lsb = 1f / 32768f;
        var buf = new float[48000 * 60];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = rng.NextDouble() < 0.5 ? lsb : -lsb;

        var dec = new LtcDecoder(48000);
        int signalPolls = 0;
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);
        for (int off = 0; off < buf.Length; off += 480)
        {
            dec.Process(buf.AsSpan(off, 480));
            if (dec.SignalPresent && dec.MeasuredFps > 0) signalPolls++;
        }
        Assert.Empty(frames);
        Assert.Equal(0, signalPolls);
        Assert.Equal(0.0, dec.MeasuredFps);
    }

    [Fact]
    public void Two_syncs_do_not_establish_a_rate_but_three_do()
    {
        // 3 generated frames flush only 2 sync words (the last frame's sync needs a
        // following transition) — one span. A rate needs two consistent spans, so a
        // coincidental pair of noise-made syncs can never seed MeasuredFps.
        var dec2 = new LtcDecoder(48000);
        dec2.Process(LtcGenerator.Generate(new LtcFrame(0, 0, 0, 0), 3, TimecodeRate.Ebu25, 25.0, 48000));
        Assert.Equal(0.0, dec2.MeasuredFps);
        Assert.False(dec2.Locked);

        // One more frame = three syncs = two consistent spans: rate established.
        var dec3 = new LtcDecoder(48000);
        dec3.Process(LtcGenerator.Generate(new LtcFrame(0, 0, 0, 0), 4, TimecodeRate.Ebu25, 25.0, 48000));
        Assert.InRange(dec3.MeasuredFps, 24.9, 25.1);
    }

    [Fact]
    public void Stray_sync_after_silence_does_not_light_signal()
    {
        // The production incident: a brief blip latches a measured rate, the input
        // goes silent, and a later lone stray sync pattern lights SIGNAL (and could
        // even decode a bogus frame that the bridge would transmit as Art-Net).
        var blip1 = LtcGenerator.Generate(new LtcFrame(0, 0, 0, 0), 3, TimecodeRate.Ebu25, 25.0, 48000);
        var silence = new float[48000 * 30];
        var blip2full = LtcGenerator.Generate(new LtcFrame(7, 7, 7, 7), 2, TimecodeRate.Ebu25, 25.0, 48000);
        var blip2 = blip2full.AsSpan(0, (int)(1.2 * 1920)).ToArray(); // one frame + enough to flush its sync
        var tail = new float[48000 * 2];
        var all = blip1.Concat(silence).Concat(blip2).Concat(tail).ToArray();

        var dec = new LtcDecoder(48000);
        var frames = new List<LtcFrame>();
        dec.FrameDecoded = f => frames.Add(f);
        long afterMark = blip1.Length + 48000 * 25L; // well into the silence
        int latePolls = 0;
        for (int off = 0; off < all.Length; off += 480)
        {
            int n = Math.Min(480, all.Length - off);
            dec.Process(all.AsSpan(off, n));
            if (off + n > afterMark && dec.SignalPresent && dec.MeasuredFps > 0)
                latePolls++;
        }
        Assert.Equal(0, latePolls);
        Assert.Empty(frames);
        Assert.False(dec.Locked);
    }

    [Fact]
    public void Measures_frame_rate_accurately()
    {
        var dec = new LtcDecoder(48000);
        var audio = LtcGenerator.Generate(new LtcFrame(0, 0, 0, 0), 60, TimecodeRate.Smpte30, 29.97, 48000);
        Decode(audio, 48000, decoder: dec);
        Assert.InRange(dec.MeasuredFps, 29.9, 30.05);
        Assert.Equal(TimecodeRate.Smpte30, dec.DetectedRate);

        var dec25 = new LtcDecoder(48000);
        var audio25 = LtcGenerator.Generate(new LtcFrame(0, 0, 0, 0), 60, TimecodeRate.Ebu25, 25.0, 48000);
        Decode(audio25, 48000, decoder: dec25);
        Assert.InRange(dec25.MeasuredFps, 24.9, 25.1);
        Assert.Equal(TimecodeRate.Ebu25, dec25.DetectedRate);
    }
}
