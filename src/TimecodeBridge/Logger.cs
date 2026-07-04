namespace TimecodeBridge;

/// <summary>
/// Minimal thread-safe event/error logger. Never used on the per-frame hot path.
/// Logging failures are swallowed — a full disk must never take the show down.
/// </summary>
public static class Logger
{
    static readonly object Sync = new();
    static readonly string LogPath = ResolvePath();

    static string ResolvePath()
    {
        string preferred = Path.Combine(AppContext.BaseDirectory, "timecodebridge.log");
        try
        {
            // Cap the log at 1 MB; start fresh when it grows past that.
            if (File.Exists(preferred) && new FileInfo(preferred).Length > 1_000_000)
                File.Delete(preferred);
            File.AppendAllText(preferred, "");
            return preferred;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "timecodebridge.log");
        }
    }

    static int _writesSinceSizeCheck;

    public static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                // Re-check the cap during the run too — this app is designed to run
                // for weeks, and retry loops must not fill the show machine's disk.
                if (++_writesSinceSizeCheck >= 200)
                {
                    _writesSinceSizeCheck = 0;
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 1_000_000)
                        File.WriteAllText(LogPath,
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  --- log rotated (1 MB cap) ---{Environment.NewLine}");
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let logging failures propagate.
        }
    }
}
