using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Trayce;

/// <summary>Mirrors the prototype's "Trayce has quit" overlay with a Relaunch action.</summary>
internal sealed class QuitForm : Form
{
    public QuitForm(Action onRelaunch)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Trayce";
        Icon = AppIcon.Load();
        BackColor = UiPalette.Bg;
        ClientSize = new Size(360, 300);
        Padding = new Padding(Dpi.Scale(this, 1));

        var iconBox = new IconBadge { Size = new Size(Dpi.Scale(this, 46), Dpi.Scale(this, 46)), Location = new Point((ClientSize.Width - Dpi.Scale(this, 46)) / 2, Dpi.Scale(this, 32)) };
        Controls.Add(iconBox);

        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(Dpi.Scale(this, 20), Dpi.Scale(this, 92)),
            Size = new Size(ClientSize.Width - Dpi.Scale(this, 40), Dpi.Scale(this, 28)),
            Text = "Trayce has quit",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = UiFont.Px(16f, bold: true),
            ForeColor = UiPalette.Text
        });

        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(Dpi.Scale(this, 26), Dpi.Scale(this, 124)),
            Size = new Size(ClientSize.Width - Dpi.Scale(this, 52), Dpi.Scale(this, 80)),
            Text = "All tray icons have been removed. Trayce is no longer monitoring your APIs.",
            TextAlign = ContentAlignment.TopCenter,
            Font = UiFont.Px(12.5f),
            ForeColor = UiPalette.Text2
        });

        var relaunch = new RoundedButton("Relaunch Trayce") { Accent = true, Size = new Size(Dpi.Scale(this, 160), Dpi.Scale(this, 36)), Radius = 7, TextPx = 13f, Location = new Point((ClientSize.Width - Dpi.Scale(this, 160)) / 2, Dpi.Scale(this, 222)) };
        relaunch.Click += (_, _) => { onRelaunch(); Close(); };
        Controls.Add(relaunch);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindows11Corners(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiPalette.Border2);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private sealed class IconBadge : Control
    {
        public IconBadge() => DoubleBuffered = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiPalette.Backdrop(this, UiPalette.Bg));
            using var back = new SolidBrush(UiPalette.Control);
            UiPalette.FillRound(g, back, new Rectangle(0, 0, Width - 1, Height - 1), Dpi.Scale(this, 11));
            var size = Dpi.Scale(this, 22);
            IconPainter.Draw(g, "power", new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), UiPalette.Text2);
        }
    }
}
