using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TimecodeBridge.Audio;
using TimecodeBridge.Core;
using TimecodeBridge.Ltc;
using TimecodeBridge.Ui;

namespace TimecodeBridge;

/// <summary>
/// Always-on main window: the engine starts on launch and restarts itself half a
/// second after any setting changes — there is no start/stop button. Closing the
/// window hides it to the tray; the tray menu quits the app.
/// </summary>
public sealed class MainForm : Form
{
    public const string ShowSignalName = @"Local\TimecodeBridge_Show";

    // Palette — near-monochrome dark, green as the single accent, amber for freewheel.
    static readonly Color BackDark = Color.FromArgb(15, 17, 21);
    static readonly Color BackField = Color.FromArgb(26, 29, 35);
    static readonly Color Hairline = Color.FromArgb(40, 44, 52);
    static readonly Color ForeText = Color.FromArgb(222, 226, 232);
    static readonly Color ForeDim = Color.FromArgb(118, 126, 138);
    static readonly Color ForeFaint = Color.FromArgb(70, 76, 86);
    static readonly Color Green = Color.FromArgb(52, 199, 123);
    static readonly Color Amber = Color.FromArgb(229, 180, 66);
    static readonly Color Red = Color.FromArgb(224, 82, 70);

    const int Margin_ = 36;
    const int ContentW = 488;

    readonly Label _lblTc = new();
    readonly Label _lblRateInfo = new();
    readonly Label _lblSignal = new();
    readonly Label _lblLock = new();
    readonly Label _lblTx = new();
    readonly Label _lblPackets = new();
    readonly Panel _levelBar = new();
    readonly DarkCombo _cmbDriver = new();
    readonly DarkCombo _cmbDevice = new();
    readonly DarkStepper _numChannel = new();
    readonly Button _btnRefresh = new();
    readonly DarkCombo _cmbNic = new();
    readonly DarkTextField _txtTargetIpField = new();
    TextBox _txtTargetIp => _txtTargetIpField.Inner;
    readonly DarkStepper _numPort = new();
    readonly DarkCombo _cmbRate = new();
    readonly DarkStepper _numOffset = new();
    readonly DarkStepper _numFreewheel = new();
    readonly DarkCombo _cmbNic2 = new();
    readonly DarkTextField _txtTargetIp2Field = new();
    TextBox _txtTargetIp2 => _txtTargetIp2Field.Inner;
    readonly DarkCheck _chkAlert = new();
    readonly DarkCheck _chkSound = new();
    readonly DarkCheck _chkGenerator = new();
    readonly DarkCheck _chkStartup = new();
    readonly DarkCheck _chkWatchdog = new();
    readonly Button _btnLock = new();
    readonly Label _lblStatus = new();
    readonly System.Windows.Forms.Timer _uiTimer = new();
    readonly System.Windows.Forms.Timer _restartTimer = new();
    readonly ToolTip _tips = new();
    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _trayMenu = new();
    readonly Icon _iconGrey;
    readonly Icon _iconGreen;
    readonly Icon _iconAmber;
    readonly Icon _iconRed;
    readonly EventWaitHandle _showSignal = new(false, EventResetMode.AutoReset, ShowSignalName);
    readonly ManualResetEvent _watcherStop = new(false);
    Thread? _showWatcher;

    AppConfig _config = AppConfig.Load();
    Engine? _engine;
    float _levelShown;
    bool _quitting;
    bool _balloonShown;
    int _trayState = -1;
    string _trayText = "";
    int _prevFlags;
    bool _outputDead; // was sending, now silent — drives the red tray state + alert

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainForm()
    {
        Text = "Timecode Bridge";
        ClientSize = new Size(560, 576);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BackDark;
        ForeColor = ForeText;
        Font = new Font("Segoe UI", 9f);

        _iconGrey = MakeDotIcon(ForeFaint);
        _iconGreen = MakeDotIcon(Green);
        _iconAmber = MakeDotIcon(Amber);
        _iconRed = MakeDotIcon(Red);
        // Window/taskbar/Alt-Tab icon: use the multi-size icon embedded in the exe.
        // A 16 px tray icon here renders as a blank on the taskbar of some machines.
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? MakeDotIcon(Green, 32); }
        catch { Icon = MakeDotIcon(Green, 32); }

