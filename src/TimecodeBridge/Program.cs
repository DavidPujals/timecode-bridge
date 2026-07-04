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

        // 1 ms system timer resolution — needed for accurate freewheel timing and
        // tight worker-thread wait granularity.
        timeBeginPeriod(1);

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch { /* not permitted — run at normal priority */ }

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Application.ThreadException += (_, e) =>
        {
            Logger.Log("UI thread exception: " + e.Exception);
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
