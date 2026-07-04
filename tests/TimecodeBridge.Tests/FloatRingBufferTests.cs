using TimecodeBridge.Core;
using Xunit;

namespace TimecodeBridge.Tests;

public class FloatRingBufferTests
{
    [Fact]
    public void Roundtrips_with_wraparound()
    {
        var ring = new FloatRingBuffer(64);
        var read = new float[48];
        float v = 0;
        for (int pass = 0; pass < 20; pass++)
        {
            var chunk = new float[48];
            for (int i = 0; i < chunk.Length; i++) chunk[i] = v++;
            Assert.Equal(48, ring.Write(chunk));
            Assert.Equal(48, ring.Read(read));
            Assert.Equal(chunk, read);
        }
        Assert.Equal(0, ring.Overruns);
    }

    [Fact]
    public void Drops_and_counts_on_overflow()
    {
        var ring = new FloatRingBuffer(16);
        Assert.Equal(16, ring.Write(new float[20])); // 4 dropped
        Assert.Equal(4, ring.Overruns);
        Assert.Equal(0, ring.Write(new float[5]));   // full — everything dropped
        Assert.Equal(9, ring.Overruns);
    }

    [Fact]
    public void Concurrent_producer_consumer_preserves_sequence()
    {
        var ring = new FloatRingBuffer(1 << 12);
        const int total = 2_000_000;

        var producer = new Thread(() =>
        {
            var chunk = new float[311];
            int sent = 0;
            while (sent < total)
            {
                int n = Math.Min(chunk.Length, total - sent);
                for (int i = 0; i < n; i++) chunk[i] = sent + i;
                int written = ring.Write(chunk.AsSpan(0, n));
                sent += written;
                if (written < n) Thread.Yield();
            }
        });

        long errors = 0;
        var consumer = new Thread(() =>
        {
            var buf = new float[479];
            int got = 0;
            while (got < total)
            {
                int n = ring.Read(buf);
                for (int i = 0; i < n; i++)
                    if (buf[i] != got + i) Interlocked.Increment(ref errors);
                got += n;
                if (n == 0) Thread.Yield();
            }
        });

        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(30000) && consumer.Join(30000), "Threads did not finish in time");
        // Overruns are expected (the producer deliberately outruns the consumer and
        // retries); what must hold is that no sample is ever lost or reordered.
        Assert.Equal(0, Interlocked.Read(ref errors));
    }
}
