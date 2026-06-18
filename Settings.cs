using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Trayce;

/// <summary>Process-lifetime font cache keyed by CSS pixel size (converted to points).</summary>
internal static class UiFont
{
    private static readonly Dictionary<(int, bool, bool), Font> Cache = new();

    public static Font Px(float px, bool bold = false, bool mono = false)
    {
        var key = ((int)Math.Round(px * 4), bold, mono);
        if (!Cache.TryGetValue(key, out var font))
        {
            font = new Font(mono ? "Consolas" : "Segoe UI", px * 0.75f, bold ? FontStyle.Bold : FontStyle.Regular);
            Cache[key] = font;
        }

        return font;
    }
}

/// <summary>A text input with a rounded border that tracks focus, wrapping a borderless TextBox.</summary>
internal sealed class RoundedTextBox : Control
{
    public TextBox Box { get; }
    public event EventHandler? Edited;

    public RoundedTextBox(float px = 13f, bool mono = false, bool centered = false)
    {
        DoubleBuffered = true;
        BackColor = UiPalette.Control;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = UiPalette.Control,
            ForeColor = UiPalette.Text,
            Font = UiFont.Px(px, mono: mono),
            TextAlign = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left
        };
        Box.Enter += (_, _) => Invalidate();
        Box.Leave += (_, _) => Invalidate();
        Box.TextChanged += (_, _) => Edited?.Invoke(this, EventArgs.Empty);
        Controls.Add(Box);
        Height = 31;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }
    public bool Password { get => Box.UseSystemPasswordChar; set => Box.UseSystemPasswordChar = value; }
    public bool ReadOnlyBox { get => Box.ReadOnly; set => Box.ReadOnly = value; }
    public string Placeholder { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var inset = Dpi.Scale(this, 11);
        Box.SetBounds(inset, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - inset * 2), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(UiPalette.Control);
        using var border = new Pen(Box.Focused ? UiPalette.Accent2 : UiPalette.Border2);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        UiPalette.FillRound(g, back, rect, Dpi.Scale(this, 6));
        UiPalette.DrawRound(g, border, rect, Dpi.Scale(this, 6));
    }
}

/// <summary>A flat, rounded button with optional leading glyph, hover state, and dashed/accent variants.</summary>
internal sealed class RoundedButton : Control
{
    private bool hover;

    public Color Back { get; set; } = UiPalette.Control;
    public Color HoverBack { get; set; } = UiPalette.ControlHover;
    public Color BorderColor { get; set; } = UiPalette.Border2;
    public Color Foreground { get; set; } = UiPalette.Text;
    public int Radius { get; set; } = 6;
    public bool Dashed { get; set; }
    public bool Accent { get; set; }
    public bool Bordered { get; set; } = true;
    public string? Glyph { get; set; }
    public Color? GlyphColor { get; set; }
    public float TextPx { get; set; } = 12.5f;

    public RoundedButton(string text)
    {
        Text = text;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Height = 32;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Card));

        var fill = Accent ? UiPalette.Accent2 : hover && Enabled ? HoverBack : Back;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var back = new SolidBrush(fill)) UiPalette.FillRound(g, back, rect, Dpi.Scale(this, Radius));
        if (Bordered && !Accent)
        {
            using var pen = new Pen(BorderColor) { DashStyle = Dashed ? DashStyle.Dash : DashStyle.Solid };
            UiPalette.DrawRound(g, pen, rect, Dpi.Scale(this, Radius));
        }

        var font = UiFont.Px(TextPx, bold: true);
        var fore = Accent ? Color.White : Foreground;
        using var brush = new SolidBrush(fore);
        var textSize = g.MeasureString(Text, font);
        var glyphSize = Glyph is null ? 0 : Dpi.Scale(this, 15);
        var gap = Glyph is null ? 0 : Dpi.Scale(this, 7);
        var total = textSize.Width + glyphSize + gap;
        var x = (Width - total) / 2f;
        if (Glyph is not null)
        {
            IconPainter.Draw(g, Glyph, new Rectangle((int)x, (Height - glyphSize) / 2, glyphSize, glyphSize), GlyphColor ?? fore);
            x += glyphSize + gap;
        }

        g.DrawString(Text, font, brush, x, (Height - textSize.Height) / 2f);
    }
}

/// <summary>Light/Dark segmented pill. When disabled it still reflects the active (system) theme, dimmed.</summary>
internal sealed class SegmentedToggle : Control
{
    private readonly string[] options;

    public int SelectedIndex { get; private set; }
    public bool Disabled { get; set; }
    public event EventHandler? SelectionChanged;

    public SegmentedToggle(string[] options, int selected)
    {
        this.options = options;
        SelectedIndex = selected;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Height = 26;
        Width = options.Length * 56;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Disabled)
        {
            var seg = Math.Clamp(e.X / Math.Max(1, Width / options.Length), 0, options.Length - 1);
            if (seg != SelectedIndex)
            {
                SelectedIndex = seg;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Titlebar));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var back = new SolidBrush(UiPalette.Control)) UiPalette.FillRound(g, back, rect, Height / 2);
        using (var border = new Pen(UiPalette.Border2)) UiPalette.DrawRound(g, border, rect, Height / 2);

        var font = UiFont.Px(12.5f, bold: true);
        var segW = Width / options.Length;
        for (var i = 0; i < options.Length; i++)
        {
            var segRect = new Rectangle(i * segW + 2, 2, segW - 4, Height - 5);
            if (i == SelectedIndex)
            {
                using var fill = new SolidBrush(Disabled ? UiPalette.Border2 : UiPalette.Accent2);
                UiPalette.FillRound(g, fill, segRect, (Height - 5) / 2);
            }

            var color = i == SelectedIndex
                ? (Disabled ? UiPalette.Text2 : Color.White)
                : (Disabled ? UiPalette.Text3 : UiPalette.Text2);
            using var brush = new SolidBrush(color);
            var size = g.MeasureString(options[i], font);
            g.DrawString(options[i], font, brush, i * segW + (segW - size.Width) / 2f, (Height - size.Height) / 2f);
        }
    }
}

