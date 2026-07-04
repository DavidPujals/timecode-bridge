using TimecodeBridge.Ltc;

namespace TimecodeBridge.Tests;

/// <summary>
/// Reference LTC (SMPTE 12M) audio generator used to exercise the decoder.
/// Biphase-mark modulates 80-bit frames with fractional-sample transition placement,
/// exactly like a real generator running on an unrelated clock.
/// </summary>
public static class LtcGenerator
{
    public static float[] Generate(
        LtcFrame start, int frameCount, TimecodeRate rate, double fps, int sampleRate,
        float amplitude = 0.8f, bool invert = false, ulong userBits = 0)
    {
        double samplesPerBit = sampleRate / (fps * 80.0);
        int total = (int)Math.Ceiling(frameCount * 80 * samplesPerBit) + 8;
        var samples = new float[total];

        float level = invert ? -amplitude : amplitude;
        double t = 0;
        int idx = 0;
        var frame = start;

        void FillTo(double position)
        {
            int end = Math.Min((int)Math.Round(position), samples.Length);
            while (idx < end) samples[idx++] = level;
        }

        var bits = new byte[80];
        for (int f = 0; f < frameCount; f++)
        {
            EncodeFrame(frame, rate, userBits, bits);
            for (int b = 0; b < 80; b++)
            {
                level = -level; // cell-boundary transition
                if (bits[b] == 1)
                {
                    FillTo(t + samplesPerBit / 2);
                    level = -level; // mid-cell transition for a '1'
                }
                FillTo(t + samplesPerBit);
                t += samplesPerBit;
            }
            frame = frame.AddFrames(1, rate);
        }
        FillTo(samples.Length);
        return samples;
    }

    static void EncodeFrame(in LtcFrame f, TimecodeRate rate, ulong userBits, byte[] bits)
    {
        Array.Clear(bits);
        Put(bits, 0, f.Frames % 10, 4);
        Put(bits, 8, f.Frames / 10, 2);
        if (rate == TimecodeRate.Df2997) bits[10] = 1;
        Put(bits, 16, f.Seconds % 10, 4);
        Put(bits, 24, f.Seconds / 10, 3);
        Put(bits, 32, f.Minutes % 10, 4);
        Put(bits, 40, f.Minutes / 10, 3);
        Put(bits, 48, f.Hours % 10, 4);
        Put(bits, 56, f.Hours / 10, 2);
        // user bit groups UB1..UB8 at 4,12,20,28,36,44,52,60
        for (int g = 0; g < 8; g++)
            Put(bits, 4 + g * 8, (int)(userBits >> (g * 4)) & 0xF, 4);
        // sync word 0011 1111 1111 1101 (bits 64..79)
        for (int i = 66; i <= 77; i++) bits[i] = 1;
        bits[79] = 1;
    }

    static void Put(byte[] bits, int pos, int value, int count)
    {
        for (int i = 0; i < count; i++)
            bits[pos + i] = (byte)((value >> i) & 1);
    }

    public static void AddNoise(float[] samples, float amount, int seed = 42)
    {
        var rng = new Random(seed);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += (float)(rng.NextDouble() * 2 - 1) * amount;
    }

    public static void AddDcOffset(float[] samples, float offset)
    {
        for (int i = 0; i < samples.Length; i++)
            samples[i] += offset;
    }
}
