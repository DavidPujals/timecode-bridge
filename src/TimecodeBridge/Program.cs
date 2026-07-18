using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace TimecodeBridge;

static class Program
{
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint uMilliseconds);

    [STAThread]
    static void Main(string[] args)
    {
        // Watchdog mode: no UI, no mutex — just guard the parent process.
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            Watchdog.Run(parentPid);
            return;
        }

        // Post-update restart: the old instance still holds the single-instance
        // mutex for a moment — wait for it to exit or this launch would be treated
        // as a "show the window" signal and quit.
        if (args.Length >= 2 && args[0] == "--restarted" && int.TryParse(args[1], out int oldPid))
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); }
            catch (ArgumentException) { /* already gone */ }
        }

        using var mutex = new Mutex(true, @"Local\TimecodeBridge_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Ask the running instance to bring its window out of the tray. The
            // event appears a moment after the mutex during that instance's startup,
            // so retry briefly — and never block an unattended show machine with a
            // modal dialog if it can't be reached.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var show = EventWaitHandle.OpenExisting(MainForm.ShowSignalName);
                    show.Set();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(400);
                }
            }
            Logger.Log("Second launch: running instance did not expose its show signal");
            return;
        }

        UpdateService.CleanupLeftovers();

        // 1 ms system timer resolution — needed for accurate freewheel timing and
        // tight worker-thread wait granularity.
        timeBeginPeriod(1);

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch { /* not permitted — run at normal priority */ }

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Application.ThreadException += (_, e) =>
        {
            Logger.Log("UI thread exception: " + e.Exception);
            // A modal during shutdown would silently pin a half-closed process open.
            if (!MainForm.ShuttingDown)
                MessageBox.Show("Unexpected error (engine keeps running):\n" + e.Exception.Message,
                    "Timecode Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log("Fatal exception: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();
        Logger.Log("--- Timecode Bridge started ---");
        Application.Run(new MainForm());
        Logger.Log("--- Timecode Bridge exited ---");
    }
}
