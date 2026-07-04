using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TimecodeBridge.Ui;

/// <summary>Shared dark palette for the custom controls and the main window.</summary>
public static class Theme
{
    public static readonly Color Back = Color.FromArgb(15, 17, 21);
    public static readonly Color Field = Color.FromArgb(26, 29, 35);
    public static readonly Color Hairline = Color.FromArgb(40, 44, 52);
    public static readonly Color Text = Color.FromArgb(222, 226, 232);
    public static readonly Color Dim = Color.FromArgb(118, 126, 138);
    public static readonly Color Faint = Color.FromArgb(70, 76, 86);
    public static readonly Color Green = Color.FromArgb(52, 199, 123);
    public static readonly Color Amber = Color.FromArgb(229, 180, 66);
    public static readonly Color Red = Color.FromArgb(224, 82, 70);
}

/// <summary>
/// Fully dark drop-down list. The closed control is painted entirely by this class —
/// WM_PAINT never reaches the native renderer, so no stock chrome can ever flash
/// through (hover, focus or otherwise). List items are owner-drawn dark as well.
/// </summary>
public sealed class DarkCombo : ComboBox
{
    const int WmPaint = 0x000F;
    const int WmEraseBkgnd = 0x0014;

    bool _hover;
    Bitmap? _buffer;

