using System.Text.Json;

namespace TimecodeBridge;

public sealed class AppConfig
{
    public string InputDriver { get; set; } = "Wasapi"; // Asio | Wasapi | Wdm
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int Channel { get; set; } = 1;               // 1-based
    public string LocalIp { get; set; } = "";           // "" = any interface
    public string TargetIp { get; set; } = "255.255.255.255";
    public string LocalIp2 { get; set; } = "";
    public string TargetIp2 { get; set; } = "";         // "" = backup output disabled
    public int Port { get; set; } = 6454;
    public string Rate { get; set; } = "Auto";          // Auto | 24 | 25 | 29.97 DF | 30
    public int OffsetFrames { get; set; } = 1;
    public int FreewheelFrames { get; set; } = 25;
    public bool AlertOnLoss { get; set; } = true;
    public bool AlertSound { get; set; }
    public bool GeneratorFallback { get; set; }
    public bool WatchdogEnabled { get; set; }
    public bool Locked { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static string PortablePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    static string RoamingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TimecodeBridge", "config.json");

    public static AppConfig Load()
    {
        foreach (var path in new[] { PortablePath, RoamingPath })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Logger.Log($"Config load failed ({path}): {ex.Message}");
            }
        }
        return new AppConfig();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        try
        {
            File.WriteAllText(PortablePath, json); // portable install (exe dir writable)
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RoamingPath)!);
                File.WriteAllText(RoamingPath, json);
            }
            catch (Exception ex)
            {
                Logger.Log("Config save failed: " + ex.Message);
            }
        }
    }
}
