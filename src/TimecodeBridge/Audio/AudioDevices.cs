using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TimecodeBridge.Audio;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Device enumeration for the UI. Failures return empty lists, never throw.</summary>
public static class AudioDevices
{
    public static List<AudioDeviceInfo> Asio()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            foreach (var name in AsioOut.GetDriverNames())
                list.Add(new AudioDeviceInfo(name, name));
        }
        catch { /* no ASIO support installed */ }
        return list;
    }

    public static List<AudioDeviceInfo> Wasapi()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch { /* endpoint service unavailable */ }
        return list;
    }

    public static List<AudioDeviceInfo> WaveIn()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new AudioDeviceInfo(i.ToString(), WaveInEvent.GetCapabilities(i).ProductName));
        }
        catch { /* no waveIn devices */ }
        return list;
    }
}