/// <summary>A rounded swatch + hex field that opens the color picker on click.</summary>
internal sealed class ColorField : Control
{
    private readonly string hex;
    private bool hover;

    public ColorField(string hex)
    {
        this.hex = hex;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Size = new Size(120, 31);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Card));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var back = new SolidBrush(hover && Enabled ? UiPalette.ControlHover : UiPalette.Control)) UiPalette.FillRound(g, back, rect, Dpi.Scale(this, 6));
        using (var border = new Pen(Enabled ? UiPalette.Border2 : UiPalette.Border)) UiPalette.DrawRound(g, border, rect, Dpi.Scale(this, 6));

        var sw = Dpi.Scale(this, 21);
        var swatch = new Rectangle(Dpi.Scale(this, 5), (Height - sw) / 2, sw, sw);
        Color color;
        try { color = ColorTranslator.FromHtml(hex); } catch { color = UiPalette.Accent2; }
        using (var fill = new SolidBrush(Enabled ? color : Color.FromArgb(140, color))) UiPalette.FillRound(g, fill, swatch, Dpi.Scale(this, 5));
        using (var border = new Pen(UiPalette.Border2)) UiPalette.DrawRound(g, border, swatch, Dpi.Scale(this, 5));

        using var text = new SolidBrush(Enabled ? UiPalette.Text : UiPalette.Text3);
        var font = UiFont.Px(12.5f, mono: true);
        var size = g.MeasureString(hex, font);
        g.DrawString(hex, font, text, swatch.Right + Dpi.Scale(this, 7), (Height - size.Height) / 2f);
    }
}

/// <summary>Compact sidebar row: badge, name, status dot + cadence, and a health percentage with mini bar.</summary>
internal sealed class ApiListItem : Control
{
    private readonly ApiConfig api;
    private readonly bool selected;
    private bool hover;

    public ApiListItem(ApiConfig api, bool selected)
    {
        this.api = api;
        this.selected = selected;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Height = 52;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    // Draw proportional to the control's actual height so nothing clips regardless of DPI scaling.
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Bg2));

        var s = Height / 52f;
        int S(float v) => (int)Math.Round(v * s);

        if (selected || hover)
        {
            using var back = new SolidBrush(UiPalette.ControlHover);
            UiPalette.FillRound(g, back, new Rectangle(0, 0, Width - 1, Height - 1), S(7));
        }

        if (selected)
        {
            using var accent = new SolidBrush(UiPalette.Accent);
            UiPalette.FillRound(g, accent, new Rectangle(S(1), S(10), S(3), Height - S(20)), S(3));
        }

        var badgeSize = S(30);
        var badge = new Rectangle(S(11), (Height - badgeSize) / 2, badgeSize, badgeSize);
        using (var badgeBrush = new SolidBrush(Brand.Color(api, UiPalette.Accent2)))
            UiPalette.FillRound(g, badgeBrush, badge, S(8));
        Logo.Draw(g, api, Rectangle.Inflate(badge, -S(5), -S(5)), Color.White, small: true);

        var textLeft = badge.Right + S(11);
        using var name = new SolidBrush(UiPalette.Text);
        using var muted = new SolidBrush(UiPalette.Text3);
        using var nameFont = new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString(api.DisplayName, nameFont, name, textLeft, S(8));

        var dotSize = S(7);
        var dot = new Rectangle(textLeft, S(30), dotSize, dotSize);
        using (var status = new SolidBrush(UsageMath.ColorFor(api.Usage ?? UsageSnapshot.Unknown(), false)))
            g.FillEllipse(status, dot);
        g.DrawString(PollText(api.PollSeconds), subFont, muted, dot.Right + S(5), S(28));

        var ratio = UsageMath.Ratio(api.Usage ?? UsageSnapshot.Unknown()) ?? 0m;
        var color = UsageMath.ColorForRatio(ratio);
        using var health = new SolidBrush(color);
        using var pctFont = new Font("Segoe UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var pct = Math.Round(ratio * 100m).ToString("0") + "%";
        var pctSize = g.MeasureString(pct, pctFont);
        g.DrawString(pct, pctFont, health, Width - pctSize.Width - S(14), S(9));

        var bar = new Rectangle(Width - S(14) - S(46), S(31), S(46), S(4));
        using var barBack = new SolidBrush(UiPalette.ControlHover);
        UiPalette.FillRound(g, barBack, bar, S(4));
        var fillWidth = (int)(bar.Width * Math.Clamp(ratio, 0m, 1m));
        if (ratio > 0m) fillWidth = Math.Max(S(5), fillWidth);
        UiPalette.FillRound(g, health, new Rectangle(bar.X, bar.Y, Math.Min(bar.Width, fillWidth), bar.Height), S(4));
    }

    private static string PollText(int seconds)
    {
        if (seconds % 3600 == 0) return "every " + seconds / 3600 + " " + (seconds == 3600 ? "hour" : "hours");
        if (seconds % 60 == 0) return "every " + seconds / 60 + " " + (seconds == 60 ? "minute" : "minutes");
        return "every " + seconds + " " + (seconds == 1 ? "second" : "seconds");
    }
}