        BuildLayout();
        BuildTray();
        LoadDeviceList();
        ApplyConfigToUi();
        HookSettingChanges();

        _restartTimer.Interval = 500;
        _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); RestartEngine(); };

        _uiTimer.Interval = 33;
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();

        // A second app launch signals this event instead of starting — bring the
        // window back from the tray when that happens.
        _showWatcher = new Thread(() =>
        {
            try
            {
                var handles = new WaitHandle[] { _watcherStop, _showSignal };
                while (WaitHandle.WaitAny(handles) == 1)
                {
                    try { BeginInvoke(ShowFromTray); }
                    catch (InvalidOperationException) { /* window handle not ready / closing */ }
                }
            }
            catch (ObjectDisposedException) { /* shutdown disposed the handles first */ }
        }) { IsBackground = true, Name = "show-watcher" };
        _showWatcher.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar (Windows 10 20H1+ / Windows 11).
        int dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_config.WatchdogEnabled)
            Watchdog.Enable();
        StartEngine();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window hides to the tray; only the tray menu (or Windows
        // shutdown / task manager) actually quits.
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(2500, "Timecode Bridge is still running",
                    "LTC keeps translating in the background. Right-click the tray icon to quit.",
                    ToolTipIcon.Info);
            }
            base.OnFormClosing(e);
            return;
        }

        Watchdog.SignalCleanExit(); // before anything else: this exit is intentional
        _uiTimer.Stop();
        _restartTimer.Stop();
        _watcherStop.Set();
        _showWatcher?.Join(1000);
        _engine?.Dispose();
        _engine = null;
        SaveUiToConfig();
        _config.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _showSignal.Dispose();
        _watcherStop.Dispose();
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ tray

    static Icon MakeDotIcon(Color color, int size = 16)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            int pad = Math.Max(1, size / 16);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
            using var pen = new Pen(Color.FromArgb(60, 0, 0, 0));
            g.DrawEllipse(pen, pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    void BuildTray()
    {
        var open = new ToolStripMenuItem("Open Timecode Bridge", null, (_, _) => ShowFromTray())
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        _trayMenu.Items.Add(open);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => { _quitting = true; Close(); }));

        _tray.Icon = _iconGrey;
        _tray.Text = "Timecode Bridge";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    static string RateName(TimecodeRate rate) => rate switch
    {
        TimecodeRate.Film24 => "24 · Film",
        TimecodeRate.Ebu25 => "25 · EBU",
        TimecodeRate.Df2997 => "29.97 · Drop-frame",
        _ => "30 · SMPTE",
    };

    /// <summary>Edge-triggered notifications: LTC lost → generator, and output dead.</summary>
    void HandleAlerts(int flags)
    {
        bool tx = (flags & Engine.FlagTx) != 0;
        bool generator = (flags & Engine.FlagGenerator) != 0;
        bool txPrev = (_prevFlags & Engine.FlagTx) != 0;
        bool genPrev = (_prevFlags & Engine.FlagGenerator) != 0;

        if (generator && !genPrev && txPrev)
        {
            // Real timecode died and the generator took over.
            if (_chkAlert.Checked)
                _tray.ShowBalloonTip(5000, "LTC signal lost",
                    "Internal generator has taken over — timecode continues from the PC clock.",
                    ToolTipIcon.Warning);
            if (_chkSound.Checked) System.Media.SystemSounds.Exclamation.Play();
        }
        else if (!tx && txPrev && !generator)
        {
            // Output went fully silent (freewheel exhausted, no generator).
            _outputDead = true;
            if (_chkAlert.Checked)
                _tray.ShowBalloonTip(5000, "Timecode output stopped",
                    "LTC signal lost and freewheel has ended. No timecode is being sent.",
                    ToolTipIcon.Error);
            if (_chkSound.Checked) System.Media.SystemSounds.Hand.Play();
        }
        if (tx) _outputDead = false;
        _prevFlags = flags;
    }

    void UpdateTray(int state, string text)
    {
        if (state != _trayState)
        {
            _trayState = state;
            _tray.Icon = state switch { 1 => _iconGreen, 2 => _iconAmber, 3 => _iconRed, _ => _iconGrey };
        }
        if (text.Length > 63) text = text[..63]; // NotifyIcon hard limit
        if (text != _trayText)
        {
            _trayText = text;
            _tray.Text = text;
        }
    }

    // ---------------------------------------------------------------- layout

    void BuildLayout()
    {
        _lblTc.SetBounds(0, 22, 560, 68);
        _lblTc.TextAlign = ContentAlignment.MiddleCenter;
        _lblTc.Font = new Font("Consolas", 46f, FontStyle.Bold);
        _lblTc.ForeColor = ForeFaint;
        _lblTc.Text = "--:--:--:--";

        _lblRateInfo.SetBounds(0, 94, 560, 18);
        _lblRateInfo.TextAlign = ContentAlignment.MiddleCenter;
        _lblRateInfo.ForeColor = ForeDim;
        _lblRateInfo.Font = new Font("Segoe UI", 9f);
        _lblRateInfo.Text = "";

        int y = 126;
        StyleDot(_lblSignal, "SIGNAL", Margin_, y);
        StyleDot(_lblLock, "LOCK", Margin_ + 96, y);
        StyleDot(_lblTx, "TX", Margin_ + 172, y);
        _lblPackets.SetBounds(320, y, Margin_ + ContentW - 320, 16);
        _lblPackets.TextAlign = ContentAlignment.MiddleRight;
        _lblPackets.ForeColor = ForeFaint;
        _lblPackets.Font = new Font("Segoe UI", 8.5f);
        _lblPackets.Text = "0 packets";

        _levelBar.SetBounds(Margin_, y + 26, ContentW, 3);
        _levelBar.BackColor = BackField;
        _levelBar.Paint += PaintLevel;

        // INPUT ----------------------------------------------------------
        int sy = 186;
        AddSection("INPUT", sy);
        int r1 = sy + 30, r2 = sy + 64;
        AddFieldLabel("Driver", Margin_, r1);
        StyleCombo(_cmbDriver, 100, r1 - 4, 130);
        _cmbDriver.Items.AddRange(new object[] { "ASIO", "WASAPI", "WDM (WaveIn)" });
        _cmbDriver.SelectedIndexChanged += (_, _) => LoadDeviceList();
        AddFieldLabel("Device", 248, r1);
        StyleCombo(_cmbDevice, 300, r1 - 4, 224);
        AddFieldLabel("Channel", Margin_, r2);
        StyleNumeric(_numChannel, 100, r2 - 4, 60, 1, 64, 1);
        _btnRefresh.SetBounds(444, r2 - 5, 80, 26);
        StyleButton(_btnRefresh, "Refresh");
        _btnRefresh.Click += (_, _) => LoadDeviceList();

        // OUTPUT ---------------------------------------------------------
        sy = 288;
        AddSection("OUTPUT", sy);
        r1 = sy + 30;
        r2 = sy + 64;
        int r3 = sy + 98;
        int r4 = sy + 132;
        AddFieldLabel("Interface", Margin_, r1);
        StyleCombo(_cmbNic, 100, r1 - 4, 424);
        AddFieldLabel("Target IP", Margin_, r2);
        _txtTargetIpField.SetBounds(100, r2 - 4, 140, 24);
        Controls.Add(_txtTargetIpField);
        AddFieldLabel("Port", 260, r2);
        StyleNumeric(_numPort, 294, r2 - 4, 66, 1, 65535, 6454);
        AddFieldLabel("Rate", 380, r2);
        StyleCombo(_cmbRate, 414, r2 - 4, 110);
        _cmbRate.Items.AddRange(new object[] { "Auto", "24", "25", "29.97 DF", "30" });
        AddFieldLabel("Backup", Margin_, r3);
        StyleCombo(_cmbNic2, 100, r3 - 4, 240);
        AddFieldLabel("IP", 356, r3);
        _txtTargetIp2Field.SetBounds(384, r3 - 4, 140, 24);
        Controls.Add(_txtTargetIp2Field);
        AddFieldLabel("Offset", Margin_, r4);
        StyleNumeric(_numOffset, 100, r4 - 4, 56, -10, 10, 1);
        AddFieldLabel("Freewheel", 190, r4);
        StyleNumeric(_numFreewheel, 258, r4 - 4, 60, 0, 300, 25);
        AddFieldLabel("frames", 328, r4);

        // SAFETY ---------------------------------------------------------
        sy = 456;
        AddSection("SHOW SAFETY", sy);
        int s1 = sy + 28, s2 = sy + 56;
        StyleCheck(_chkAlert, "Signal-loss alert", Margin_, s1, 130);
        StyleCheck(_chkSound, "Alert sound", 176, s1, 100);
        StyleCheck(_chkGenerator, "Generator fallback", 296, s1, 150);
        StyleCheck(_chkStartup, "Run at startup", Margin_, s2, 130);
        StyleCheck(_chkWatchdog, "Crash watchdog", 176, s2, 130);
        _btnLock.SetBounds(404, s2 - 3, 120, 26);
        StyleButton(_btnLock, "LOCK SETTINGS");
        _btnLock.Click += (_, _) => SetLocked(!_config.Locked);
        Controls.Add(_btnLock);

        _lblStatus.SetBounds(Margin_, 548, ContentW, 18);
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.ForeColor = ForeFaint;
        _lblStatus.Font = new Font("Segoe UI", 8.5f);
        _lblStatus.Text = "Starting…";

        Controls.AddRange(new Control[]
        {
            _lblTc, _lblRateInfo, _lblSignal, _lblLock, _lblTx, _lblPackets,
            _levelBar, _btnRefresh, _lblStatus,
        });

        SetTip(_numOffset, "Frames added before sending. +1 compensates for the one-frame LTC read delay.");
        SetTip(_numFreewheel, "Frames to keep generating after the LTC signal disappears.");
        SetTip(_txtTargetIpField, "Unicast IP of the console/media server, or a broadcast address.");
        SetTip(_cmbNic, "Network interface the Art-Net packets leave from.");
        SetTip(_txtTargetIp2Field, "Optional second target (backup console). Leave empty to disable.");
        SetTip(_cmbNic2, "Interface for the backup target.");
        SetTip(_chkGenerator, "When LTC dies (beyond freewheel), keep generating timecode from the PC clock until it returns. Starts at 00:00:00:00 if no LTC ever arrives.");
        SetTip(_chkWatchdog, "A companion process relaunches the bridge within seconds if it ever crashes.");
        SetTip(_chkAlert, "Windows notification when timecode output stops or falls back to the generator.");
        SetTip(_btnLock, "Lock all settings so nothing can be changed mid-show.");
    }

    void StyleCheck(DarkCheck c, string text, int x, int y, int w)
    {
        c.Text = text;
        c.SetBounds(x, y, w, 20);
        c.Font = new Font("Segoe UI", 8.75f);
        Controls.Add(c);
    }

    void SetLocked(bool locked)
    {
        _config.Locked = locked;
        _config.Save();
        foreach (Control c in new Control[]
                 {
                     _cmbDriver, _cmbDevice, _numChannel, _btnRefresh, _cmbNic,
                     _txtTargetIpField, _numPort, _cmbRate, _numOffset, _numFreewheel,
                     _cmbNic2, _txtTargetIp2Field, _chkAlert, _chkSound, _chkGenerator,
                     _chkStartup, _chkWatchdog,
                 })
            c.Enabled = !locked;
        _btnLock.Text = locked ? "UNLOCK" : "LOCK SETTINGS";
        _btnLock.ForeColor = locked ? Amber : ForeDim;
        _btnLock.FlatAppearance.BorderColor = locked ? Amber : Hairline;
    }

    // Composite controls (steppers, framed fields) swallow hover with their children —
    // the tip must be registered on every child too or it never shows.
    void SetTip(Control c, string text)
    {
        _tips.SetToolTip(c, text);
        foreach (Control child in c.Controls)
            _tips.SetToolTip(child, text);
    }

    void AddSection(string title, int y)
    {
        var l = new Label
        {
            Text = title,
            ForeColor = ForeFaint,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize = true,
        };
        l.SetBounds(Margin_, y, 10, 14);
        Controls.Add(l);

        var line = new Panel { BackColor = Hairline };
        line.SetBounds(Margin_ + 60, y + 7, ContentW - 60, 1);
        Controls.Add(line);
    }

    void AddFieldLabel(string text, int x, int y)
    {
        var l = new Label { Text = text, ForeColor = ForeDim, AutoSize = true };
        l.SetBounds(x, y, 10, 16);
        Controls.Add(l);
    }

    void StyleCombo(DarkCombo c, int x, int y, int w)
    {
        c.SetBounds(x, y, w, 24);
        Controls.Add(c);
    }

    void StyleNumeric(DarkStepper n, int x, int y, int w, int min, int max, int value)
    {
        n.SetBounds(x, y, w, 24);
        n.Minimum = min;
        n.Maximum = max;
        n.Value = value;
        Controls.Add(n);
    }

    void StyleButton(Button b, string text)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Hairline;
        b.FlatAppearance.MouseOverBackColor = BackField;
        b.BackColor = BackDark;
        b.ForeColor = ForeDim;
        b.Font = new Font("Segoe UI", 8.5f);
    }

    void StyleDot(Label l, string text, int x, int y)
    {
        l.AutoSize = true;
        l.Location = new Point(x, y);
        l.Text = "●  " + text;
        l.ForeColor = ForeFaint;
        l.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
    }

    static readonly Brush LevelBrushOk = new SolidBrush(Green);
    static readonly Brush LevelBrushClip = new SolidBrush(Red);

    void PaintLevel(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(BackField);
        int w = (int)(_levelShown * _levelBar.Width);
        if (w <= 0) return;
        e.Graphics.FillRectangle(_levelShown > 0.95f ? LevelBrushClip : LevelBrushOk, 0, 0, w, _levelBar.Height);
    }

    // ------------------------------------------------------------- device UI

    void LoadDeviceList()
    {
        _cmbDevice.Items.Clear();
        var devices = _cmbDriver.SelectedIndex switch
        {
            0 => AudioDevices.Asio(),
            2 => AudioDevices.WaveIn(),
            _ => AudioDevices.Wasapi(),
        };
        foreach (var d in devices)
            _cmbDevice.Items.Add(d);
        if (_cmbDevice.Items.Count > 0)
            _cmbDevice.SelectedIndex = 0;
    }

    void LoadNicList()
    {
        _cmbNic.Items.Clear();
        _cmbNic2.Items.Clear();
        _cmbNic.Items.Add(new AudioDeviceInfo("", "Any interface (0.0.0.0)"));
        _cmbNic2.Items.Add(new AudioDeviceInfo("", "Any interface (0.0.0.0)"));
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    _cmbNic.Items.Add(new AudioDeviceInfo(ua.Address.ToString(), $"{ua.Address} — {nic.Name}"));
                    _cmbNic2.Items.Add(new AudioDeviceInfo(ua.Address.ToString(), $"{ua.Address} — {nic.Name}"));
                }
            }
        }
        catch { /* enumeration failed — "Any" is always available */ }
        _cmbNic.SelectedIndex = 0;
        _cmbNic2.SelectedIndex = 0;
    }

    void ApplyConfigToUi()
    {
        _cmbDriver.SelectedIndex = _config.InputDriver switch
        {
            "Asio" => 0,
            "Wdm" => 2,
            _ => 1,
        };
        LoadDeviceList();
        SelectDevice(_config.DeviceId, _config.DeviceName);
        LoadNicList();
        SelectNic(_cmbNic, _config.LocalIp);
        SelectNic(_cmbNic2, _config.LocalIp2);
        _numChannel.Value = Math.Clamp(_config.Channel, (int)_numChannel.Minimum, (int)_numChannel.Maximum);
        _txtTargetIp.Text = _config.TargetIp;
        _txtTargetIp2.Text = _config.TargetIp2;
        _numPort.Value = Math.Clamp(_config.Port, 1, 65535);
        _cmbRate.SelectedIndex = Math.Max(0, _cmbRate.Items.IndexOf(_config.Rate));
        _numOffset.Value = Math.Clamp(_config.OffsetFrames, -10, 10);
        _numFreewheel.Value = Math.Clamp(_config.FreewheelFrames, 0, 300);
        _chkAlert.Checked = _config.AlertOnLoss;
        _chkSound.Checked = _config.AlertSound;
        _chkGenerator.Checked = _config.GeneratorFallback;
        _chkWatchdog.Checked = _config.WatchdogEnabled;
        _chkStartup.Checked = StartupRegistration.IsEnabled;
        SetLocked(_config.Locked);
    }

    static void SelectNic(DarkCombo combo, string id)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((AudioDeviceInfo)combo.Items[i]!).Id == id)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    void SelectDevice(string id, string name)
    {
        for (int i = 0; i < _cmbDevice.Items.Count; i++)
        {
            if (((AudioDeviceInfo)_cmbDevice.Items[i]!).Id == id)
            {
                _cmbDevice.SelectedIndex = i;
                return;
            }
        }
        for (int i = 0; i < _cmbDevice.Items.Count; i++)
        {
            if (((AudioDeviceInfo)_cmbDevice.Items[i]!).Name == name)
            {
                _cmbDevice.SelectedIndex = i;
                return;
            }
        }
    }

    void SaveUiToConfig()
    {
        _config.InputDriver = _cmbDriver.SelectedIndex switch { 0 => "Asio", 2 => "Wdm", _ => "Wasapi" };
        if (_cmbDevice.SelectedItem is AudioDeviceInfo d)
        {
            _config.DeviceId = d.Id;
            _config.DeviceName = d.Name;
        }
        _config.Channel = (int)_numChannel.Value;
        _config.LocalIp = (_cmbNic.SelectedItem as AudioDeviceInfo)?.Id ?? "";
        _config.TargetIp = _txtTargetIp.Text.Trim();
        _config.LocalIp2 = (_cmbNic2.SelectedItem as AudioDeviceInfo)?.Id ?? "";
        _config.TargetIp2 = _txtTargetIp2.Text.Trim();
        _config.Port = (int)_numPort.Value;
        _config.Rate = _cmbRate.SelectedItem?.ToString() ?? "Auto";
        _config.OffsetFrames = (int)_numOffset.Value;
        _config.FreewheelFrames = (int)_numFreewheel.Value;
        _config.AlertOnLoss = _chkAlert.Checked;
        _config.AlertSound = _chkSound.Checked;
        _config.GeneratorFallback = _chkGenerator.Checked;
        _config.WatchdogEnabled = _chkWatchdog.Checked;
    }

    // ------------------------------------------------- always-on engine

    void HookSettingChanges()
    {
        // The driver combo funnels through LoadDeviceList, which changes the device
        // selection and lands here too.
        _cmbDevice.SelectedIndexChanged += OnSettingChanged;
        _numChannel.ValueChanged += OnSettingChanged;
        _cmbNic.SelectedIndexChanged += OnSettingChanged;
        _txtTargetIp.TextChanged += OnSettingChanged;
        _numPort.ValueChanged += OnSettingChanged;
        _cmbRate.SelectedIndexChanged += OnSettingChanged;
        _numOffset.ValueChanged += OnSettingChanged;
        _numFreewheel.ValueChanged += OnSettingChanged;
        _cmbNic2.SelectedIndexChanged += OnSettingChanged;
        _txtTargetIp2.TextChanged += OnSettingChanged;
        _chkGenerator.CheckedChanged += OnSettingChanged;

        // App-level toggles: no engine restart, applied immediately, persisted.
        _chkAlert.CheckedChanged += (_, _) => { SaveUiToConfig(); _config.Save(); };
        _chkSound.CheckedChanged += (_, _) => { SaveUiToConfig(); _config.Save(); };
        _chkStartup.CheckedChanged += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(_chkStartup.Checked); }
            catch (Exception ex)
            {
                Logger.Log("Startup registration failed: " + ex.Message);
                _lblStatus.Text = "Could not update startup registration";
            }
        };
        _chkWatchdog.CheckedChanged += (_, _) =>
        {
            if (_chkWatchdog.Checked) Watchdog.Enable();
            else Watchdog.Disable();
            SaveUiToConfig();
            _config.Save();
        };
    }

    void OnSettingChanged(object? sender, EventArgs e)
    {
        // Debounce so typing an IP or scrolling a combo doesn't thrash the device.
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    void RestartEngine()
    {
        // Dispose off the UI thread: Stop() joins the worker (up to 5 s if a driver
        // hangs) and must never freeze the window. The new engine's input open
        // simply retries until the old device is released.
        var old = _engine;
        _engine = null;
        if (old != null)
            Task.Run(old.Dispose);
        StartEngine();
    }

    void StartEngine()
    {
        if (_cmbDevice.SelectedItem is not AudioDeviceInfo device)
        {
            _lblStatus.Text = "No input device found — check driver selection and press Refresh";
            return;
        }
        if (!IPAddress.TryParse(_txtTargetIp.Text.Trim(), out var target))
        {
            _lblStatus.Text = "Invalid target IP — output paused until corrected";
            return;
        }
        IPAddress? local = null;
        string localId = (_cmbNic.SelectedItem as AudioDeviceInfo)?.Id ?? "";
        if (localId.Length > 0 && !IPAddress.TryParse(localId, out local))
            local = null;

        // Optional backup target — invalid or empty text disables it (logged, not fatal).
        IPAddress? target2 = null;
        string ip2Text = _txtTargetIp2.Text.Trim();
        if (ip2Text.Length > 0 && !IPAddress.TryParse(ip2Text, out target2))
            Logger.Log($"Backup target '{ip2Text}' is not a valid IP — backup output disabled");
        IPAddress? local2 = null;
        string local2Id = (_cmbNic2.SelectedItem as AudioDeviceInfo)?.Id ?? "";
        if (local2Id.Length > 0 && !IPAddress.TryParse(local2Id, out local2))
            local2 = null;

        SaveUiToConfig();
        _config.Save();

        int driver = _cmbDriver.SelectedIndex;
        int channel = (int)_numChannel.Value - 1;
        var cfg = new EngineConfig
        {
            InputFactory = (ring, evt) => driver switch
            {
                0 => new AsioAudioInput(device.Id, channel, ring, evt),
                2 => new WaveInAudioInput(int.Parse(device.Id), channel, ring, evt),
                _ => new WasapiAudioInput(device.Id, channel, ring, evt),
            },
            LocalAddress = local,
            TargetAddress = target,
            TargetAddress2 = target2,
            LocalAddress2 = local2,
            GeneratorFallback = _chkGenerator.Checked,
            Port = (int)_numPort.Value,
            RateOverride = _cmbRate.SelectedItem?.ToString() switch
            {
                "24" => TimecodeRate.Film24,
                "25" => TimecodeRate.Ebu25,
                "29.97 DF" => TimecodeRate.Df2997,
                "30" => TimecodeRate.Smpte30,
                _ => null,
            },
            OffsetFrames = (int)_numOffset.Value,
            FreewheelFrames = (int)_numFreewheel.Value,
        };

        // A fresh engine starts with no TX — stale previous-flags must not read as a
        // "signal lost" transition and raise a false alarm during settings changes.
        _prevFlags = 0;
        _outputDead = false;

        _engine = new Engine(cfg, Logger.Log);
        _engine.Start();
    }

    // -------------------------------------------------------------- polling

    void RefreshStatus()
    {
        var engine = _engine;
        if (engine == null)
        {
            _lblTc.ForeColor = ForeFaint;
            _lblTc.Text = "--:--:--:--";
            _lblRateInfo.Text = "";
            _lblSignal.ForeColor = ForeFaint;
            _lblLock.ForeColor = ForeFaint;
            _lblTx.ForeColor = ForeFaint;
            UpdateTray(0, "Timecode Bridge — " + _lblStatus.Text);
            return;
        }

        Engine.UnpackState(engine.StateBits, out var frame, out var rate, out int flags);
        bool signal = (flags & Engine.FlagSignal) != 0;
        bool locked = (flags & Engine.FlagLocked) != 0;
        bool freewheel = (flags & Engine.FlagFreewheel) != 0;
        bool tx = (flags & Engine.FlagTx) != 0;
        bool generator = (flags & Engine.FlagGenerator) != 0;

        HandleAlerts(flags);

        _lblSignal.ForeColor = signal ? Green : ForeFaint;
        _lblLock.ForeColor = locked ? Green : freewheel || generator ? Amber : ForeFaint;
        _lblTx.ForeColor = tx ? (freewheel || generator ? Amber : Green) : ForeFaint;
        _lblPackets.Text = $"{engine.PacketsSent:N0} packets";

        if (tx || locked)
        {
            _lblTc.ForeColor = freewheel || generator ? Amber : ForeText;
            _lblTc.Text = frame.ToString();
            if (generator)
            {
                _lblRateInfo.Text = $"{RateName(rate)}   GENERATOR — internal clock";
                UpdateTray(2, $"Timecode Bridge — {frame} (generator)");
            }
            else
            {
                double fps = engine.MeasuredFps;
                _lblRateInfo.Text = fps > 0
                    ? $"{RateName(rate)}   measured {fps:0.00} fps{(freewheel ? "   FREEWHEEL" : "")}"
                    : RateName(rate);
                UpdateTray(freewheel ? 2 : 1, $"Timecode Bridge — {frame}{(freewheel ? " (freewheel)" : "")}");
            }
        }
        else
        {
            _lblTc.ForeColor = _outputDead ? Red : ForeFaint;
            _lblTc.Text = "--:--:--:--";
            _lblRateInfo.Text = signal ? "signal detected — locking…"
                : _outputDead ? "SIGNAL LOST — output stopped" : "no LTC signal";
            UpdateTray(_outputDead ? 3 : 0,
                _outputDead ? "Timecode Bridge — SIGNAL LOST" : "Timecode Bridge — no LTC signal");
        }

        _lblStatus.Text = engine.Status;

        // Peak meter with a smooth fall
        float peak = engine.ConsumePeak();
        _levelShown = peak > _levelShown ? peak : Math.Max(0, _levelShown - 0.06f);
        _levelBar.Invalidate();

        if (!engine.IsRunning)
        {
            // Fatal engine error (e.g. Art-Net socket failure) — tear down and show
            // why. A settings change (or app restart) brings it back.
            string status = engine.Status;
            _engine = null;
            Task.Run(engine.Dispose);
            _lblStatus.Text = status;
        }
    }
}