    [DllImport("user32.dll")]
    static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    public DarkCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 18;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
    }

    // Drop-down list items (drawn in the popup list window).
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(inList && selected ? Theme.Hairline : Theme.Field);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Index >= 0)
        {
            var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? "", Font, bounds, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        base.OnDrawItem(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmPaint:
                // Validate the update region ourselves and paint the whole control —
                // the native painter never runs.
                ValidateRect(Handle, IntPtr.Zero);
                PaintClosed();
                return;
            case WmEraseBkgnd:
                m.Result = 1;
                return;
        }
        base.WndProc(ref m);
    }

    void PaintClosed()
    {
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;

        // Render to an off-screen buffer and blit once — direct GDI drawing
        // (clear → text → border) shimmers when repaints cascade. The buffer is
        // cached: hover repaints happen constantly and must not churn the GC.
        if (_buffer == null || _buffer.Width != r.Width || _buffer.Height != r.Height)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(r.Width, r.Height);
        }
        var bmp = _buffer;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Theme.Field);

            var textBounds = new Rectangle(5, 0, r.Width - 26, r.Height);
            TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, textBounds,
                Enabled ? Theme.Text : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // Hover is the only highlight state: focus must not leave a box looking
            // "stuck on" after clicking elsewhere (labels/form don't steal focus).
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.Width - 11, cy = r.Height / 2f - 1f;
            using (var chevron = new Pen(_hover ? Theme.Text : Theme.Dim, 1.6f))
                g.DrawLines(chevron, new[]
                {
                    new PointF(cx - 3.5f, cy - 1.5f),
                    new PointF(cx, cy + 2.2f),
                    new PointF(cx + 3.5f, cy - 1.5f),
                });

            g.SmoothingMode = SmoothingMode.None;
            using var border = new Pen(_hover ? Theme.Faint : Theme.Hairline);
            g.DrawRectangle(border, 0, 0, r.Width - 1, r.Height - 1);
        }

        using var screen = Graphics.FromHwnd(Handle);
        screen.DrawImageUnscaled(bmp, 0, 0);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
    protected override void OnDropDownClosed(EventArgs e) { Invalidate(); base.OnDropDownClosed(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer?.Dispose();
            _buffer = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Flat numeric stepper: [−] value [+] with typed entry, arrow-key and click
/// adjustment. Replaces NumericUpDown, whose native spinners fight a dark theme.
/// </summary>
public sealed class DarkStepper : Control
{
    readonly TextBox _text = new();
    readonly Label _minus = new();
    readonly Label _plus = new();
    int _min;
    int _max = 100;
    int _value;
    bool _syncing;

    public event EventHandler? ValueChanged;

    public DarkStepper()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Field;
        Size = new Size(60, 24);

        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = Theme.Field;
        _text.ForeColor = Theme.Text;
        _text.TextAlign = HorizontalAlignment.Center;
        _text.Text = "0";
        _text.TextChanged += (_, _) =>
        {
            if (!_syncing && int.TryParse(_text.Text, out int v))
                SetValue(v, updateText: false);
        };
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { SetValue(_value + 1, updateText: true); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { SetValue(_value - 1, updateText: true); e.Handled = true; e.SuppressKeyPress = true; }
        };
        _text.Leave += (_, _) => SyncText(); // normalise whatever was typed

        StyleGlyph(_minus, "−");
        StyleGlyph(_plus, "+");
        _minus.Click += (_, _) => SetValue(_value - 1, updateText: true);
        _plus.Click += (_, _) => SetValue(_value + 1, updateText: true);

        Controls.Add(_minus);
        Controls.Add(_text);
        Controls.Add(_plus);
    }

    static void StyleGlyph(Label l, string text)
    {
        l.Text = text;
        l.TextAlign = ContentAlignment.MiddleCenter;
        l.ForeColor = Theme.Dim;
        l.BackColor = Theme.Field;
        l.Cursor = Cursors.Hand;
        l.MouseEnter += (_, _) => l.ForeColor = Theme.Text;
        l.MouseLeave += (_, _) => l.ForeColor = Theme.Dim;
    }

    public int Minimum
    {
        get => _min;
        set { _min = value; if (_value < _min) SetValue(_min, updateText: true); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = value; if (_value > _max) SetValue(_max, updateText: true); }
    }

    public int Value
    {
        get => _value;
        set => SetValue(value, updateText: true);
    }

    void SetValue(int v, bool updateText)
    {
        v = Math.Clamp(v, _min, _max);
        bool changed = v != _value;
        _value = v;
        if (updateText) SyncText();
        if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void SyncText()
    {
        _syncing = true;
        _text.Text = _value.ToString();
        _syncing = false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int h = Height;
        _minus.SetBounds(1, 1, 18, h - 2);
        _plus.SetBounds(Width - 19, 1, 18, h - 2);
        _text.SetBounds(20, (h - _text.PreferredHeight) / 2 + 1, Width - 40, _text.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Theme.Field);
        using var line = new Pen(Theme.Hairline);
        g.DrawLine(line, 19, 4, 19, Height - 5);
        g.DrawLine(line, Width - 20, 4, Width - 20, Height - 5);
        g.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// Flat checkbox with an unmistakable checked state: empty hairline box when off,
/// solid accent-filled box with a dark tick when on. The stock flat checkbox draws
/// its glyph in colours too close to the border to read at a glance on dark UI.
/// </summary>
public sealed class DarkCheck : CheckBox
{
    bool _hover;

    public DarkCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Back;
        ForeColor = Theme.Dim;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Back);

        const int box = 14;
        int y = (Height - box) / 2;
        var rect = new Rectangle(0, y, box, box);

        if (Checked)
        {
            using var fill = new SolidBrush(Enabled ? Theme.Green : Theme.Faint);
            g.FillRectangle(fill, rect);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var tick = new Pen(Theme.Back, 1.8f);
            g.DrawLines(tick, new[]
            {
                new PointF(3f, y + 7f),
                new PointF(5.5f, y + 10f),
                new PointF(10.5f, y + 4f),
            });
            g.SmoothingMode = SmoothingMode.None;
        }
        else
        {
            using var fill = new SolidBrush(Theme.Field);
            g.FillRectangle(fill, rect);
            using var border = new Pen(_hover && Enabled ? Theme.Dim : Theme.Hairline);
            g.DrawRectangle(border, rect.X, rect.Y, box - 1, box - 1);
        }

        var textBounds = new Rectangle(box + 7, 0, Width - box - 7, Height);
        TextRenderer.DrawText(g, Text, Font, textBounds,
            !Enabled ? Theme.Faint : Checked ? Theme.Text : Theme.Dim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}

/// <summary>Borderless TextBox wrapped in a flat hairline frame.</summary>
public sealed class DarkTextField : Panel
{
    public TextBox Inner { get; } = new();

    public DarkTextField()
    {
        DoubleBuffered = true;
        BackColor = Theme.Field;
        Size = new Size(140, 24);
        Inner.BorderStyle = BorderStyle.None;
        Inner.BackColor = Theme.Field;
        Inner.ForeColor = Theme.Text;
        Controls.Add(Inner);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Inner.SetBounds(7, (Height - Inner.PreferredHeight) / 2 + 1, Width - 14, Inner.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
