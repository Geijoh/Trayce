using System.Windows.Forms;

namespace Trayce;

/// <summary>Custom translucent tray context menu mirroring the prototype (rounded, blurred, stroke-icon rows).</summary>
internal sealed class TrayMenu : Form
{
    public sealed record Item(string Label, string Glyph, Action OnClick, bool Checked = false, bool Separator = false, Color? Color = null)
    {
        public static readonly Item Sep = new("", "", () => { }, Separator: true);
    }

    public TrayMenu(IReadOnlyList<Item> items)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = UiPalette.Menu;
        Padding = new Padding(Dpi.Scale(this, 5));

        var width = MeasureWidth(items);
        var y = Dpi.Scale(this, 5);
        foreach (var item in items)
        {
            if (item.Separator)
            {
                Controls.Add(new MenuSeparator { Location = new Point(Dpi.Scale(this, 5), y), Size = new Size(width - Dpi.Scale(this, 10), Dpi.Scale(this, 11)) });
                y += Dpi.Scale(this, 11);
            }
            else
            {
                var row = new MenuItemRow(item) { Location = new Point(Dpi.Scale(this, 5), y), Size = new Size(width - Dpi.Scale(this, 10), Dpi.Scale(this, 34)) };
                row.Invoked += () => { Close(); item.OnClick(); };
                Controls.Add(row);
                y += Dpi.Scale(this, 34);
            }
        }

        ClientSize = new Size(width, y + Dpi.Scale(this, 5));
        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => Controls.OfType<MenuItemRow>().OrderBy(row => row.Top).FirstOrDefault()?.Focus();
    }

    private int MeasureWidth(IReadOnlyList<Item> items)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var font = UiFont.Px(13f);
        var max = 0f;
        foreach (var item in items.Where(i => !i.Separator))
        {
            max = Math.Max(max, g.MeasureString(item.Label, font).Width);
        }

        // icon(16) + gaps + label + checkmark room + paddings
        return Math.Max(Dpi.Scale(this, 224), (int)Math.Ceiling(max) + Dpi.Scale(this, 10 + 16 + 12 + 26 + 10 + 10));
    }

    public void ShowAt(Point anchor)
    {
        var screen = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X, screen.Left, screen.Right - Width);
        var y = Math.Clamp(anchor.Y - Height, screen.Top, screen.Bottom - Height);
        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindows11Corners(this);
        NativeChrome.ApplyWindows11Backdrop(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiPalette.Border2);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class MenuSeparator : Control
{
    public MenuSeparator() => DoubleBuffered = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Menu));
        using var pen = new Pen(UiPalette.Border);
        e.Graphics.DrawLine(pen, Dpi.Scale(this, 9), Height / 2, Width - Dpi.Scale(this, 9), Height / 2);
    }
}

internal sealed class MenuItemRow : Control
{
    private readonly TrayMenu.Item item;
    private bool hover;

    public event Action? Invoked;

    public MenuItemRow(TrayMenu.Item item)
    {
        this.item = item;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.MenuItem;
        AccessibleName = item.Label;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { hover = true; Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { hover = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnClick(EventArgs e) { Invoked?.Invoke(); base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Invoked?.Invoke();
        }
        else if (e.KeyCode is Keys.Up or Keys.Down)
        {
            FocusSibling(e.KeyCode == Keys.Down ? 1 : -1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    private void FocusSibling(int delta)
    {
        if (Parent is null) return;
        var rows = Parent.Controls.OfType<MenuItemRow>().OrderBy(row => row.Top).ToList();
        var index = rows.IndexOf(this);
        if (index < 0 || rows.Count == 0) return;
        rows[(index + delta + rows.Count) % rows.Count].Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Menu));

        if (hover)
        {
            using var back = new SolidBrush(UiPalette.ControlHover);
            UiPalette.FillRound(g, back, new Rectangle(Dpi.Scale(this, 1), Dpi.Scale(this, 1), Width - Dpi.Scale(this, 2), Height - Dpi.Scale(this, 2)), Dpi.Scale(this, 5));
        }

        var color = item.Color ?? UiPalette.Text;
        IconPainter.Draw(g, item.Glyph, new Rectangle(Dpi.Scale(this, 10), (Height - Dpi.Scale(this, 16)) / 2, Dpi.Scale(this, 16), Dpi.Scale(this, 16)), color);

        using var text = new SolidBrush(color);
        var font = UiFont.Px(13f);
        var size = g.MeasureString(item.Label, font);
        g.DrawString(item.Label, font, text, Dpi.Scale(this, 38), (Height - size.Height) / 2f);

        if (item.Checked)
        {
            IconPainter.Draw(g, "check", new Rectangle(Width - Dpi.Scale(this, 26), (Height - Dpi.Scale(this, 15)) / 2, Dpi.Scale(this, 15), Dpi.Scale(this, 15)), UiPalette.Accent);
        }
    }
}