internal sealed class SettingsForm : Form
{
    private readonly List<ApiConfig> apis;
    private readonly Action? onSaved;
    private Panel sidebarList = new();
    private Panel editor = new();
    private Label footerStatus = new();
    private string selectedId;
    private ThemeMode themeMode;
    private bool dirty;
    private bool saved;
    private bool revealKey;
    private bool rendering;

    private int S(int value) => Dpi.Scale(this, value);
    private Point P(int x, int y) => new(S(x), S(y));
    private Size Z(int width, int height) => new(S(width), S(height));
    private Padding G(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));

    public SettingsForm(TrayceConfig config, string? selectedApiId = null, Action? onSaved = null)
    {
        this.onSaved = onSaved;
        themeMode = SystemTheme.Parse(config.Theme);
        Text = "Trayce — Settings";
        Icon = AppIcon.Load();
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = new Size(S(840), S(560));
        ClientSize = new Size(940, 680);
        BackColor = UiPalette.Bg;
        Font = UiFont.Px(13f);
        KeyPreview = true;

        apis = config.Apis.Count == 0
            ? new List<ApiConfig> { NewApi("api") }
            : config.Apis.Select(Clone).ToList();
        selectedId = apis.Any(a => string.Equals(a.Id, selectedApiId, StringComparison.OrdinalIgnoreCase))
            ? selectedApiId!
            : apis[0].Id;

        BuildAll();

        UiPalette.Changed += OnThemeChanged;
        FormClosed += (_, _) => UiPalette.Changed -= OnThemeChanged;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) return;
            RenderSidebar();
            RenderEditor();
        };
        Shown += (_, _) => ActiveControl = null;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed) return;
            SuspendLayout();
            Controls.Clear();
            BackColor = UiPalette.Bg;
            BuildAll();
            ResumeLayout();
        }));
    }

    private void BuildAll()
    {
        sidebarList = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiPalette.Bg2, Padding = G(8, 0, 8, 8) };
        editor = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiPalette.Bg };
        footerStatus = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(BuildShell());
        if (UiPalette.IsDark)
        {
            NativeChrome.ApplyDarkScrollbars(editor);
            NativeChrome.ApplyDarkScrollbars(sidebarList);
        }
        RenderSidebar();
        RenderEditor();
        UpdateFooter();
    }

    protected override void WndProc(ref Message m)
    {
        const int wmNcHitTest = 0x84;
        const int htClient = 1;
        const int htLeft = 10, htRight = 11, htTop = 12, htTopLeft = 13, htTopRight = 14, htBottom = 15, htBottomLeft = 16, htBottomRight = 17;

        base.WndProc(ref m);
        if (m.Msg != wmNcHitTest || (int)m.Result != htClient || WindowState == FormWindowState.Maximized) return;

        var p = PointToClient(Cursor.Position);
        var grip = Dpi.Scale(this, 6);
        var left = p.X <= grip;
        var right = p.X >= ClientSize.Width - grip;
        var top = p.Y <= grip;
        var bottom = p.Y >= ClientSize.Height - grip;

        if (left && top) m.Result = htTopLeft;
        else if (right && top) m.Result = htTopRight;
        else if (left && bottom) m.Result = htBottomLeft;
        else if (right && bottom) m.Result = htBottomRight;
        else if (left) m.Result = htLeft;
        else if (right) m.Result = htRight;
        else if (top) m.Result = htTop;
        else if (bottom) m.Result = htBottom;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindows11Corners(this);
        NativeChrome.ApplyWindows11Backdrop(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed) return;
            SuspendLayout();
            Controls.Clear();
            BuildAll();
            ResumeLayout();
        }));
    }

    private Control BuildShell()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiPalette.Bg };
        body.Controls.Add(editor);
        body.Controls.Add(BuildSidebar());

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = UiPalette.Bg, ColumnCount = 1, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, S(46)));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, S(56)));
        shell.Controls.Add(BuildTitleBar(), 0, 0);
        shell.Controls.Add(body, 0, 1);
        shell.Controls.Add(BuildFooter(), 0, 2);
        return shell;
    }

    private Control BuildTitleBar()
    {
        var title = new Panel { Dock = DockStyle.Fill, Height = S(46), BackColor = UiPalette.Titlebar };
        title.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) NativeChrome.DragWindow(Handle); };

        title.Controls.Add(new AppMark { Location = P(14, 12), Size = Z(18, 18) });
        title.Controls.Add(new Label
        {
            AutoSize = false,
            Location = P(41, 0),
            Size = Z(168, 46),
            Text = "Trayce — Settings",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiPalette.Text2,
            Font = UiFont.Px(12.5f)
        });

        var close = new TitleGlyphButton("close") { Size = Z(46, 46) };
        close.Click += (_, _) => Close();
        var max = new TitleGlyphButton("max") { Size = Z(46, 46) };
        max.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        var min = new TitleGlyphButton("min") { Size = Z(46, 46) };
        min.Click += (_, _) => WindowState = FormWindowState.Minimized;

        // theme cluster (mirrors the prototype's floating Light/Dark toggle + a Match-system switch)
        var matchOn = UiPalette.Mode == ThemeMode.System;
        var segment = new SegmentedToggle(new[] { "Light", "Dark" }, UiPalette.IsDark ? 1 : 0) { Disabled = matchOn, Size = Z(112, 26) };
        segment.SelectionChanged += (_, _) => ApplyTheme(segment.SelectedIndex == 0 ? ThemeMode.Light : ThemeMode.Dark);
        var matchToggle = new ToggleSwitch(matchOn) { Size = Z(34, 18) };
        matchToggle.Click += (_, _) => ApplyTheme(UiPalette.Mode == ThemeMode.System ? (UiPalette.IsDark ? ThemeMode.Dark : ThemeMode.Light) : ThemeMode.System);
        var matchLabel = new Label { AutoSize = false, Text = "Match system", TextAlign = ContentAlignment.MiddleRight, ForeColor = UiPalette.Text2, Font = UiFont.Px(11.5f), Size = Z(104, 46) };
        var about = new RoundedButton("About") { Glyph = "info", Size = Z(84, 28), TextPx = 12f, Back = UiPalette.Control, Foreground = UiPalette.Text2, BorderColor = UiPalette.Border2 };
        about.Click += (_, _) => new AboutForm().ShowDialog(this);

        var traySeg = new SegmentedToggle(new[] { "Bars", "Ring", "Minimal" }, (int)UiPalette.Tray) { Size = Z(156, 26) };
        traySeg.SelectionChanged += (_, _) => ApplyTrayStyle((TrayStyle)traySeg.SelectedIndex);
        var trayLabel = new Label { AutoSize = false, Text = "Tray", TextAlign = ContentAlignment.MiddleRight, ForeColor = UiPalette.Text2, Font = UiFont.Px(11.5f), Size = Z(34, 46) };

        title.Controls.Add(trayLabel);
        title.Controls.Add(traySeg);
        title.Controls.Add(matchLabel);
        title.Controls.Add(about);
        title.Controls.Add(matchToggle);
        title.Controls.Add(segment);
        title.Controls.Add(min);
        title.Controls.Add(max);
        title.Controls.Add(close);
        title.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = UiPalette.Border });

        void LayoutRight()
        {
            var w = title.ClientSize.Width;
            if (w <= 0) return;
            close.Location = new Point(w - S(46), 0);
            max.Location = new Point(w - S(92), 0);
            min.Location = new Point(w - S(138), 0);
            var h = title.ClientSize.Height;
            segment.Location = new Point(w - S(138) - S(12) - segment.Width, (h - segment.Height) / 2);
            matchToggle.Location = new Point(segment.Left - S(12) - matchToggle.Width, (h - matchToggle.Height) / 2);
            matchLabel.Location = new Point(matchToggle.Left - matchLabel.Width - S(2), 0);
            about.Visible = w >= S(920);
            about.Location = new Point(matchLabel.Left - S(12) - about.Width, (h - about.Height) / 2);
            var trayRight = about.Visible ? about.Left - S(14) : matchLabel.Left - S(14);
            traySeg.Location = new Point(trayRight - traySeg.Width, (h - traySeg.Height) / 2);
            trayLabel.Location = new Point(traySeg.Left - trayLabel.Width - S(2), 0);
        }

        title.Layout += (_, _) => LayoutRight();
        LayoutRight();
        return title;
    }

    private void ApplyTheme(ThemeMode mode)
    {
        themeMode = mode;
        ConfigStore.SaveTheme(mode);
        UiPalette.Apply(mode); // fires UiPalette.Changed -> OnThemeChanged rebuilds the window
    }

    private void ApplyTrayStyle(TrayStyle style)
    {
        ConfigStore.SaveTrayStyle(style);
        UiPalette.ApplyTray(style); // fires UiPalette.Changed -> rebuilds the window + re-renders tray icons
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = S(286), BackColor = UiPalette.Bg2 };
        sidebar.Controls.Add(sidebarList);
        sidebar.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = S(38),
            Padding = G(14, 16, 0, 0),
            Text = "TRACKED APIS",
            ForeColor = UiPalette.Text3,
            Font = UiFont.Px(11f, bold: true)
        });

        var add = new RoundedButton("Add API") { Glyph = "plus", Back = UiPalette.Control, Foreground = UiPalette.Text, Dock = DockStyle.Fill, Height = S(36), BorderColor = UiPalette.Border2 };
        add.Click += (_, _) =>
        {
            using var picker = new PresetPickerForm();
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var api = picker.Preset is { } preset ? PresetApi(UniqueId(Slug(preset.Name)), preset) : NewApi(UniqueId("api"));
            apis.Add(api);
            selectedId = api.Id;
            MarkDirty();
            RenderSidebar();
            RenderEditor();
        };
        var addWrap = new Panel { Dock = DockStyle.Bottom, Height = S(56), Padding = G(12, 8, 12, 12), BackColor = UiPalette.Bg2 };
        addWrap.Controls.Add(add);
        sidebar.Controls.Add(addWrap);
        sidebar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(1), BackColor = UiPalette.Border });
        return sidebar;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, Height = S(56), BackColor = UiPalette.Titlebar };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(1), BackColor = UiPalette.Border });

        footerStatus.Location = P(16, 1);
        footerStatus.Size = Z(340, 55);
        footerStatus.Font = UiFont.Px(12.5f);

        var save = new RoundedButton("Save") { Accent = true, Size = Z(76, 32) };
        save.Click += (_, _) => Save();
        var cancel = new RoundedButton("Cancel") { Size = Z(82, 32) };
        cancel.Click += (_, _) => Close();

        footer.Controls.Add(footerStatus);
        footer.Controls.Add(cancel);
        footer.Controls.Add(save);

        void LayoutRight()
        {
            var w = footer.ClientSize.Width;
            if (w <= 0) return;
            save.Location = new Point(w - S(16) - save.Width, S(11));
            cancel.Location = new Point(save.Left - S(9) - cancel.Width, S(11));
        }

        footer.Layout += (_, _) => LayoutRight();
        LayoutRight();
        return footer;
    }

    private void RenderSidebar()
    {
        sidebarList.SuspendLayout();
        sidebarList.Controls.Clear();
        var y = S(4);
        foreach (var api in apis)
        {
            var item = new ApiListItem(api, string.Equals(api.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                Location = new Point(S(4), y),
                Size = new Size(sidebarList.ClientSize.Width - S(16), S(52)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            item.Click += (_, _) =>
            {
                selectedId = api.Id;
                revealKey = false;
                RenderSidebar();
                RenderEditor();
            };
            sidebarList.Controls.Add(item);
            y += S(56);
        }

        sidebarList.ResumeLayout();
    }

    private void RenderEditor()
    {
        if (rendering) return;
        rendering = true;
        try
        {
            editor.SuspendLayout();
            editor.Controls.Clear();
            var api = SelectedApi();
            var width = Math.Max(S(420), editor.ClientSize.Width - S(48) - (editor.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            var y = S(20);

            Section("Identity", ref y, width, Card(width,
                Row("API name", null, TextField(api.DisplayName, v => { api.DisplayName = v; MarkDirty(); RenderSidebar(); }, 260)),
                Row("Provider / service type", null, TextField(api.Provider ?? "", v => { api.Provider = v; MarkDirty(); }, 260, placeholder: "Optional")),
                LogoRow(api)));

            Section("Appearance", ref y, width, Card(width,
                BrandRow(api),
                ToggleRow(api),
                CustomColorRow(api),
                TrayPreviewRow(api)));

            Section("Connection", ref y, width, Card(width,
                ApiKeyRow(api),
                Row("Usage endpoint URL", null, TextField(api.SourceUrl ?? "", v => { api.SourceUrl = v; MarkDirty(); }, 330, mono: true, placeholder: "https://api.example.com/usage")),
                PollRow(api)));

            UsageLimits(api, ref y, width);

            var json = new RoundedButton("Open config JSON") { Glyph = "code", Bordered = false, Back = Color.Transparent, Foreground = UiPalette.Accent, GlyphColor = UiPalette.Accent, TextPx = 12.5f, Location = new Point(S(24), y), Size = Z(180, 26) };
            json.Click += (_, _) => new JsonPreviewForm(new TrayceConfig { Apis = apis.Select(Clone).ToList() }).ShowDialog(this);
            editor.Controls.Add(json);
            y += S(56);

            editor.AutoScrollMinSize = new Size(0, y);
        }
        finally
        {
            editor.ResumeLayout();
            rendering = false;
        }
    }

    private void Section(string title, ref int y, int width, Control card)
    {
        editor.Controls.Add(new Label { AutoSize = true, Location = new Point(S(24), y), Text = title, ForeColor = UiPalette.Text, Font = UiFont.Px(13f, bold: true) });
        y += S(24);
        card.Location = new Point(S(24), y);
        editor.Controls.Add(card);
        y += card.Height + S(18);
    }

    private Control Card(int width, params Control[] rows)
    {
        var height = rows.Sum(r => r.Height) + Math.Max(0, rows.Length - 1) * S(1);
        var card = new RoundedPanel { Size = new Size(width, height), Back = UiPalette.Card, Stroke = UiPalette.Border, Radius = 8 };
        var top = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Location = new Point(0, top);
            rows[i].Width = width;
            rows[i].Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            card.Controls.Add(rows[i]);
            top += rows[i].Height;
            if (i == rows.Length - 1) continue;
            card.Controls.Add(new Panel { Location = new Point(0, top), Size = new Size(width, S(1)), BackColor = UiPalette.Border, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top });
            top += S(1);
        }

        return card;
    }

    private Control Row(string title, string? subtitle, Control right, bool muted = false)
    {
        var leftHeight = subtitle is null ? S(20) : S(38);
        var height = Math.Max(right.Height, leftHeight) + S(subtitle is null ? 18 : 20);
        var row = new Panel { Height = height, BackColor = Color.Transparent };
        var titleLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Location = P(16, subtitle is null ? 12 : 9),
            Size = Z(250, 20),
            Text = title,
            ForeColor = muted ? UiPalette.Text3 : UiPalette.Text,
            Font = UiFont.Px(13f)
        };
        row.Controls.Add(titleLabel);
        Label? subtitleLabel = null;
        if (subtitle is not null)
        {
            subtitleLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Location = P(16, 29),
                Size = Z(250, 16),
                Text = subtitle,
                ForeColor = UiPalette.Text3,
                Font = UiFont.Px(11f)
            };
            row.Controls.Add(subtitleLabel);
        }

        right.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        void Place()
        {
            var maxLeft = row.Width - right.Width - S(16);
            var preferred = S(290);
            var left = Math.Max(S(210), Math.Min(maxLeft, preferred));
            right.Location = new Point(left, (row.Height - right.Height) / 2);
            var labelWidth = Math.Max(S(150), right.Left - titleLabel.Left - S(24));
            titleLabel.Width = labelWidth;
            if (subtitleLabel is not null) subtitleLabel.Width = labelWidth;
        }
        row.Controls.Add(right);
        row.Resize += (_, _) => Place();
        Place();
        return row;
    }

    private Control LogoRow(ApiConfig api)
    {
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Size = Z(258, 35) };
        wrap.Controls.Add(new LogoBadge(api) { Size = Z(34, 34), Radius = 8, Margin = G(0, 0, 10, 0) });
        var initials = TextField(api.LogoText ?? Logo.Initials(api.DisplayName), v => { api.LogoText = v[..Math.Min(3, v.Length)]; MarkDirty(); RenderSidebar(); }, 54, centered: true);
        initials.Margin = G(0, 0, 10, 0);
        wrap.Controls.Add(initials);
        var choose = new RoundedButton("Choose image…") { Size = Z(150, 31), Margin = G(0, 1, 0, 0) };
        choose.Click += (_, _) => ChooseLogo(api);
        wrap.Controls.Add(choose);
        return Row("Logo", "Image, or falls back to initials", wrap);
    }

    private Control BrandRow(ApiConfig api)
    {
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Size = Z(130, 30) };
        wrap.Controls.Add(new ColorSwatch(BrandColor(api)) { Size = Z(24, 24), Margin = G(0, 3, 9, 0) });
        wrap.Controls.Add(new Label { Text = BrandColor(api), AutoSize = false, Size = Z(96, 30), TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiPalette.Text2, Font = UiFont.Px(12.5f, mono: true) });
        return Row("Brand color", "Provided default", wrap);
    }

    private Control ToggleRow(ApiConfig api)
    {
        var toggle = new ToggleSwitch(api.UseBrandColor) { Size = Z(40, 21) };
        toggle.Click += (_, _) =>
        {
            api.UseBrandColor = !api.UseBrandColor;
            MarkDirty();
            RenderEditor();
            RenderSidebar();
        };
        return Row("Use brand color for tray", null, toggle);
    }

    private Control CustomColorRow(ApiConfig api)
    {
        var current = (api.CustomColor ?? api.BrandColor ?? "#0078D4").ToUpperInvariant();
        var field = new ColorField(current) { Size = Z(120, 31) };
        field.Enabled = !api.UseBrandColor;
        field.Click += (_, _) =>
        {
            using var picker = new ColorPickerForm(api.CustomColor ?? api.BrandColor ?? "#0078D4", api.BrandColor ?? "#0078D4");
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            api.CustomColor = picker.SelectedColor;
            api.UseBrandColor = false;
            MarkDirty();
            RenderEditor();
            RenderSidebar();
        };
        return Row("Custom tray color", "Overrides the brand default", field, muted: api.UseBrandColor);
    }

    private Control TrayPreviewRow(ApiConfig api) =>
        Row("Tray preview", "Status colors override when warning or critical", new TrayPreview(api) { Size = Z(58, 34) });

    private Control ApiKeyRow(ApiConfig api)
    {
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Size = Z(294, 31) };
        var key = TextField(api.ApiKey ?? "", v => { api.ApiKey = v; MarkDirty(); }, 220, mono: true);
        key.Password = !revealKey;
        key.Margin = G(0, 0, 6, 0);
        wrap.Controls.Add(key);
        var reveal = new GlyphButton { Glyph = "eye", Size = Z(31, 31), Margin = G(0, 0, 6, 0) };
        reveal.Click += (_, _) => { revealKey = !revealKey; RenderEditor(); };
        wrap.Controls.Add(reveal);
        var copy = new GlyphButton { Glyph = "copy", Size = Z(31, 31) };
        copy.Click += (_, _) => { Clipboard.SetText(api.ApiKey ?? ""); Toaster.Show(this, "API key copied"); };
        wrap.Controls.Add(copy);
        return Row("API key", "Stored in Windows Credential Manager - config JSON keeps metadata only.", wrap);
    }

    private Control PollRow(ApiConfig api)
    {
        var parts = PollParts(api.PollSeconds);
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Size = Z(282, 31) };
        var seconds = new Label { AutoSize = false, Size = Z(78, 31), TextAlign = ContentAlignment.MiddleLeft, Text = "= " + api.PollSeconds + " s", ForeColor = UiPalette.Text3, Font = UiFont.Px(12f), Margin = G(9, 0, 0, 0) };
        var num = TextField(parts.Number.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) api.PollSeconds = PollSeconds(Math.Max(1, n), PollParts(api.PollSeconds).Unit);
            seconds.Text = "= " + api.PollSeconds + " s";
            MarkDirty();
            RenderSidebar();
        }, 66);
        num.Margin = G(0, 0, 9, 0);
        var units = Combo(new[] { "seconds", "minutes", "hours" }, parts.Unit);
        units.SelectedIndexChanged += (_, _) =>
        {
            if (int.TryParse(num.Text, out var n)) api.PollSeconds = PollSeconds(Math.Max(1, n), units.Text);
            seconds.Text = "= " + api.PollSeconds + " s";
            MarkDirty();
            RenderSidebar();
        };
        wrap.Controls.Add(num);
        wrap.Controls.Add(units);
        wrap.Controls.Add(seconds);
        return Row("Poll cadence", "How often Trayce refreshes usage", wrap);
    }

    private void UsageLimits(ApiConfig api, ref int y, int width)
    {
        var header = new Panel { Location = new Point(S(24), y), Size = new Size(width, S(28)), BackColor = Color.Transparent };
        header.Controls.Add(new Label { AutoSize = true, Location = P(0, 2), Text = "Usage limits", ForeColor = UiPalette.Text, Font = UiFont.Px(13f, bold: true) });
        var add = new RoundedButton("Add window") { Glyph = "plus", Size = Z(116, 28), TextPx = 12f, Anchor = AnchorStyles.Right | AnchorStyles.Top, Location = new Point(width - S(116), 0) };
        add.Click += (_, _) =>
        {
            api.Usage ??= new UsageSnapshot();
            api.Usage.Windows.Add(new UsageWindow { Label = "New window", Metric = "requests", Used = 0, Limit = 1000 });
            MarkDirty();
            RenderEditor();
        };
        header.Controls.Add(add);
        editor.Controls.Add(header);
        y += S(37);

        api.Usage ??= new UsageSnapshot();
        foreach (var window in api.Usage.Windows.ToList())
        {
            var card = LimitEditor(api, window, width);
            card.Location = new Point(S(24), y);
            editor.Controls.Add(card);
            y += card.Height + S(11);
        }

        y += S(9);
    }

    private Control LimitEditor(ApiConfig api, UsageWindow window, int width)
    {
        var card = new RoundedPanel { Size = new Size(width, S(116)), Back = UiPalette.Card, Stroke = UiPalette.Border, Radius = 8 };

        var label = TextField(window.Label, v => { window.Label = v; MarkDirty(); }, 108);
        label.Location = P(14, 13);
        var metric = Combo(new[] { "tokens", "requests", "calls", "dollars" }, window.Metric ?? "requests");
        metric.Location = P(130, 13);
        metric.SelectedIndexChanged += (_, _) => { window.Metric = metric.Text; MarkDirty(); RenderEditor(); };

        var ratio = window.Limit is > 0 ? Math.Clamp(window.Used / window.Limit.Value, 0m, 1m) : 0m;
        var pctLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(width - S(88), S(17)), Size = Z(44, 20), Text = Math.Round((window.Limit is > 0 ? window.Used / window.Limit.Value : 0m) * 100).ToString("0") + "%", ForeColor = UsageMath.ColorForRatio(ratio), Font = UiFont.Px(13f, bold: true) };
        var delete = new GlyphButton { Glyph = "trash", Location = new Point(width - S(44), S(13)), Size = Z(30, 30), GlyphColor = UiPalette.Text3 };
        delete.Click += (_, _) => { api.Usage?.Windows.Remove(window); MarkDirty(); RenderEditor(); };

        var bar = new ProgressStrip(window.Limit is > 0 ? window.Used / window.Limit.Value : 0m) { Location = P(14, 50), Size = new Size(width - S(28), S(6)) };

        var used = LabeledField("Used", Format.Decimal(window.Used), v => { if (decimal.TryParse(v, out var n)) { window.Used = n; MarkDirty(); } }, 92);
        used.Location = P(14, 67);
        var limit = LabeledField("Limit", Format.Decimal(window.Limit ?? 0), v => { if (decimal.TryParse(v, out var n)) { window.Limit = n; MarkDirty(); RenderEditor(); } }, 92);
        limit.Location = P(116, 67);
        var reset = LabeledField("Resets", Format.Reset(window.ResetsAt), v => { window.ResetsAt = ParseReset(v); MarkDirty(); }, 110);
        reset.Location = P(218, 67);
        var note = LabeledField("Note", window.Message ?? "", v => { window.Message = v; MarkDirty(); }, Math.Max(S(120), width - S(360)), scaledWidth: true);
        note.Location = P(340, 67);

        card.Controls.Add(label);
        card.Controls.Add(metric);
        card.Controls.Add(pctLabel);
        card.Controls.Add(delete);
        card.Controls.Add(bar);
        card.Controls.Add(used);
        card.Controls.Add(limit);
        card.Controls.Add(reset);
        card.Controls.Add(note);
        return card;
    }

    private Control LabeledField(string title, string value, Action<string> set, int width, bool scaledWidth = false)
    {
        var w = scaledWidth ? width : S(width);
        var wrap = new Panel { Size = new Size(w, S(46)), BackColor = Color.Transparent };
        wrap.Controls.Add(new Label { Location = Point.Empty, Size = new Size(w, S(13)), Text = title.ToUpperInvariant(), ForeColor = UiPalette.Text3, Font = UiFont.Px(10.5f, bold: true) });
        var field = TextField(value, set, w, scaledWidth: true);
        field.Size = new Size(w, S(29));
        field.Location = new Point(0, S(16));
        wrap.Controls.Add(field);
        return wrap;
    }

    private RoundedTextBox TextField(string value, Action<string> set, int width = 260, bool mono = false, bool centered = false, bool scaledWidth = false, string? placeholder = null)
    {
        var field = new RoundedTextBox(mono: mono, centered: centered) { Size = new Size(scaledWidth ? width : S(width), S(31)) };
        field.Text = value;
        if (placeholder is not null) field.Placeholder = placeholder;
        field.Edited += (_, _) => { if (!rendering) set(field.Text); };
        return field;
    }

    private ComboBox Combo(IEnumerable<string> items, string selected)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = S(120),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiPalette.Control,
            ForeColor = UiPalette.Text,
            Font = UiFont.Px(12.5f),
            IntegralHeight = false
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.SelectedItem = combo.Items.Contains(selected) ? selected : combo.Items[0];
        return combo;
    }

    private void ChooseLogo(ApiConfig api)
    {
        using var dialog = new LogoPickerForm(api);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPath is null) return;
        try
        {
            api.LogoPath = ConfigStore.ImportLogo(api.Id, dialog.SelectedPath);
            MarkDirty();
            RenderEditor();
            RenderSidebar();
            Toaster.Show(this, "Logo updated");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Trayce", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Save()
    {
        var errors = CollectErrors();
        if (errors.Count > 0)
        {
            using var invalid = new ValidationForm(errors);
            invalid.ShowDialog(this);
            return;
        }

        try
        {
            ConfigStore.Save(new TrayceConfig { Apis = apis.Select(Clone).ToList(), Theme = SystemTheme.ToConfig(themeMode), TrayStyle = TrayStyles.ToConfig(UiPalette.Tray) });
            dirty = false;
            saved = true;
            UpdateFooter();
            onSaved?.Invoke();
            Toaster.Show(this, "Settings saved");
        }
        catch (Exception ex)
        {
            using var dialog = new ValidationForm(ex.Message);
            dialog.ShowDialog(this);
        }
    }

    private List<string> CollectErrors()
    {
        var errors = new List<string>();
        var api = SelectedApi();
        if (string.IsNullOrWhiteSpace(api.DisplayName)) errors.Add("API name can’t be empty.");
        foreach (var window in api.Usage?.Windows ?? new List<UsageWindow>())
        {
            var label = string.IsNullOrWhiteSpace(window.Label) ? "window" : window.Label;
            if (!(window.Limit is > 0)) errors.Add($"The limit for “{label}” must be greater than zero.");
            if (window.Used < 0) errors.Add($"The used value for “{label}” can’t be negative.");
        }

        return errors;
    }

    private void MarkDirty()
    {
        dirty = true;
        saved = false;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        footerStatus.Text = saved ? "All changes saved" : dirty ? "Unsaved changes" : "No unsaved changes";
        footerStatus.ForeColor = saved ? UiPalette.Ok : dirty ? UiPalette.Text2 : UiPalette.Text2;
        footerStatus.Image = saved ? IconPainter.Bitmap("check", UiPalette.Ok) : null;
        footerStatus.ImageAlign = ContentAlignment.MiddleLeft;
        footerStatus.TextAlign = ContentAlignment.MiddleLeft;
        footerStatus.Padding = G(saved ? 22 : 0, 0, 0, 0);
    }

    private ApiConfig SelectedApi() => apis.FirstOrDefault(a => string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? apis[0];
    private string BrandColor(ApiConfig api) => (api.BrandColor ?? "#0078D4").ToUpperInvariant();

    private string UniqueId(string seed)
    {
        var i = 1;
        var candidate = seed;
        while (apis.Any(a => string.Equals(a.Id, candidate, StringComparison.OrdinalIgnoreCase))) candidate = seed + "-" + i++;
        return candidate;
    }

    private static DateTimeOffset? ParseReset(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static ApiConfig NewApi(string id) => new()
    {
        Id = id,
        DisplayName = "New API",
        Provider = "Custom API",
        LogoText = "API",
        BrandColor = "#0078D4",
        UseBrandColor = true,
        PollSeconds = 300,
        Usage = new UsageSnapshot { Windows = new List<UsageWindow> { new() { Label = "Daily", Metric = "requests", Used = 0, Limit = 1000 } } }
    };

    private static ApiConfig PresetApi(string id, ApiPreset preset) => new()
    {
        Id = id,
        DisplayName = preset.Name,
        Provider = preset.Provider,
        LogoPath = preset.IconPath,
        LogoText = preset.Initials,
        BrandColor = preset.BrandColor,
        UseBrandColor = true,
        PollSeconds = 300,
        Usage = new UsageSnapshot { Windows = new List<UsageWindow> { new() { Label = "Daily", Metric = "requests", Used = 0, Limit = 1000 } } }
    };

    private static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(slug) ? "api" : slug;
    }

    private static (int Number, string Unit) PollParts(int seconds)
    {
        if (seconds % 3600 == 0) return (Math.Max(1, seconds / 3600), "hours");
        if (seconds % 60 == 0) return (Math.Max(1, seconds / 60), "minutes");
        return (Math.Max(1, seconds), "seconds");
    }

    private static int PollSeconds(int number, string unit) => unit switch
    {
        "hours" => number * 3600,
        "minutes" => number * 60,
        _ => number
    };

    private static ApiConfig Clone(ApiConfig api) => new()
    {
        Id = api.Id,
        DisplayName = api.DisplayName,
        Provider = api.Provider,
        LogoPath = api.LogoPath,
        LogoText = api.LogoText,
        BrandColor = api.BrandColor,
        UseBrandColor = api.UseBrandColor,
        CustomColor = api.CustomColor,
        ApiKey = api.ApiKey,
        SourceUrl = api.SourceUrl,
        PollSeconds = api.PollSeconds,
        Usage = api.Usage?.Clone()
    };
}
