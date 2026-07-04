namespace TimecodeBridge.Core;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer for audio samples.
/// The audio driver callback writes; the decode thread reads. On overflow the
/// newest samples are dropped and counted (the decoder resynchronises on its own).
/// </summary>
public sealed class FloatRingBuffer
{
    readonly float[] _buf;
    readonly int _mask;
    long _write; // total samples ever written (monotonic)
    long _read;  // total samples ever read (monotonic)
    long _overruns;

    public FloatRingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacity));
        _buf = new float[capacity];
        _mask = capacity - 1;
    }

    public long Overruns => Interlocked.Read(ref _overruns);

    /// <summary>Consumer-side: discard everything currently buffered.</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _write));

    /// <summary>Producer side. Returns the number of samples actually written.</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long w = _write;
        long r = Volatile.Read(ref _read);
        int free = _buf.Length - (int)(w - r);
        int count = src.Length;
        if (count > free)
        {
            Interlocked.Add(ref _overruns, count - free);
            count = free;
            if (count == 0) return 0;
            src = src[..count];
        }

        int idx = (int)(w & _mask);
        int first = Math.Min(count, _buf.Length - idx);
        src[..first].CopyTo(_buf.AsSpan(idx));
        if (count > first)
            src[first..].CopyTo(_buf);

        Volatile.Write(ref _write, w + count);
        return count;
    }

    /// <summary>Consumer side. Returns the number of samples read into <paramref name="dst"/>.</summary>
    public int Read(Span<float> dst)
    {
        long r = _read;
        long w = Volatile.Read(ref _write);
        int available = (int)(w - r);
        int count = Math.Min(available, dst.Length);
        if (count <= 0) return 0;

        int idx = (int)(r & _mask);
        int first = Math.Min(count, _buf.Length - idx);
        _buf.AsSpan(idx, first).CopyTo(dst);
        if (count > first)
            _buf.AsSpan(0, count - first).CopyTo(dst[first..]);

        Volatile.Write(ref _read, r + count);
        return count;
    }
}
