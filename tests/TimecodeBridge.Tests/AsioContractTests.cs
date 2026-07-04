using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.Asio;
using Xunit;

namespace TimecodeBridge.Tests;

public class AsioContractTests
{
    /// <summary>
    /// AsioAudioInput follows the device's current sample rate (critical for
    /// clock-slaved drivers like Dante Virtual Soundcard) by reading AsioOut's
    /// private "driver" field. This pins that contract against the shipped NAudio
    /// version — if an upgrade renames the field, this fails at build time in CI
    /// instead of silently reverting to forcing 48 kHz on show hardware.
    /// </summary>
    [Fact]
    public void AsioOut_private_driver_field_still_exists()
    {
        var field = typeof(AsioOut).GetField("driver", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(AsioDriverExt), field!.FieldType);
        // And the capability type still carries the current sample rate.
        var prop = typeof(AsioDriverExt).GetProperty("Capabilities");
        Assert.NotNull(prop);
        Assert.True(prop!.PropertyType.GetMember("SampleRate").Length > 0,
            "AsioDriverCapability no longer exposes SampleRate");
    }
}
