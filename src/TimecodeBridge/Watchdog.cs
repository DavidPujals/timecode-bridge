using System.Diagnostics;

namespace TimecodeBridge;

/// <summary>
/// Crash watchdog: a second instance of this exe launched with "--watchdog &lt;pid&gt;".
/// It waits for the main process to exit; if the exit was NOT preceded by the
/// clean-exit signal, it relaunches the app. Clean quits (tray menu, Windows
/// shutdown) set the signal first, so only real crashes trigger a restart.
/// </summary>
public static class Watchdog
{
    // Pid-scoped: a session-wide name would let any other copy of this exe (a test
    // build, a second install) set or reset OUR cancel event — a stale "clean exit"
    // signal would then mask a real crash and the watchdog would never relaunch.
    static string CancelEventName(int pid) => $@"Local\TimecodeBridge_WatchdogCancel_{pid}";

    static EventWaitHandle? _cancel;
    static Process? _process;

    /// <summary>Main-app side: spawn (or re-arm) the watchdog process.</summary>
    public static void Enable()
    {
        try
        {
            _cancel ??= new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(Environment.ProcessId));
            _cancel.Reset();
            if (_process is { HasExited: false }) return;
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--watchdog {Environment.ProcessId}",
                UseShellExecute = false,
            });
            Logger.Log($"Watchdog armed (pid {_process?.Id})");
        }
        catch (Exception ex)
        {
            Logger.Log("Watchdog start failed: " + ex.Message);
        }
    }

    /// <summary>Main-app side: tell the watchdog to stand down and exit.</summary>
    public static void Disable()
    {
        _cancel?.Set();
        var process = _process;
        _process = null;
        if (process != null)
        {
            // The watchdog samples the cancel event twice a second; a rapid
            // off→on toggle could reset the event before it looks. Make sure this
            // one is really gone so two watchdogs can never be armed at once.
            Task.Run(() =>
            {
                try
                {
                    if (!process.WaitForExit(1500))
                        process.Kill();
                }
                catch { /* already exited */ }
                finally { process.Dispose(); }
            });
        }
        Logger.Log("Watchdog disarmed");
    }

    /// <summary>Main-app side: mark the imminent exit as intentional.</summary>
    public static void SignalCleanExit() => _cancel?.Set();

    /// <summary>Watchdog-process side: blocks until the parent exits, then decides.</summary>
    public static void Run(int parentPid)
    {
        try
        {
            using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(parentPid));
            Process parent;
            try { parent = Process.GetProcessById(parentPid); }
            catch { return; } // parent already gone before we started — nothing to guard

            while (true)
            {
                if (cancel.WaitOne(0)) return; // disarmed
                if (parent.WaitForExit(500))
                {
                    // Give a clean-exit signal racing the process teardown a moment.
                    if (cancel.WaitOne(1500))
                    {
                        Logger.Log("Watchdog: clean exit confirmed — standing down");
                        return;
                    }
                    if (!RecordRelaunchAllowed())
                    {
                        Logger.Log("Watchdog: crash loop detected (5 relaunches in 10 min) — giving up");
                        return;
                    }
                    Thread.Sleep(2000); // let drivers/sockets settle before relaunch
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Environment.ProcessPath!,
                            UseShellExecute = true,
                        });
                        Logger.Log("Watchdog: app crashed — relaunched");
                    }
                    catch (Exception ex)
                    {
                        // Nothing more a watchdog can do — but say so, or a dead show
                        // machine gives no clue why the app never came back.
                        Logger.Log("Watchdog: relaunch FAILED: " + ex.Message);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // The watchdog must never crash loudly — but never silently either.
            Logger.Log("Watchdog: internal error: " + ex);
        }
    }

    /// <summary>Crash-loop breaker: allow at most 5 relaunches per 10 minutes,
    /// tracked in a timestamp file that survives across watchdog generations.</summary>
    static bool RecordRelaunchAllowed()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TimecodeBridge-relaunches.txt");
            long cutoff = DateTime.UtcNow.AddMinutes(-10).Ticks;
            var recent = new List<long>();
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                    if (long.TryParse(line, out long t) && t > cutoff)
                        recent.Add(t);
            }
            if (recent.Count >= 5) return false;
            recent.Add(DateTime.UtcNow.Ticks);
            File.WriteAllLines(path, recent.Select(t => t.ToString()));
            return true;
        }
        catch
        {
            return true; // never let bookkeeping block a needed relaunch
        }
    }
}
