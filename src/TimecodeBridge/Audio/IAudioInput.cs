namespace TimecodeBridge.Audio;

/// <summary>
/// A running audio capture that writes one mono channel of float samples into a
/// ring buffer and pulses an event after each buffer. Created, started and disposed
/// on the engine worker thread.
/// </summary>
public interface IAudioInput : IDisposable
{
    int SampleRate { get; }
    string Description { get; }

    /// <summary>Raised (from a driver thread) if the device stops unexpectedly.</summary>
    event Action<Exception?>? Stopped;

    void Start();
}
