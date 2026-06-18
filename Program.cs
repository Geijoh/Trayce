using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Trayce;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--self-test"))
        {
            SelfTest.Run();
            return 0;
        }

        var previewIndex = Array.IndexOf(args, "--render-preview");
        if (previewIndex >= 0)
        {
            PreviewRenderer.Render(previewIndex + 1 < args.Length ? args[previewIndex + 1] : "trayce-preview.png");
            return 0;
        }

        var settingsPreviewIndex = Array.IndexOf(args, "--render-settings-preview");
        if (settingsPreviewIndex >= 0)
        {
            PreviewRenderer.RenderSettings(settingsPreviewIndex + 1 < args.Length ? args[settingsPreviewIndex + 1] : "trayce-settings-preview.png");
            return 0;
        }

        var renderAllIndex = Array.IndexOf(args, "--render-all");
        if (renderAllIndex >= 0)
        {
            PreviewRenderer.RenderAll(renderAllIndex + 1 < args.Length ? args[renderAllIndex + 1] : "out");
            return 0;
        }

        // Real on-screen capture (reflects actual DPI scaling, unlike DrawToBitmap).
        var shotIndex = Array.IndexOf(args, "--shot");
        if (shotIndex >= 0)
        {
            LiveShot.Settings(shotIndex + 1 < args.Length ? args[shotIndex + 1] : "shot.png");
            return 0;
        }

        if (args.Contains("--dpi-probe"))
        {
            LiveShot.DpiProbe();
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\Trayce", out var created);
        if (!created) return 0;

        Application.Run(new TrayceContext());
        return 0;
    }
}

internal sealed class TrayceContext : ApplicationContext
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, ApiTray> trays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageSnapshot> cachedSnapshots = StateStore.Load();
    private readonly DetailsPopupController details = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private bool refreshing;

    public TrayceContext()
    {
        UiPalette.Changed += OnThemeChanged;
        LoadConfig();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        timer.Tick += async (_, _) => await RefreshDueAsync();
        timer.Start();
        _ = RefreshAllAsync();
    }

    internal ThemeMode ThemeMode => UiPalette.Mode;

    /// <summary>Persist a new theme choice and apply it immediately.</summary>
    internal void SetTheme(ThemeMode mode)
    {
        try
        {
            var config = ConfigStore.Load();
            config.Theme = SystemTheme.ToConfig(mode);
            ConfigStore.Save(config);
        }
        catch
        {
            // ponytail: theme is cosmetic; if the config can't be written we still apply in-memory.
        }

        UiPalette.Apply(mode);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && UiPalette.Mode == ThemeMode.System)
        {
            UiPalette.Apply(ThemeMode.System);
        }
    }

    private void OnThemeChanged()
    {
        details.Close();
        foreach (var tray in trays.Values) tray.OnThemeChanged();
    }

    internal async Task RefreshOneAsync(string apiId)
    {
        if (!trays.TryGetValue(apiId, out var tray)) return;

        try
        {
            var snapshot = await FetchUsageAsync(tray.Api);
            tray.Update(snapshot, stale: false);
            cachedSnapshots[apiId] = snapshot;
            SaveState();
        }
        catch (Exception ex)
        {
            tray.Update(tray.Snapshot?.WithMessage("Refresh failed: " + ex.Message) ?? UsageSnapshot.Error(ex.Message), stale: true);
        }
    }

    internal void ShowDetails(string apiId)
    {
        if (!trays.TryGetValue(apiId, out var tray)) return;
        details.Show(
            tray.Api,
            tray.Snapshot ?? UsageSnapshot.Unknown(),
            tray.Stale,
            async () => await RefreshOneAsync(apiId),
            () => ShowSettings(apiId));
    }

    internal void ShowMenu(string apiId)
    {
        if (!trays.ContainsKey(apiId)) return;
        details.Close();

        var items = new List<TrayMenu.Item>
        {
            new("Refresh now", "refresh", () => _ = RefreshOneAsync(apiId)),
            new("Details", "details", () => ShowDetails(apiId)),
            new("Settings…", "settings", () => ShowSettings(apiId)),
            TrayMenu.Item.Sep,
            new("Open config JSON", "code", OpenConfig),
            new("Reload config", "refresh", () => { Reload(); Toaster.Show(null, "Configuration reloaded"); }),
            TrayMenu.Item.Sep,
            new("About Trayce", "info", ShowAbout),
            TrayMenu.Item.Sep,
            new("Start with Windows", "windows", () =>
            {
                var enable = !StartupStore.IsEnabled();
                StartupStore.Set(enable);
                Toaster.Show(null, enable ? "Trayce will start with Windows" : "Removed from Windows startup");
            }, Checked: StartupStore.IsEnabled()),
            TrayMenu.Item.Sep,
            new("Quit Trayce", "power", Quit, Color: UiPalette.Crit)
        };

        new TrayMenu(items).ShowAt(Cursor.Position);
    }

    internal void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog();
    }

    internal void OpenConfig()
    {
        ConfigStore.EnsureSample();
        Process.Start(new ProcessStartInfo(ConfigStore.ConfigPath) { UseShellExecute = true });
    }

    internal void ShowSettings(string? selectedApiId = null)
    {
        using var form = new SettingsForm(ConfigStore.Load(), selectedApiId, Reload);
        form.ShowDialog();
    }

    internal void Reload()
    {
        LoadConfig();
        _ = RefreshAllAsync();
    }

    internal void Quit()
    {
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        UiPalette.Changed -= OnThemeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        details.Dispose();
        foreach (var tray in trays.Values) tray.Dispose();
        http.Dispose();
        base.ExitThreadCore();
    }

    private void LoadConfig()
    {
        TrayceConfig config;

        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Trayce config is invalid.\n\n" + ex.Message, "Trayce", MessageBoxButtons.OK, MessageBoxIcon.Error);
            config = new TrayceConfig();
        }

        UiPalette.Apply(SystemTheme.Parse(config.Theme));
        UiPalette.ApplyTray(TrayStyles.Parse(config.TrayStyle));

        var liveIds = config.Apis.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in trays.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            trays[stale].Dispose();
            trays.Remove(stale);
        }

        foreach (var api in config.Apis)
        {
            if (trays.TryGetValue(api.Id, out var tray))
            {
                tray.Api = api;
                tray.Update(tray.Snapshot ?? InitialSnapshot(api), stale: false);
            }
            else
            {
                trays.Add(api.Id, new ApiTray(api, this, InitialSnapshot(api)));
            }
        }
    }

    private UsageSnapshot InitialSnapshot(ApiConfig api)
    {
        if (!string.IsNullOrWhiteSpace(api.SourceUrl) && cachedSnapshots.TryGetValue(api.Id, out var cached)) return cached;
        return api.Usage ?? UsageSnapshot.Unknown();
    }

    private void SaveState()
    {
        StateStore.Save(trays.Values
            .Where(t => t.Snapshot is not null)
            .ToDictionary(t => t.Api.Id, t => t.Snapshot!, StringComparer.OrdinalIgnoreCase));
    }

    private async Task RefreshDueAsync()
    {
        if (refreshing) return;
        refreshing = true;

        try
        {
            var now = DateTimeOffset.Now;
            foreach (var tray in trays.Values.Where(t => t.NextPoll <= now).ToList())
            {
                await RefreshOneAsync(tray.Api.Id);
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    private async Task RefreshAllAsync()
    {
        foreach (var id in trays.Keys.ToList()) await RefreshOneAsync(id);
    }

    private async Task<UsageSnapshot> FetchUsageAsync(ApiConfig api)
    {
        if (string.IsNullOrWhiteSpace(api.SourceUrl))
        {
            return api.Usage ?? UsageSnapshot.Unknown("No sourceUrl or usage configured");
        }

        if (!Uri.TryCreate(api.SourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("sourceUrl must be http or https");
        }

        await using var stream = await http.GetStreamAsync(uri);
        var snapshot = await JsonSerializer.DeserializeAsync<UsageSnapshot>(stream, ConfigStore.JsonOptions);
        return snapshot?.Normalize().WithObservedAt(DateTimeOffset.Now) ?? throw new InvalidOperationException("usage endpoint returned empty JSON");
    }
}

internal sealed class ApiTray : IDisposable
{
    private readonly TrayceContext owner;
    private Icon? icon;

    public ApiConfig Api { get; set; }
    public NotifyIcon NotifyIcon { get; }
    public UsageSnapshot? Snapshot { get; private set; }
    public bool Stale { get; private set; }
    public DateTimeOffset NextPoll { get; private set; } = DateTimeOffset.MinValue;

    public ApiTray(ApiConfig api, TrayceContext owner, UsageSnapshot initialSnapshot)
    {
        Api = api;
        this.owner = owner;
        NotifyIcon = new NotifyIcon { Visible = true };
        NotifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) owner.ShowDetails(Api.Id);
            else if (e.Button == MouseButtons.Right) owner.ShowMenu(Api.Id);
        };
        Update(initialSnapshot, stale: false);
    }

    public void Update(UsageSnapshot snapshot, bool stale)
    {
        Snapshot = snapshot;
        Stale = stale;
        NextPoll = DateTimeOffset.Now.AddSeconds(Math.Max(1, Api.PollSeconds));

        icon?.Dispose();
        icon = IconRenderer.Render(Api, snapshot, stale);
        NotifyIcon.Icon = icon;
        NotifyIcon.Text = TrimTooltip(Tooltip(snapshot, stale));
    }

    public void OnThemeChanged() => Update(Snapshot ?? UsageSnapshot.Unknown(), Stale);

    public void Dispose()
    {
        NotifyIcon.Visible = false;
        NotifyIcon.Dispose();
        icon?.Dispose();
    }

    private string Tooltip(UsageSnapshot snapshot, bool stale)
    {
        var windows = UsageMath.WindowsForDisplay(snapshot);
        var usage = string.Join("   |   ", windows.Take(2).Select(w =>
            w.Limit is > 0
                ? $"{w.Label}: {Math.Round(w.Used / w.Limit.Value * 100m):0}%"
                : $"{w.Label}: {Format.Compact(w.Used, w.Metric)}"));
        var line = string.IsNullOrEmpty(usage) ? Api.DisplayName : $"{Api.DisplayName}\n{usage}";
        if (stale) line += "\n" + StatusInfo.For(stale, snapshot.Message).Label;
        if (!string.IsNullOrWhiteSpace(snapshot.Message)) line += "\n" + snapshot.Message;
        return line;
    }

    private static string TrimTooltip(string value) => value.Length <= 127 ? value : value[..124] + "...";
}

internal static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Tray icon rendered in the selected style; a status dot (top-right of the badge) shows only when stale/failed.
    public static Icon Render(ApiConfig api, UsageSnapshot usage, bool stale)
    {
        var iconSize = Math.Max(32, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(iconSize, iconSize);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);
        g.ScaleTransform(iconSize / 32f, iconSize / 32f);

        var color = Brand.Color(api, fallback: UiPalette.Accent2);
        var badge = UiPalette.Tray switch
        {
            TrayStyle.Ring => DrawRing(g, api, usage, color),
            TrayStyle.Minimal => DrawMinimal(g, api, usage, color),
            _ => DrawBars(g, api, usage, color)
        };

        if (stale)
        {
            var dot = new Rectangle(badge.Right - 6, Math.Max(1, badge.Top - 1), 8, 8);
            using var border = new SolidBrush(UiPalette.TaskbarSolid);
            using var fill = new SolidBrush(StatusInfo.For(stale, usage.Message).Color);
            g.FillEllipse(border, dot.X - 1, dot.Y - 1, dot.Width + 2, dot.Height + 2);
            g.FillEllipse(fill, dot);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Rectangle DrawBars(Graphics g, ApiConfig api, UsageSnapshot usage, Color color)
    {
        var badge = new Rectangle(4, 0, 24, 24);
        using (var brush = new SolidBrush(color)) UiPalette.FillRound(g, brush, badge, 5);
        DrawBadgeMark(g, api, badge);

        var ratios = UsageMath.Ratios(usage).Take(2).ToList();
        if (ratios.Count == 0) ratios.Add(UsageMath.Ratio(usage) ?? 0m);
        const float barWidth = 23f;
        const float barHeight = 3f;
        const float barGap = 1f;
        var barX = badge.Left + (badge.Width - barWidth) / 2;
        var y = badge.Bottom + 1f;
        using var track = new SolidBrush(Color.FromArgb(38, Color.White));
        foreach (var ratio in ratios)
        {
            var fillWidth = Math.Max(2f, barWidth * (float)Math.Clamp(ratio, 0m, 1m));
            using var fill = new SolidBrush(UsageMath.ColorForRatio(ratio));
            UiPalette.FillRound(g, track, new RectangleF(barX, y, barWidth, barHeight), 1.5f);
            if (ratio > 0m) UiPalette.FillRound(g, fill, new RectangleF(barX, y, fillWidth, barHeight), 1.5f);
            y += barHeight + barGap;
        }

        return badge;
    }

    private static Rectangle DrawMinimal(Graphics g, ApiConfig api, UsageSnapshot usage, Color color)
    {
        var badge = new Rectangle(3, 0, 26, 26);
        using (var brush = new SolidBrush(color)) UiPalette.FillRound(g, brush, badge, 6);
        DrawBadgeMark(g, api, badge);

        var ratio = UsageMath.Ratio(usage) ?? 0m;
        const float barWidth = 26f;
        const float barHeight = 3.5f;
        const float barY = 28f;
        var fillWidth = Math.Max(2f, barWidth * (float)Math.Clamp(ratio, 0m, 1m));
        using var track = new SolidBrush(Color.FromArgb(38, Color.White));
        using var fill = new SolidBrush(UsageMath.ColorForRatio(ratio));
        UiPalette.FillRound(g, track, new RectangleF(badge.Left, barY, barWidth, barHeight), 1.75f);
        if (ratio > 0m) UiPalette.FillRound(g, fill, new RectangleF(badge.Left, barY, fillWidth, barHeight), 1.75f);
        return badge;
    }

    private static Rectangle DrawRing(Graphics g, ApiConfig api, UsageSnapshot usage, Color color)
    {
        var ring = new Rectangle(0, 0, 32, 32);
        var ratio = UsageMath.Ratio(usage) ?? 0m;
        using (var track = new SolidBrush(UiPalette.ControlHover)) g.FillEllipse(track, ring);
        if (ratio > 0)
        {
            using var arc = new SolidBrush(UsageMath.ColorForRatio(ratio));
            g.FillPie(arc, ring, -90, (float)(Math.Clamp(ratio, 0m, 1m) * 360m));
        }

        var badge = new Rectangle(4, 4, 24, 24);
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, badge);
        DrawBadgeMark(g, api, badge);
        return badge;
    }

    private static void DrawBadgeMark(Graphics g, ApiConfig api, Rectangle badge)
    {
        var logoPath = ConfigStore.ResolvePath(api.LogoPath);
        if (logoPath is not null && File.Exists(logoPath))
        {
            var inset = badge.Width <= 24 ? 3 : 4;
            Logo.Draw(g, api, Rectangle.Inflate(badge, -inset, -inset), Color.White, small: true);
            return;
        }

        var text = !string.IsNullOrWhiteSpace(api.LogoText) ? api.LogoText : Logo.Initials(api.DisplayName);
        var px = badge.Width * (text.Length >= 3 ? 0.34f : text.Length == 2 ? 0.42f : 0.52f);
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        var measured = g.MeasureString(text, font);
        g.DrawString(text, font, brush,
            badge.Left + (badge.Width - measured.Width) / 2f,
            badge.Top + (badge.Height - measured.Height) / 2f);
    }

}

internal static class Dpi
{
    public static int Scale(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96.0);
    public static float Scale(Control control, float value) => value * control.DeviceDpi / 96f;
    public static Rectangle Rect(Control control, int x, int y, int width, int height) => new(Scale(control, x), Scale(control, y), Scale(control, width), Scale(control, height));
}

internal static class Brand
{
    public static Color Color(ApiConfig api, Color fallback)
    {
        var value = api.UseBrandColor ? api.BrandColor : api.CustomColor;
        if (string.IsNullOrWhiteSpace(value)) value = api.BrandColor;
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }
}

internal static class Logo
{
    public static void Draw(Graphics g, ApiConfig api, Rectangle bounds, Color textColor, bool small)
    {
        var logoPath = ConfigStore.ResolvePath(api.LogoPath);
        var drawablePath = logoPath is null ? null : DrawablePath(logoPath);
        if (drawablePath is not null && File.Exists(drawablePath))
        {
            try
            {
                using var stream = File.OpenRead(drawablePath);
                if (string.Equals(Path.GetExtension(drawablePath), ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using var icon = new Icon(stream);
                    g.DrawIcon(icon, bounds);
                }
                else
                {
                    using var image = Image.FromStream(stream);
                    g.DrawImage(image, bounds);
                }
                return;
            }
            catch
            {
                // ponytail: broken logo path falls back to text; config can be fixed later.
            }
        }

        var text = !string.IsNullOrWhiteSpace(api.LogoText) ? api.LogoText : Initials(api.DisplayName);
        using var font = FitFont(g, text, bounds, small ? 13 : 24);
        using var brush = new SolidBrush(textColor);
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, bounds.Left + (bounds.Width - size.Width) / 2, bounds.Top + (bounds.Height - size.Height) / 2);
    }

    private static string? DrawablePath(string logoPath)
    {
        if (!string.Equals(Path.GetExtension(logoPath), ".svg", StringComparison.OrdinalIgnoreCase)) return logoPath;

        var png = Path.ChangeExtension(logoPath, ".png");
        return File.Exists(png) ? png : null;
    }

    public static string Initials(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private static Font FitFont(Graphics g, string text, Rectangle bounds, int start)
    {
        for (var size = start; size >= 7; size--)
        {
            var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font);
            if (measured.Width <= bounds.Width + 2 && measured.Height <= bounds.Height + 2) return font;
            font.Dispose();
        }

        return new Font("Segoe UI", 7, FontStyle.Bold, GraphicsUnit.Pixel);
    }
}

internal sealed class DetailsPopupController : IDisposable
{
    private DetailsForm? active;

    public bool HasActive => active is { IsDisposed: false };

    public void Show(ApiConfig api, UsageSnapshot usage, bool stale, Action? onRefresh = null, Action? onSettings = null)
    {
        Show(new DetailsForm(api, usage, stale, onRefresh, onSettings));
    }

    internal void Show(DetailsForm form)
    {
        Close();
        active = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(active, form)) active = null;
            form.Dispose();
        };
        form.Show();
        form.Activate();
    }

    public void Close()
    {
        var form = active;
        active = null;
        if (form is not null && !form.IsDisposed) form.Close();
    }

    public void Dispose()
    {
        Close();
    }
}

internal sealed class GlyphButton : Control
{
    private bool hover;

    public string Glyph { get; set; } = "";
    public Color GlyphColor { get; set; } = UiPalette.Text2;
    public bool UseAccentFill { get; set; }

    public GlyphButton()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Size = new Size(32, 32);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(UseAccentFill ? UiPalette.Accent2 : hover ? UiPalette.ControlHover : UiPalette.Control);
        using var border = new Pen(UseAccentFill ? UiPalette.Accent2 : UiPalette.Border2);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        UiPalette.FillRound(e.Graphics, back, rect, Dpi.Scale(this, 7));
        UiPalette.DrawRound(e.Graphics, border, rect, Dpi.Scale(this, 7));
        IconPainter.Draw(e.Graphics, Glyph, new Rectangle(Dpi.Scale(this, 8), Dpi.Scale(this, 8), Width - Dpi.Scale(this, 16), Height - Dpi.Scale(this, 16)), UseAccentFill ? Color.White : GlyphColor);
    }
}

internal static class IconPainter
{
    public static Bitmap Bitmap(string glyph, Color color)
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw(g, glyph, new Rectangle(1, 1, 16, 16), color);
        return bmp;
    }

    public static void Draw(Graphics g, string glyph, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.5f, bounds.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);

        var l = bounds.Left;
        var t = bounds.Top;
        var r = bounds.Right;
        var b = bounds.Bottom;
        var w = bounds.Width;
        var h = bounds.Height;
        var cx = l + w / 2f;
        var cy = t + h / 2f;

        switch (glyph)
        {
            case "refresh":
                PointF p(float x, float y) => new(l + x / 24f * w, t + y / 24f * h);
                g.DrawBezier(pen, p(3.5f, 9), p(6f, 4.4f), p(12.6f, 2.2f), p(18.4f, 5.6f));
                g.DrawLine(pen, p(23, 4), p(23, 10));
                g.DrawLine(pen, p(23, 10), p(17, 10));
                g.DrawBezier(pen, p(20.5f, 15), p(18f, 19.6f), p(11.4f, 21.8f), p(5.6f, 18.4f));
                g.DrawLine(pen, p(1, 20), p(1, 14));
                g.DrawLine(pen, p(1, 14), p(7, 14));
                break;
            case "details":
                g.DrawLine(pen, l + 3, b - 3, r - 3, t + 3);
                g.DrawLine(pen, r - 3, t + 3, r - 3, t + 8);
                g.DrawLine(pen, r - 3, t + 3, r - 8, t + 3);
                g.DrawLine(pen, l + 3, b - 3, l + 3, b - 8);
                g.DrawLine(pen, l + 3, b - 3, l + 8, b - 3);
                break;
            case "settings":
                // three vertical sliders with handles (vertical equalizer)
                g.DrawLine(pen, l + 4, t + 2, l + 4, b - 2);
                g.DrawLine(pen, cx, t + 2, cx, b - 2);
                g.DrawLine(pen, r - 4, t + 2, r - 4, b - 2);
                g.FillEllipse(brush, l + 2, t + 4, 4, 4);
                g.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                g.FillEllipse(brush, r - 6, b - 8, 4, 4);
                break;
            case "code":
                g.DrawLines(pen, new[] { new Point(l + 6, t + 4), new Point(l + 2, (int)cy), new Point(l + 6, b - 4) });
                g.DrawLines(pen, new[] { new Point(r - 6, t + 4), new Point(r - 2, (int)cy), new Point(r - 6, b - 4) });
                break;
            case "windows":
                g.DrawRectangle(pen, l + 2, t + 2, 5, 5);
                g.DrawRectangle(pen, r - 7, t + 2, 5, 5);
                g.DrawRectangle(pen, l + 2, b - 7, 5, 5);
                g.DrawRectangle(pen, r - 7, b - 7, 5, 5);
                break;
            case "copy":
                g.DrawRectangle(pen, l + 6, t + 6, w - 7, h - 7);
                g.DrawRectangle(pen, l + 2, t + 2, w - 7, h - 7);
                break;
            case "eye":
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(l + 1, cy, l + 5, t + 4, r - 5, t + 4, r - 1, cy);
                    path.AddBezier(r - 1, cy, r - 5, b - 4, l + 5, b - 4, l + 1, cy);
                    g.DrawPath(pen, path);
                }
                g.DrawEllipse(pen, cx - 3, cy - 3, 6, 6);
                break;
            case "trash":
                g.DrawLine(pen, l + 2, t + 5, r - 2, t + 5);
                g.DrawLine(pen, l + 6, t + 5, l + 7, b - 2);
                g.DrawLine(pen, r - 6, t + 5, r - 7, b - 2);
                g.DrawLine(pen, l + 7, b - 2, r - 7, b - 2);
                g.DrawLine(pen, l + 7, t + 2, r - 7, t + 2);
                break;
            case "power":
                g.DrawLine(pen, cx, t + 2, cx, cy);
                g.DrawArc(pen, l + 3, t + 4, w - 6, h - 6, 135, 270);
                break;
            case "plus":
                g.DrawLine(pen, cx, t + 3, cx, b - 3);
                g.DrawLine(pen, l + 3, cy, r - 3, cy);
                break;
            case "check":
                g.DrawLines(pen, new[] { new Point(l + 2, (int)cy), new Point((int)cx - 1, b - 3), new Point(r - 2, t + 3) });
                break;
            case "alert":
                g.DrawPolygon(pen, new[] { new PointF(cx, t + 2), new PointF(r - 2, b - 2), new PointF(l + 2, b - 2) });
                g.DrawLine(pen, cx, cy - 1, cx, cy + 2);
                g.FillEllipse(brush, cx - 1.1f, b - 5, 2.2f, 2.2f);
                break;
            case "info":
                g.DrawEllipse(pen, l + 2, t + 2, w - 4, h - 4);
                g.DrawLine(pen, cx, cy, cx, cy + 3);
                g.FillEllipse(brush, cx - 1.1f, t + 4, 2.2f, 2.2f);
                break;
            case "link":
                g.DrawLines(pen, new[] { new Point((int)cx + 2, t + 3), new Point(r - 2, (int)cy), new Point((int)cx + 2, b - 3) });
                g.DrawLines(pen, new[] { new Point((int)cx - 2, t + 3), new Point(l + 2, (int)cy), new Point((int)cx - 2, b - 3) });
                break;
            case "upload":
                g.DrawLine(pen, l + 2, b - 3, r - 2, b - 3);
                g.DrawLine(pen, l + 2, b - 6, l + 2, b - 3);
                g.DrawLine(pen, r - 2, b - 6, r - 2, b - 3);
                g.DrawLines(pen, new[] { new Point(l + 4, (int)cy - 1), new Point((int)cx, t + 2), new Point(r - 4, (int)cy - 1) });
                g.DrawLine(pen, cx, t + 2, cx, b - 6);
                break;
            case "image":
                g.DrawRectangle(pen, l + 2, t + 3, w - 4, h - 6);
                g.DrawEllipse(pen, l + 5, t + 6, 3, 3);
                g.DrawLines(pen, new[] { new Point(l + 3, b - 4), new Point((int)cx, (int)cy), new Point(r - 3, b - 4) });
                break;
            default:
                g.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                break;
        }
    }
}

internal sealed class DetailsForm : Form
{
    private readonly bool previewMode;
    private readonly System.Windows.Forms.Timer outsideClickTimer = new() { Interval = 50 };
    private readonly Action? onRefresh;
    private readonly Action? onSettings;

    public DetailsForm(ApiConfig api, UsageSnapshot usage, bool stale, Action? onRefresh = null, Action? onSettings = null, bool previewMode = false)
    {
        this.previewMode = previewMode;
        this.onRefresh = onRefresh;
        this.onSettings = onSettings;
        Text = "Trayce - " + api.DisplayName;
        Icon = AppIcon.Load();
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        TopMost = !previewMode;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = UiPalette.Flyout;
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;
        DoubleBuffered = true;
        Padding = new Padding(1);

        int S(int value) => Dpi.Scale(this, value);
        var pad = S(16);
        var formWidth = S(344);
        var contentWidth = formWidth - (pad * 2);
        var windows = UsageMath.WindowsForDisplay(usage);
        var status = StatusInfo.For(stale, usage.Message);
        var showStatusBanner = stale;
        var showInfoBanner = !stale && !string.IsNullOrWhiteSpace(usage.Message);

        var root = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, BackColor = UiPalette.Flyout };

        var titleWidth = contentWidth - S(36) - S(12) - (onRefresh is null ? 0 : S(40));
        var badge = new LogoBadge(api) { Location = new Point(pad, S(16)), Size = new Size(S(36), S(36)), Radius = 9, LogoInset = 3 };
        var title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(S(64), S(15)),
            Size = new Size(Math.Max(S(120), titleWidth), S(22)),
            Text = api.DisplayName,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = UiPalette.Text
        };
        var provider = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(S(64), S(37)),
            Size = new Size(Math.Max(S(120), titleWidth), S(16)),
            Text = api.Provider ?? "",
            Font = new Font("Segoe UI", 8.25F),
            ForeColor = UiPalette.Text3
        };
        var updated = new Label
        {
            AutoSize = false,
            Location = new Point(pad, S(60)),
            Size = new Size(contentWidth, S(16)),
            Text = "Updated " + Format.Relative(usage.ObservedAt),
            Font = new Font("Segoe UI", 8.25F),
            ForeColor = UiPalette.Text3
        };
        var refresh = new GlyphButton { Glyph = "refresh", Location = new Point(formWidth - pad - S(32), S(16)), Size = new Size(S(32), S(32)) };
        refresh.Click += (_, _) => this.onRefresh?.Invoke();

        root.Controls.Add(badge);
        root.Controls.Add(title);
        root.Controls.Add(provider);
        root.Controls.Add(updated);
        if (onRefresh is not null) root.Controls.Add(refresh);

        var top = S(84);
        if (showStatusBanner)
        {
            var banner = new FlyoutBanner("alert", status.Color, status.BannerBg, status.Label, usage.Message ?? "", contentWidth)
            {
                Location = new Point(pad, top)
            };
            root.Controls.Add(banner);
            top += banner.Height + S(13);
        }

        foreach (var window in windows)
        {
            var row = new UsageWindowRow(window) { Location = new Point(pad, top), Width = contentWidth };
            row.Height = S(row.Height);
            root.Controls.Add(row);
            top += row.Height + S(15);
        }
        if (windows.Count > 0) top -= S(11); // swap trailing 15 gap for 4px container padding

        if (showInfoBanner)
        {
            var info = new FlyoutBanner("info", UiPalette.Text3, UiPalette.Control, null, usage.Message ?? "", contentWidth)
            {
                Location = new Point(pad, top + S(4))
            };
            root.Controls.Add(info);
            top += info.Height + S(12);
        }

        if (onSettings is not null)
        {
            top += S(12);
            root.Controls.Add(new Panel { BackColor = UiPalette.Border, Location = new Point(0, top), Size = new Size(formWidth, S(1)) });
            var manage = new RoundedButton("Manage in Settings")
            {
                Accent = true,
                Radius = 7,
                TextPx = 12.5f,
                Location = new Point(pad, top + S(14)),
                Size = new Size(contentWidth, S(33)),
            };
            manage.Click += (_, _) =>
            {
                Close();
                this.onSettings?.Invoke();
            };
            root.Controls.Add(manage);
            top += S(14 + 33 + 16);
        }
        else
        {
            top += S(14);
        }

        ClientSize = new Size(formWidth, Math.Min(S(760), top));
        Controls.Add(root);
        if (!previewMode)
        {
            Deactivate += (_, _) => Close();
            outsideClickTimer.Tick += (_, _) =>
            {
                if (ShouldCloseForOutsideClick(Cursor.Position, MouseButtons)) Close();
            };
            outsideClickTimer.Start();
        }
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    internal bool ShouldCloseForOutsideClick(Point cursorPosition, MouseButtons buttons) =>
        !previewMode && buttons != MouseButtons.None && !Bounds.Contains(cursorPosition);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        outsideClickTimer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) outsideClickTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindows11Corners(this);
        if (!previewMode) NativeChrome.ApplyWindows11Backdrop(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiPalette.Border2);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (previewMode)
        {
            Location = new Point(-10000, -10000);
            return;
        }

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Clamp(Cursor.Position.X - Width + 20, screen.Left, screen.Right - Width),
            Math.Clamp(Cursor.Position.Y - 12, screen.Top, screen.Bottom - Height));
    }
}

internal sealed class LogoBadge : Control
{
    private readonly ApiConfig api;

    public int Radius { get; set; } = 8;
    public int LogoInset { get; set; } = 5;
    public Color? Fallback { get; set; }

    public LogoBadge(ApiConfig api)
    {
        this.api = api;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var brush = new SolidBrush(Brand.Color(api, Fallback ?? Color.FromArgb(30, 41, 59)));
        UiPalette.FillRound(g, brush, new Rectangle(0, 0, Width - 1, Height - 1), Dpi.Scale(this, Radius));

        var logoPath = ConfigStore.ResolvePath(api.LogoPath);
        if (logoPath is not null && File.Exists(logoPath))
        {
            var inset = Dpi.Scale(this, LogoInset);
            Logo.Draw(g, api, new Rectangle(inset, inset, Width - (inset * 2), Height - (inset * 2)), Color.White, small: false);
            return;
        }

        var text = !string.IsNullOrWhiteSpace(api.LogoText) ? api.LogoText : Logo.Initials(api.DisplayName);
        var basePx = text.Length >= 3 ? 13f : text.Length == 2 ? 16f : 19f;
        var px = basePx * (Width / 36f) * (DeviceDpi / 96f);
        using var font = new Font("Segoe UI", Math.Max(7f, px), FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, white, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
    }
}

internal sealed class UsageWindowRow : Control
{
    private readonly UsageWindow window;
    private readonly bool hasNote;

    private const int Base = 68;

    public UsageWindowRow(UsageWindow window)
    {
        this.window = window;
        hasNote = !string.IsNullOrWhiteSpace(window.Message);
        DoubleBuffered = true;
        BackColor = UiPalette.Flyout;
        Height = hasNote ? Base : 50;
    }

    // Draw proportional to actual height with device-pixel fonts so descenders never clip across DPI.
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Flyout));

        var s = Height / (float)(hasNote ? Base : 50);
        int S(float v) => (int)Math.Round(v * s);
        var hasLimit = window.Limit is > 0;
        var ratio = hasLimit ? window.Used / window.Limit!.Value : 0m;

        using var labelFont = new Font("Segoe UI", 12.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(UiPalette.Text);
        using var muted = new SolidBrush(UiPalette.Text2);
        using var subtle = new SolidBrush(UiPalette.Text3);
        using var accent = new SolidBrush(UsageMath.ColorForRatio(ratio));

        g.DrawString(window.Label, labelFont, text, 0, 0);
        if (hasLimit)
        {
            var pct = Math.Round(ratio * 100m).ToString("0") + "%";
            var pctSize = g.MeasureString(pct, labelFont);
            g.DrawString(pct, labelFont, accent, Width - pctSize.Width, 0);
        }

        var bar = new Rectangle(0, S(22), Width, S(7));
        using var barBack = new SolidBrush(UiPalette.ControlHover);
        UiPalette.FillRound(g, barBack, bar, S(6));
        UiPalette.FillRound(g, accent, new Rectangle(bar.X, bar.Y, (int)Math.Round(bar.Width * Math.Clamp(ratio, 0m, 1m)), bar.Height), S(6));

        var line = hasLimit
            ? $"{Format.Compact(window.Used, window.Metric)} / {Format.Compact(window.Limit!.Value, window.Metric)} {window.Metric}".Trim()
            : $"{Format.Decimal(window.Used)} {window.Metric}".Trim();
        g.DrawString(line, smallFont, muted, 0, S(34));

        var reset = Format.Reset(window.ResetsAt);
        if (!string.IsNullOrEmpty(reset))
        {
            var resetText = "resets " + reset;
            var size = g.MeasureString(resetText, smallFont);
            g.DrawString(resetText, smallFont, subtle, Width - size.Width, S(34));
        }

        if (hasNote) g.DrawString(window.Message, smallFont, subtle, 0, S(51));
    }
}

/// <summary>Status/info banner inside the flyout: rounded tinted background, leading glyph,
/// and a bold colored label followed by a muted, word-wrapped message.</summary>
internal sealed class FlyoutBanner : Control
{
    private readonly string glyph;
    private readonly Color accent;
    private readonly Color back;
    private readonly string? label;
    private readonly string message;

    public FlyoutBanner(string glyph, Color accent, Color back, string? label, string message, int width)
    {
        this.glyph = glyph;
        this.accent = accent;
        this.back = back;
        this.label = label;
        this.message = message;
        DoubleBuffered = true;
        Width = width;
        Height = Measure();
    }

    private int TextLeft => Dpi.Scale(this, 11 + 15 + 9);
    private int TextWidth => Width - TextLeft - Dpi.Scale(this, 11);

    private int Measure()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var fonts = BuildRuns(out var runs);
        var height = TextFlow.Measure(g, runs, TextWidth);
        return Math.Max(Dpi.Scale(this, 33), height + Dpi.Scale(this, 18));
    }

    private IDisposable BuildRuns(out List<TextFlow.Run> runs)
    {
        var bold = new Font("Segoe UI", 9F, FontStyle.Bold);
        var reg = new Font("Segoe UI", 8.6F);
        runs = new List<TextFlow.Run>();
        if (!string.IsNullOrEmpty(label))
        {
            runs.Add(new TextFlow.Run(label!, bold, accent));
            runs.Add(new TextFlow.Run(" — ", reg, UiPalette.Text2));
        }
        runs.Add(new TextFlow.Run(message, reg, UiPalette.Text2));
        return new FontPair(bold, reg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Flyout));
        using (var fill = new SolidBrush(back))
        {
            UiPalette.FillRound(g, fill, new Rectangle(0, 0, Width - 1, Height - 1), Dpi.Scale(this, 8));
        }

        IconPainter.Draw(g, glyph, new Rectangle(Dpi.Scale(this, 11), Dpi.Scale(this, 10), Dpi.Scale(this, 15), Dpi.Scale(this, 15)), accent);
        using var fonts = BuildRuns(out var runs);
        TextFlow.Draw(g, runs, new Rectangle(TextLeft, Dpi.Scale(this, 9), TextWidth, Height - Dpi.Scale(this, 18)));
    }

    private sealed class FontPair : IDisposable
    {
        private readonly Font a;
        private readonly Font b;
        public FontPair(Font a, Font b) { this.a = a; this.b = b; }
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }
}

/// <summary>Minimal run-based word-wrapping text layout (mixed fonts/colors on a flowing line).</summary>
internal static class TextFlow
{
    internal readonly record struct Run(string Text, Font Font, Color Color);

    public static int Measure(Graphics g, IReadOnlyList<Run> runs, int width) => Layout(g, runs, width, null);

    public static void Draw(Graphics g, IReadOnlyList<Run> runs, Rectangle area) => Layout(g, runs, area.Width, area.Location);

    private static int Layout(Graphics g, IReadOnlyList<Run> runs, int width, Point? origin)
    {
        var format = StringFormat.GenericTypographic;
        float lineHeight = 0;
        foreach (var run in runs) lineHeight = Math.Max(lineHeight, run.Font.GetHeight(g));
        if (lineHeight <= 0) lineHeight = 14;

        float x = 0;
        float y = 0;
        foreach (var run in runs)
        {
            using var brush = origin is null ? null : new SolidBrush(run.Color);
            // GenericTypographic trims trailing whitespace, so measure the space as a delta.
            var spaceWidth = g.MeasureString("a a", run.Font, int.MaxValue, format).Width
                - g.MeasureString("aa", run.Font, int.MaxValue, format).Width;
            var tokens = run.Text.Split(' ');
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i > 0)
                {
                    if (x > 0) x += spaceWidth;
                }

                var token = tokens[i];
                if (token.Length == 0) continue;
                var w = g.MeasureString(token, run.Font, int.MaxValue, format).Width;
                if (x > 0 && x + w > width)
                {
                    x = 0;
                    y += lineHeight;
                }

                if (origin is { } o && brush is not null)
                {
                    g.DrawString(token, run.Font, brush, o.X + x, o.Y + y, format);
                }

                x += w;
            }
        }

        return (int)Math.Ceiling(y + lineHeight);
    }
}

internal readonly record struct StatusInfo(string Label, Color Color, Color BannerBg)
{
    public static StatusInfo For(bool stale, string? message)
    {
        if (!stale) return new StatusInfo("Up to date", UiPalette.Ok, Color.FromArgb(33, UiPalette.Ok));

        var m = message ?? "";
        // Hard auth/permission errors are "failed" (crit); timeouts and generic refresh issues are "stale" (warn).
        var failed = m.Contains("reject", StringComparison.OrdinalIgnoreCase)
            || m.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || m.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || m.Contains("403") || m.Contains("401");

        return failed
            ? new StatusInfo("Refresh failed", UiPalette.Crit, Color.FromArgb(41, 255, 99, 114))
            : new StatusInfo("Data may be stale", UiPalette.Warn, Color.FromArgb(38, UiPalette.Warn));
    }
}

internal static class NativeChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, int lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    public static void ApplyWindows11Corners(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var preference = 2;
        _ = DwmSetWindowAttribute(form.Handle, 33, ref preference, sizeof(int));
    }

    public static void ApplyWindows11Backdrop(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        var transientWindow = 3;
        _ = DwmSetWindowAttribute(form.Handle, 38, ref transientWindow, sizeof(int));
    }

    public static void ApplyDarkScrollbars(Control control)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        if (control.IsHandleCreated) _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        else control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    public static void DragWindow(IntPtr handle)
    {
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, HtCaption, 0);
    }
}

internal static class AppIcon
{
    public static Icon? Load()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }
}

internal static class PreviewRenderer
{
    public static void Render(string path, ThemeMode theme = ThemeMode.Dark, bool error = false)
    {
        UiPalette.Apply(theme);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var api = error
            ? new ApiConfig { Id = "vercel", DisplayName = "Vercel", Provider = "Edge · Functions", LogoText = "V", BrandColor = "#111111" }
            : new ApiConfig { Id = "anthropic", DisplayName = "Anthropic", Provider = "Claude API", LogoPath = PresetIcons.Anthropic, LogoText = "A", BrandColor = "#D97757" };

        var usage = error
            ? new UsageSnapshot
            {
                ObservedAt = DateTimeOffset.Now.AddMinutes(-32),
                Windows = new List<UsageWindow>
                {
                    new() { Label = "Daily", Metric = "calls", Used = 18400, Limit = 50000, ResetsAt = DateTimeOffset.Now.AddHours(7) },
                    new() { Label = "Monthly", Metric = "dollars", Used = 42, Limit = 150, ResetsAt = DateTimeOffset.Now.AddDays(13) }
                },
                Message = "Last refresh failed (timeout)"
            }
            : new UsageSnapshot
            {
                ObservedAt = DateTimeOffset.Now.AddMinutes(-2),
                Windows = new List<UsageWindow>
                {
                    new() { Label = "5h", Metric = "tokens", Used = 720000, Limit = 2000000, ResetsAt = DateTimeOffset.Now.AddHours(2).AddMinutes(14), Message = "Includes cache reads" },
                    new() { Label = "7d", Metric = "tokens", Used = 26200000, Limit = 70000000, ResetsAt = DateTimeOffset.Now.AddDays(3) }
                },
                Message = "Operating normally"
            };

        using var form = new DetailsForm(api, usage, stale: error, onRefresh: () => { }, onSettings: () => { }, previewMode: true);
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
        form.Close();
    }

    public static void RenderSettings(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var form = new SettingsForm(SettingsSampleConfig(), "anthropic")
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10000, -10000)
        };
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
        form.Close();
    }

    /// <summary>Render every surface in both themes for visual verification against the prototype.</summary>
    public static void RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (mode, suffix) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
        {
            UiPalette.Apply(mode);
            Render(Path.Combine(dir, $"flyout-ok-{suffix}.png"), mode, error: false);
            Render(Path.Combine(dir, $"flyout-error-{suffix}.png"), mode, error: true);
            RenderSettings(Path.Combine(dir, $"settings-{suffix}.png"));
            Capture(Path.Combine(dir, $"menu-{suffix}.png"), MenuPreview());
            RenderTrayStrip(Path.Combine(dir, $"tray-{suffix}.png"));
        }

        UiPalette.Apply(ThemeMode.Dark);
        UiPalette.ApplyTray(TrayStyle.Ring);
        RenderTrayStrip(Path.Combine(dir, "tray-ring.png"));
        UiPalette.ApplyTray(TrayStyle.Minimal);
        RenderTrayStrip(Path.Combine(dir, "tray-minimal.png"));
        UiPalette.ApplyTray(TrayStyle.Bars);
        Capture(Path.Combine(dir, "dialog-presets.png"), new PresetPickerForm());
        Capture(Path.Combine(dir, "dialog-colorpicker.png"), new ColorPickerForm("#5E6AD2", "#D97757"));
        Capture(Path.Combine(dir, "dialog-json.png"), new JsonPreviewForm(SampleConfig()));
        Capture(Path.Combine(dir, "dialog-validation.png"), new ValidationForm(new[] { "API name can’t be empty.", "The limit for “Daily” must be greater than zero." }));
        Capture(Path.Combine(dir, "dialog-logo.png"), new LogoPickerForm(new ApiConfig { Id = "anthropic", DisplayName = "Anthropic", LogoText = "A", BrandColor = "#D97757" }));
        Capture(Path.Combine(dir, "dialog-about.png"), new AboutForm());
        Capture(Path.Combine(dir, "toast.png"), new ToastForm("Settings saved"));
    }

    private static void Capture(string path, Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.Location = new Point(-10000, -10000);
        form.Show();
        Application.DoEvents();
        using (var bitmap = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height)))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path);
        }

        form.Close();
        form.Dispose();
    }

    private static TrayMenu MenuPreview() => new(new List<TrayMenu.Item>
    {
        new("Refresh now", "refresh", () => { }),
        new("Details", "details", () => { }),
        new("Settings…", "settings", () => { }),
        TrayMenu.Item.Sep,
        new("Open config JSON", "code", () => { }),
        new("Reload config", "refresh", () => { }),
        TrayMenu.Item.Sep,
        new("About Trayce", "info", () => { }),
        TrayMenu.Item.Sep,
        new("Start with Windows", "windows", () => { }, Checked: true),
        TrayMenu.Item.Sep,
        new("Quit Trayce", "power", () => { }, Color: UiPalette.Crit)
    });

    private static void RenderTrayStrip(string path)
    {
        var apis = SampleConfig().Apis;
        const int cell = 44;
        const int size = 32;
        using var bmp = new Bitmap(cell * apis.Count + 16, cell + 8);
        using var g = Graphics.FromImage(bmp);
        g.Clear(UiPalette.TaskbarSolid);
        for (var i = 0; i < apis.Count; i++)
        {
            var api = apis[i];
            var stale = api.Id is "vercel" or "aws";
            using var icon = IconRenderer.Render(api, api.Usage ?? UsageSnapshot.Unknown(), stale);
            g.DrawIcon(icon, new Rectangle(8 + i * cell + (cell - size) / 2, 4 + (cell - size) / 2, size, size));
        }

        bmp.Save(path);
    }

    // Mirrors the prototype's seed() data so settings/tray previews match the reference 1:1.
    private static TrayceConfig SampleConfig()
    {
        var now = DateTimeOffset.Now;
        ApiConfig Api(string id, string name, string provider, string logo, string color, bool useBrand, string? custom, int poll, string? message, params UsageWindow[] windows) => new()
        {
            Id = id,
            DisplayName = name,
            Provider = provider,
            LogoPath = PresetIcons.ForId(id),
            LogoText = logo,
            BrandColor = color,
            UseBrandColor = useBrand,
            CustomColor = custom,
            ApiKey = id + "_example_key",
            SourceUrl = "https://api.example.com/" + id + "/usage",
            PollSeconds = poll,
            Usage = new UsageSnapshot { Windows = windows.ToList(), Message = message }
        };
        UsageWindow W(string label, string metric, decimal used, decimal limit, DateTimeOffset reset, string note = "") =>
            new() { Label = label, Metric = metric, Used = used, Limit = limit, ResetsAt = reset, Message = note };

        return new TrayceConfig
        {
            Apis = new List<ApiConfig>
            {
                Api("anthropic", "Anthropic", "Claude API", "A", "#D97757", true, "#D97757", 300, "Operating normally",
                    W("5h", "tokens", 720000, 2000000, now.AddHours(2).AddMinutes(14), "Includes cache reads"),
                    W("7d", "tokens", 26200000, 70000000, now.AddDays(4))),
                Api("openai", "OpenAI", "GPT-4o · API", "OAI", "#10A37F", true, "#10A37F", 600, null,
                    W("Daily", "requests", 4210, 10000, now.AddHours(8)),
                    W("Monthly", "dollars", 128, 500, now.AddDays(14), "Hard spend cap")),
                Api("github", "GitHub", "REST API", "GH", "#1F6FEB", true, "#1F6FEB", 60, null,
                    W("Hourly", "requests", 3800, 5000, now.AddMinutes(41), "Core REST quota")),
                Api("cursor", "Cursor", "Editor API", "Cu", "#111418", false, "#8E8EA0", 120, null,
                    W("Monthly", "requests", 480, 500, now.AddDays(14), "Fast requests")),
                Api("vercel", "Vercel", "Edge · Functions", "V", "#111111", true, "#111111", 30, "Last refresh failed (timeout)",
                    W("Daily", "calls", 18400, 50000, now.AddHours(6)),
                    W("Monthly", "dollars", 42, 150, now.AddDays(14))),
                Api("aws", "AWS", "CloudWatch", "AWS", "#FF9900", true, "#FF9900", 1800, "API key rejected (403 Forbidden)",
                    W("Monthly", "dollars", 0, 2000, now.AddDays(14), "Budget alarm"))
            }
        };
    }

    private static TrayceConfig SettingsSampleConfig()
    {
        var config = SampleConfig();
        config.Apis = config.Apis.Where(api => api.Id is "anthropic" or "github" or "cursor" or "aws").ToList();
        return config;
    }
}

internal static class LiveShot
{
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("User32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, int flags);

    private static uint MonitorDpi(Screen screen)
    {
        var center = new Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
        GetDpiForMonitor(MonitorFromPoint(center, 2), 0, out var dpiX, out _);
        return dpiX;
    }

    public static void DpiProbe()
    {
        Console.WriteLine("per-monitor effective DPI (100%=96, 125%=120, 150%=144):");
        foreach (var screen in Screen.AllScreens)
        {
            var center = new Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
            var mon = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
            GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _);
            Console.WriteLine($"  {(screen.Primary ? "*" : " ")} {screen.DeviceName} bounds={screen.Bounds} effectiveDPI={dpiX} (~{dpiX * 100 / 96}%)");
        }
    }

    public static void Settings(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var config = ConfigStore.Load();
        UiPalette.Apply(SystemTheme.Parse(config.Theme));
        UiPalette.ApplyTray(TrayStyles.Parse(config.TrayStyle));

        // Place the capture on the highest-DPI monitor so HiDPI rendering is actually exercised.
        var target = Screen.AllScreens.OrderByDescending(MonitorDpi).First();
        using var form = new SettingsForm(config)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(target.WorkingArea.X + 40, target.WorkingArea.Y + 40),
            TopMost = true
        };
        form.Show();
        form.Activate();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 900) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }

        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(form.Location, Point.Empty, form.Size);
        bmp.Save(path);
        Console.WriteLine($"shot {form.Width}x{form.Height} dpi={form.DeviceDpi}");
        form.Close();
    }
}

internal static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Trayce");
    private static readonly string LogoDir = Path.Combine(AppDir, "logos");
    public static readonly string ConfigPath = Path.Combine(AppDir, "apis.json");

    public static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return path;

        var imported = Path.Combine(AppDir, path);
        if (File.Exists(imported)) return imported;

        var bundled = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(bundled) ? bundled : imported;
    }

    public static string ImportLogo(string apiId, string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Logo file not found.", sourcePath);

        Directory.CreateDirectory(LogoDir);
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var fileName = SafeFileName(apiId) + extension.ToLowerInvariant();
        var destination = Path.Combine(LogoDir, fileName);
        File.Copy(sourcePath, destination, overwrite: true);
        return Path.Combine("logos", fileName);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "logo" : safe;
    }

    public static TrayceConfig Load()
    {
        EnsureSample();
        var config = JsonSerializer.Deserialize<TrayceConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new TrayceConfig();
        Validate(config);
        if (config.AutoApplyPresetIcons && PresetIconCatalog.Apply(config))
        {
            try { Save(config); }
            catch { } // ponytail: auto-fill is cosmetic; failed persistence should not block app launch.
        }
        return config;
    }

    public static void EnsureSample()
    {
        Directory.CreateDirectory(AppDir);
        if (File.Exists(ConfigPath)) return;

        var sample = new TrayceConfig
        {
            Apis = new List<ApiConfig>
            {
                new()
                {
                    Id = "openai",
                    DisplayName = "OpenAI",
                    Provider = "GPT-4o API",
                    LogoPath = PresetIcons.OpenAI,
                    LogoText = "OAI",
                    BrandColor = "#10A37F",
                    ApiKey = "",
                    SourceUrl = "",
                    PollSeconds = 300,
                    Usage = new UsageSnapshot
                    {
                        Calls = 1284,
                        Tokens = 3800000,
                        Quota = 105.42m,
                        QuotaLimit = 250m,
                        Windows = new List<UsageWindow>
                        {
                            new UsageWindow { Label = "5h", Metric = "tokens", Used = 180000, Limit = 500000, ResetsAt = DateTimeOffset.Now.AddHours(2) },
                            new UsageWindow { Label = "7d", Metric = "tokens", Used = 3800000, Limit = 10000000, ResetsAt = DateTimeOffset.Now.AddDays(4) }
                        }
                    }
                },
                new()
                {
                    Id = "github",
                    DisplayName = "GitHub",
                    Provider = "REST API",
                    LogoPath = PresetIcons.GitHub,
                    LogoText = "GH",
                    BrandColor = "#1F6FEB",
                    ApiKey = "",
                    SourceUrl = "",
                    PollSeconds = 300,
                    Usage = new UsageSnapshot
                    {
                        Calls = 314,
                        CallLimit = 5000,
                        Windows = new List<UsageWindow>
                        {
                            new UsageWindow { Label = "1h", Metric = "calls", Used = 314, Limit = 5000, ResetsAt = DateTimeOffset.Now.AddMinutes(43) },
                            new UsageWindow { Label = "7d", Metric = "calls", Used = 2400, Limit = 35000, ResetsAt = DateTimeOffset.Now.AddDays(6) }
                        },
                        Message = "Sample static usage. Add sourceUrl for live JSON."
                    }
                }
            }
        };

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(sample, JsonOptions));
    }

    public static void Save(TrayceConfig config)
    {
        Validate(config);
        Directory.CreateDirectory(AppDir);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, ConfigPath, overwrite: true);
    }

    /// <summary>Persist just the global theme choice without disturbing in-flight API edits.</summary>
    public static void SaveTheme(ThemeMode mode)
    {
        try
        {
            var config = Load();
            config.Theme = SystemTheme.ToConfig(mode);
            Save(config);
        }
        catch
        {
            // ponytail: theme is a cosmetic preference; ignore persistence failures.
        }
    }

    /// <summary>Persist just the global tray-style choice without disturbing in-flight API edits.</summary>
    public static void SaveTrayStyle(TrayStyle style)
    {
        try
        {
            var config = Load();
            config.TrayStyle = TrayStyles.ToConfig(style);
            Save(config);
        }
        catch
        {
            // ponytail: cosmetic preference; ignore persistence failures.
        }
    }

    public static void Validate(TrayceConfig config)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in config.Apis)
        {
            if (string.IsNullOrWhiteSpace(api.Id)) throw new InvalidOperationException("API id is required");
            api.DisplayName = string.IsNullOrWhiteSpace(api.DisplayName) ? api.Id : api.DisplayName;
            api.PollSeconds = Math.Max(1, api.PollSeconds);
            api.BrandColor = NormalizeColor(api.BrandColor);
            api.CustomColor = NormalizeColor(api.CustomColor);
            if (!api.UseBrandColor && string.IsNullOrWhiteSpace(api.CustomColor)) api.UseBrandColor = true;
            api.Usage?.Normalize();
            ValidateWindows(api.Id, api.Usage?.Windows ?? new List<UsageWindow>());
            if (!ids.Add(api.Id)) throw new InvalidOperationException("Duplicate API id: " + api.Id);
        }
    }

    public static void ValidateWindows(string apiId, IEnumerable<UsageWindow> windows)
    {
        foreach (var window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.Label)) throw new InvalidOperationException($"{apiId}: limit label is required");
            if (window.Used < 0) throw new InvalidOperationException($"{apiId}: {window.Label} used value cannot be negative");
            if (window.Limit is <= 0) throw new InvalidOperationException($"{apiId}: {window.Label} limit must be greater than zero");
        }
    }

    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return ColorTranslator.ToHtml(ColorTranslator.FromHtml(value));
        }
        catch
        {
            return value;
        }
    }
}

internal sealed class RoundedPanel : Panel
{
    public Color Back { get; set; } = UiPalette.Card;
    public Color Stroke { get; set; } = UiPalette.Border;
    public int Radius { get; set; } = 8;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Bg));
        using var back = new SolidBrush(Back);
        using var stroke = new Pen(Stroke);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        UiPalette.FillRound(e.Graphics, back, rect, Dpi.Scale(this, Radius));
        UiPalette.DrawRound(e.Graphics, stroke, rect, Dpi.Scale(this, Radius));
        base.OnPaint(e);
    }
}

internal sealed class AppMark : Control
{
    public AppMark()
    {
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(UiPalette.Accent2);
        UiPalette.FillRound(g, back, new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(Width * 0.28f));

        // Bar-chart mark from the prototype path, scaled onto a 24-unit grid.
        using var brush = new SolidBrush(Color.White);
        var s = Width / 24f;
        g.FillRectangle(brush, 5f * s, 13f * s, 3f * s, 6f * s);
        g.FillRectangle(brush, 10.5f * s, 8f * s, 3f * s, 11f * s);
        g.FillRectangle(brush, 16f * s, 11f * s, 3f * s, 8f * s);
    }
}

internal sealed class TitleGlyphButton : Control
{
    private readonly string glyph;
    private bool hover;

    public TitleGlyphButton(string glyph)
    {
        this.glyph = glyph;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover)
        {
            using var back = new SolidBrush(glyph == "close" ? Color.FromArgb(196, 43, 28) : UiPalette.ControlHover);
            e.Graphics.FillRectangle(back, ClientRectangle);
        }

        using var pen = new Pen(glyph == "close" && hover ? Color.White : UiPalette.Text2, Dpi.Scale(this, 1));
        var cx = Width / 2;
        var cy = Height / 2;
        if (glyph == "min") e.Graphics.DrawLine(pen, cx - 6, cy, cx + 6, cy);
        else if (glyph == "max") e.Graphics.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
        else
        {
            e.Graphics.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
            e.Graphics.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
        }
    }
}

internal sealed class ToggleSwitch : Control
{
    private readonly bool on;

    public ToggleSwitch(bool on)
    {
        this.on = on;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var back = new SolidBrush(on ? UiPalette.Accent2 : Color.Transparent);
        using var border = new Pen(on ? UiPalette.Accent2 : UiPalette.Text2);
        UiPalette.FillRound(e.Graphics, back, rect, Height / 2);
        UiPalette.DrawRound(e.Graphics, border, rect, Height / 2);
        using var knob = new SolidBrush(on ? Color.White : UiPalette.Text2);
        var d = Dpi.Scale(this, 13);
        var x = on ? Width - d - Dpi.Scale(this, 4) : Dpi.Scale(this, 4);
        e.Graphics.FillEllipse(knob, x, (Height - d) / 2, d, d);
    }
}

internal sealed class ColorSwatch : Control
{
    private readonly Color color;

    public ColorSwatch(string value)
    {
        color = Parse(value);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(color);
        using var border = new Pen(UiPalette.Border2);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        UiPalette.FillRound(e.Graphics, back, rect, Dpi.Scale(this, 6));
        UiPalette.DrawRound(e.Graphics, border, rect, Dpi.Scale(this, 6));
    }

    private static Color Parse(string value)
    {
        try { return ColorTranslator.FromHtml(value); }
        catch { return UiPalette.Accent2; }
    }
}

internal sealed class TrayPreview : Control
{
    private readonly ApiConfig api;

    public TrayPreview(ApiConfig api)
    {
        this.api = api;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(Color.FromArgb(38, 38, 40));
        using var border = new Pen(UiPalette.Border2);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        UiPalette.FillRound(e.Graphics, back, rect, Dpi.Scale(this, 7));
        UiPalette.DrawRound(e.Graphics, border, rect, Dpi.Scale(this, 7));
        var badge = new Rectangle(Width - Dpi.Scale(this, 31), Dpi.Scale(this, 7), Dpi.Scale(this, 19), Dpi.Scale(this, 19));
        using var color = new SolidBrush(Brand.Color(api, UiPalette.Accent2));
        UiPalette.FillRound(e.Graphics, color, badge, Dpi.Scale(this, 5));
        Logo.Draw(e.Graphics, api, Rectangle.Inflate(badge, -Dpi.Scale(this, 4), -Dpi.Scale(this, 4)), Color.White, small: true);
        using var accent = new SolidBrush(UiPalette.Accent);
        using var track = new SolidBrush(Color.FromArgb(38, Color.White));
        var x = Width - Dpi.Scale(this, 29);
        UiPalette.FillRound(e.Graphics, track, new RectangleF(x, Dpi.Scale(this, 28), Dpi.Scale(this, 16), Dpi.Scale(this, 2.5f)), Dpi.Scale(this, 1.5f));
        UiPalette.FillRound(e.Graphics, accent, new RectangleF(x, Dpi.Scale(this, 28), Dpi.Scale(this, 13), Dpi.Scale(this, 2.5f)), Dpi.Scale(this, 1.5f));
        UiPalette.FillRound(e.Graphics, track, new RectangleF(x, Dpi.Scale(this, 32), Dpi.Scale(this, 16), Dpi.Scale(this, 2.5f)), Dpi.Scale(this, 1.5f));
        UiPalette.FillRound(e.Graphics, accent, new RectangleF(x, Dpi.Scale(this, 32), Dpi.Scale(this, 9), Dpi.Scale(this, 2.5f)), Dpi.Scale(this, 1.5f));
    }
}

internal sealed class ProgressStrip : Control
{
    private readonly decimal ratio;

    public ProgressStrip(decimal ratio)
    {
        this.ratio = ratio;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var back = new SolidBrush(UiPalette.ControlHover);
        using var fill = new SolidBrush(UsageMath.ColorForRatio(ratio));
        var rect = new Rectangle(0, 0, Width, Height);
        UiPalette.FillRound(e.Graphics, back, rect, Height / 2);
        UiPalette.FillRound(e.Graphics, fill, new Rectangle(0, 0, (int)(Width * Math.Clamp(ratio, 0m, 1m)), Height), Height / 2);
    }
}

internal sealed class ColorPickerForm : Form
{
    private static readonly string[] Palette =
    {
        "#D97757", "#10A37F", "#1F6FEB", "#5E6AD2", "#FF9900", "#E0245E", "#8E8EA0",
        "#2DA44E", "#9C27B0", "#FF5630", "#00B8D9", "#111111", "#0078D4", "#36C5F0"
    };

    public string SelectedColor { get; private set; }

    public ColorPickerForm(string current, string brand)
    {
        SelectedColor = current;
        Text = "Tray color";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(252, 166);
        BackColor = UiPalette.Menu;
        ForeColor = UiPalette.Text;
        Font = UiFont.Px(13f);
        ShowInTaskbar = false;
        KeyPreview = true;
        Padding = new Padding(1);

        Controls.Add(new Label { Text = "Tray color", AutoSize = true, Location = new Point(15, 14), ForeColor = UiPalette.Text, Font = UiFont.Px(13f, bold: true) });
        var grid = new Panel { Location = new Point(15, 42), Size = new Size(224, 62), BackColor = Color.Transparent };
        for (var i = 0; i < Palette.Length; i++)
        {
            var hex = Palette[i];
            var swatch = new ColorButton(hex, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase))
            {
                Location = new Point((i % 7) * 32, (i / 7) * 32),
                Size = new Size(26, 26)
            };
            swatch.Click += (_, _) => { SelectedColor = hex; DialogResult = DialogResult.OK; Close(); };
            grid.Controls.Add(swatch);
        }
        Controls.Add(grid);

        var useBrand = new RoundedButton("Use brand color") { Location = new Point(15, 120), Size = new Size(222, 32), Radius = 7 };
        useBrand.Click += (_, _) => { SelectedColor = brand; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(useBrand);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

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

internal sealed class ColorButton : Control
{
    private readonly string hex;
    private readonly bool selected;

    public ColorButton(string hex, bool selected)
    {
        this.hex = hex;
        this.selected = selected;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPalette.Backdrop(this, UiPalette.Card));
        using var brush = new SolidBrush(ColorTranslator.FromHtml(hex));
        using var border = new Pen(selected ? UiPalette.Accent : Color.Transparent, Dpi.Scale(this, 2));
        UiPalette.FillRound(e.Graphics, brush, new Rectangle(1, 1, Width - 2, Height - 2), Dpi.Scale(this, 7));
        UiPalette.DrawRound(e.Graphics, border, new Rectangle(1, 1, Width - 2, Height - 2), Dpi.Scale(this, 7));
    }
}

internal sealed class JsonPreviewForm : Form
{
    public JsonPreviewForm(TrayceConfig config)
    {
        var json = JsonSerializer.Serialize(config, ConfigStore.JsonOptions);
        var fileName = Path.GetFileName(ConfigStore.ConfigPath);
        Text = fileName;
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 520);
        BackColor = UiPalette.Bg;
        ForeColor = UiPalette.Text;
        Font = UiFont.Px(13f);
        ShowInTaskbar = false;
        KeyPreview = true;
        Padding = new Padding(1);

        var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = UiPalette.Bg };
        header.Controls.Add(new Label { Text = fileName, AutoSize = true, Location = new Point(20, 14), ForeColor = UiPalette.Text, Font = UiFont.Px(14.5f, bold: true) });
        header.Controls.Add(new Label { Text = ConfigStore.ConfigPath, AutoSize = true, Location = new Point(20, 38), ForeColor = UiPalette.Text3, Font = UiFont.Px(11.5f, mono: true) });
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiPalette.Border });
        header.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) NativeChrome.DragWindow(Handle); };

        var codeWrap = new Panel { Dock = DockStyle.Fill, BackColor = UiPalette.CodeBg, Padding = new Padding(16, 14, 16, 14) };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = UiPalette.CodeBg,
            ForeColor = UiPalette.CodeFg,
            BorderStyle = BorderStyle.None,
            Font = UiFont.Px(12f, mono: true),
            Text = json
        };
        codeWrap.Controls.Add(box);
        NativeChrome.ApplyDarkScrollbars(box);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = UiPalette.Bg2 };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiPalette.Border });
        var close = new RoundedButton("Close") { Accent = true, Size = new Size(80, 32) };
        close.Click += (_, _) => Close();
        var copy = new RoundedButton("Copy") { Glyph = "copy", Size = new Size(88, 32) };
        copy.Click += (_, _) => { Clipboard.SetText(json); Toaster.Show(this, "Config copied"); };
        footer.Controls.Add(close);
        footer.Controls.Add(copy);

        void LayoutFooter()
        {
            var w = footer.ClientSize.Width;
            close.Location = new Point(w - Dpi.Scale(this, 20) - close.Width, Dpi.Scale(this, 12));
            copy.Location = new Point(close.Left - Dpi.Scale(this, 9) - copy.Width, Dpi.Scale(this, 12));
        }

        footer.Layout += (_, _) => LayoutFooter();

        Controls.Add(codeWrap);
        Controls.Add(footer);
        Controls.Add(header);
        LayoutFooter();
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; ActiveControl = null; };
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
}

internal sealed class ValidationForm : Form
{
    public ValidationForm(string message) : this(new[] { message }) { }

    public ValidationForm(IReadOnlyList<string> errors)
    {
        Text = "Couldn't save changes";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = UiPalette.Bg;
        ForeColor = UiPalette.Text;
        Font = UiFont.Px(12.5f);
        ShowInTaskbar = false;
        KeyPreview = true;
        Padding = new Padding(1);

        const int width = 420;
        Controls.Add(new WarningGlyph { Location = new Point(22, 20), Size = new Size(24, 24) });
        Controls.Add(new Label { Text = "Couldn't save changes", AutoSize = true, Location = new Point(57, 20), ForeColor = UiPalette.Text, Font = UiFont.Px(15f, bold: true) });

        var y = 56;
        foreach (var error in errors)
        {
            Controls.Add(new Label { Text = "•", AutoSize = true, Location = new Point(57, y), ForeColor = UiPalette.Text3, Font = UiFont.Px(12.5f) });
            var label = new Label { AutoSize = true, MaximumSize = new Size(width - 94, 0), Location = new Point(72, y), Text = error, ForeColor = UiPalette.Text2, Font = UiFont.Px(12.5f) };
            Controls.Add(label);
            y += Math.Max(28, label.PreferredHeight + 10);
        }

        y += 12;
        var footer = new Panel { Location = new Point(0, y), Size = new Size(width, 56), BackColor = UiPalette.Bg2 };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiPalette.Border });
        var ok = new RoundedButton("OK") { Accent = true, Size = new Size(80, 32), Location = new Point(width - 20 - 80, 12) };
        ok.Click += (_, _) => Close();
        footer.Controls.Add(ok);
        Controls.Add(footer);

        ClientSize = new Size(width, y + 56);
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
}

internal sealed class WarningGlyph : Control
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(UiPalette.Crit, Dpi.Scale(this, 2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var pts = new[] { new Point(Width / 2, 2), new Point(Width - 2, Height - 2), new Point(2, Height - 2) };
        e.Graphics.DrawPolygon(pen, pts);
        e.Graphics.DrawLine(pen, Width / 2, Dpi.Scale(this, 8), Width / 2, Dpi.Scale(this, 14));
        e.Graphics.DrawEllipse(pen, Width / 2 - 1, Dpi.Scale(this, 18), 2, 2);
    }
}

internal static class StateStore
{
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trayce");
    public static readonly string StatePath = Path.Combine(StateDir, "state.json");

    public static Dictionary<string, UsageSnapshot> Load(string? path = null)
    {
        path ??= StatePath;
        if (!File.Exists(path)) return new Dictionary<string, UsageSnapshot>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, UsageSnapshot>>(File.ReadAllText(path), ConfigStore.JsonOptions);
            if (state is not null)
            {
                foreach (var snapshot in state.Values) snapshot.Normalize();
            }
            return new Dictionary<string, UsageSnapshot>(state ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // ponytail: cache is disposable; ignore corrupt state instead of blocking the tray.
            return new Dictionary<string, UsageSnapshot>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(Dictionary<string, UsageSnapshot> state, string? path = null)
    {
        path ??= StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, ConfigStore.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

internal static class StartupStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Trayce";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(Name) as string, Quote(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Quote(Application.ExecutablePath));
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    private static string Quote(string path) => "\"" + path + "\"";
}

internal sealed class TrayceConfig
{
    public List<ApiConfig> Apis { get; set; } = new();

    public bool AutoApplyPresetIcons { get; set; } = true;

    /// <summary>"system" (default), "light", or "dark".</summary>
    public string? Theme { get; set; }

    /// <summary>"bars" (default), "ring", or "minimal".</summary>
    public string? TrayStyle { get; set; }
}

internal sealed class ApiConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Provider { get; set; }
    public string? LogoPath { get; set; }
    public string? LogoText { get; set; }
    public string? BrandColor { get; set; }
    public bool UseBrandColor { get; set; } = true;
    public string? CustomColor { get; set; }
    public string? ApiKey { get; set; }
    public string? SourceUrl { get; set; }
    public int PollSeconds { get; set; } = 300;
    public UsageSnapshot? Usage { get; set; }
}

internal sealed class UsageSnapshot
{
    public long? Calls { get; set; }
    public long? CallLimit { get; set; }
    public long? Tokens { get; set; }
    public long? TokenLimit { get; set; }
    public decimal? Quota { get; set; }
    public decimal? QuotaLimit { get; set; }
    public List<UsageWindow> Windows { get; set; } = new();
    public DateTimeOffset? ResetsAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
    public string? Message { get; set; }

    public static UsageSnapshot Unknown(string? message = null) => new() { Message = message ?? "Unknown" };
    public static UsageSnapshot Error(string message) => new() { Message = "Refresh failed: " + message };

    public UsageSnapshot WithMessage(string message) => new()
    {
        Calls = Calls,
        CallLimit = CallLimit,
        Tokens = Tokens,
        TokenLimit = TokenLimit,
        Quota = Quota,
        QuotaLimit = QuotaLimit,
        Windows = (Windows ?? new List<UsageWindow>()).Select(w => w.Clone()).ToList(),
        ResetsAt = ResetsAt,
        ObservedAt = ObservedAt,
        Message = message
    };

    public UsageSnapshot WithObservedAt(DateTimeOffset observedAt)
    {
        ObservedAt = observedAt;
        return this;
    }

    public UsageSnapshot Clone() => new()
    {
        Calls = Calls,
        CallLimit = CallLimit,
        Tokens = Tokens,
        TokenLimit = TokenLimit,
        Quota = Quota,
        QuotaLimit = QuotaLimit,
        Windows = (Windows ?? new List<UsageWindow>()).Select(w => w.Clone()).ToList(),
        ResetsAt = ResetsAt,
        ObservedAt = ObservedAt,
        Message = Message
    };

    public UsageSnapshot Normalize()
    {
        Windows ??= new List<UsageWindow>();
        return this;
    }
}

internal sealed class UsageWindow
{
    public string Label { get; set; } = "";
    public string? Metric { get; set; }
    public decimal Used { get; set; }
    public decimal? Limit { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string? Message { get; set; }

    public UsageWindow Clone() => new()
    {
        Label = Label,
        Metric = Metric,
        Used = Used,
        Limit = Limit,
        ResetsAt = ResetsAt,
        Message = Message
    };
}

internal static class UsageMath
{
    public static decimal? Ratio(UsageSnapshot usage)
    {
        var ratios = Ratios(usage).ToList();
        return ratios.Count == 0 ? null : ratios.Max();
    }

    public static IEnumerable<decimal> Ratios(UsageSnapshot usage)
    {
        var ratios = new List<decimal>();
        foreach (var window in usage.Windows)
        {
            if (window.Limit is > 0) ratios.Add(window.Used / window.Limit.Value);
        }

        AddRatio(ratios, usage.Calls, usage.CallLimit);
        AddRatio(ratios, usage.Tokens, usage.TokenLimit);
        AddRatio(ratios, usage.Quota, usage.QuotaLimit);
        return ratios;
    }

    public static string Badge(UsageSnapshot usage, bool stale)
    {
        if (stale) return "!";

        var ratio = Ratio(usage);
        if (ratio.HasValue) return ratio.Value >= 1m ? "!" : Math.Round(ratio.Value * 100m).ToString("0");
        if (usage.Tokens > 0) return Format.Compact(usage.Tokens.Value);
        if (usage.Calls > 0) return Format.Compact(usage.Calls.Value);
        return "?";
    }

    public static Color ColorFor(UsageSnapshot usage, bool stale)
    {
        if (stale) return UiPalette.Warn;
        var ratio = Ratio(usage);
        if (!ratio.HasValue) return UiPalette.Text3;
        return ColorForRatio(ratio.Value);
    }

    public static Color ColorForRatio(decimal ratio)
    {
        if (ratio >= 0.9m) return UiPalette.Crit;
        if (ratio >= 0.7m) return UiPalette.Warn;
        return UiPalette.Accent;
    }

    public static List<UsageWindow> WindowsForDisplay(UsageSnapshot usage)
    {
        if (usage.Windows.Count > 0) return usage.Windows;

        var windows = new List<UsageWindow>();
        AddWindow(windows, "Calls", "calls", usage.Calls, usage.CallLimit, usage.ResetsAt);
        AddWindow(windows, "Tokens", "tokens", usage.Tokens, usage.TokenLimit, usage.ResetsAt);
        if (usage.Quota.HasValue) windows.Add(new UsageWindow { Label = "Quota", Metric = "USD", Used = usage.Quota.Value, Limit = usage.QuotaLimit, ResetsAt = usage.ResetsAt });
        return windows.Count > 0 ? windows : new List<UsageWindow> { new() { Label = "Usage", Used = 0, Message = "No usage values" } };
    }

    private static void AddRatio(List<decimal> ratios, long? used, long? limit)
    {
        if (used.HasValue && limit is > 0) ratios.Add((decimal)used.Value / limit.Value);
    }

    private static void AddRatio(List<decimal> ratios, decimal? used, decimal? limit)
    {
        if (used.HasValue && limit is > 0) ratios.Add(used.Value / limit.Value);
    }

    private static void AddWindow(List<UsageWindow> windows, string label, string metric, long? used, long? limit, DateTimeOffset? resetsAt)
    {
        if (used.HasValue) windows.Add(new UsageWindow { Label = label, Metric = metric, Used = used.Value, Limit = limit, ResetsAt = resetsAt });
    }
}

internal static class Format
{
    public static string Count(long? value) => value?.ToString("N0") ?? "-";
    public static string Money(decimal? value) => value.HasValue ? "$" + value.Value.ToString("N2") : "-";
    public static string Percent(decimal? value) => value.HasValue ? (value.Value * 100m).ToString("0.#") + "%" : "-";

    public static string Compact(long value) =>
        value >= 1_000_000 ? (value / 1_000_000d).ToString("0.#") + "M" :
        value >= 1_000 ? (value / 1_000d).ToString("0.#") + "K" :
        value.ToString("0");

    public static string Compact(decimal value, string? metric)
    {
        var m = (metric ?? "").ToLowerInvariant();
        if (m is "dollars" or "usd") return "$" + Decimal(value);

        // Tokens compact (M with one decimal, K rounded); other metrics show full comma-grouped values.
        if (m == "tokens")
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000m) return (value / 1_000_000m).ToString("0.0") + "M";
            if (abs >= 1_000m) return Math.Round(value / 1_000m).ToString("0") + "K";
        }

        return Decimal(value);
    }

    public static string Summary(UsageSnapshot usage, bool stale)
    {
        var parts = usage.Windows.Count > 0
            ? usage.Windows.Select(WindowSummary).ToList()
            : new List<string>
        {
            $"{Count(usage.Calls)} calls",
            $"{Count(usage.Tokens)} tokens",
            $"{Percent(UsageMath.Ratio(usage))} used"
        };

        if (stale) parts.Add("stale");
        if (!string.IsNullOrWhiteSpace(usage.Message)) parts.Add(usage.Message);
        return string.Join(" | ", parts);
    }

    public static string WindowSummary(UsageWindow window)
    {
        var ratio = window.Limit is > 0 ? Percent(window.Used / window.Limit.Value) : "-";
        return $"{window.Label}: {ratio}";
    }

    public static string Decimal(decimal value) => value % 1m == 0m ? value.ToString("N0") : value.ToString("N2");

    public static string Relative(DateTimeOffset when)
    {
        var diff = DateTimeOffset.Now - when;
        if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
        if (diff.TotalSeconds < 45) return "just now";
        if (diff.TotalMinutes < 60) return Math.Max(1, (int)diff.TotalMinutes) + " min ago";
        if (diff.TotalHours < 24) return (int)diff.TotalHours + ((int)diff.TotalHours == 1 ? " hour ago" : " hours ago");
        if (diff.TotalDays < 7) return (int)diff.TotalDays + ((int)diff.TotalDays == 1 ? " day ago" : " days ago");
        return when.ToString("MMM d");
    }

    public static string Reset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return "";
        var diff = resetsAt.Value - DateTimeOffset.Now;
        if (diff <= TimeSpan.Zero) return "now";
        if (diff.TotalHours < 1) return "in " + Math.Max(1, (int)diff.TotalMinutes) + "m";
        if (diff.TotalHours < 24)
        {
            var hours = (int)diff.TotalHours;
            var minutes = diff.Minutes;
            return minutes > 0 ? $"in {hours}h {minutes}m" : $"in {hours}h";
        }
        if (diff.TotalDays < 7) return resetsAt.Value.ToString("ddd HH:mm");
        return resetsAt.Value.ToString("MMM d");
    }
}

internal static class SelfTest
{
    public static void Run()
    {
        var api = new ApiConfig { Id = "openai", DisplayName = "OpenAI", LogoText = "AI", BrandColor = "#111827" };
        var usage = new UsageSnapshot { Tokens = 900, TokenLimit = 1000 };
        Check(UsageMath.Ratio(usage) == 0.9m, "ratio");
        Check(UsageMath.Badge(usage, stale: false) == "90", "badge");
        using (var path = UiPalette.RoundPath(new Rectangle(0, 0, 100, 7), 6))
        {
            Check(path.GetBounds().Height <= 7.1f, "rounded path clamps radius");
        }

        using var icon = IconRenderer.Render(api, usage, stale: false);
        Check(icon.Width > 0 && icon.Height > 0, "icon");

        var windowed = new UsageSnapshot
        {
            Windows = new List<UsageWindow>
            {
                new() { Label = "5h", Used = 40, Limit = 100 },
                new() { Label = "7d", Used = 90, Limit = 100 }
            }
        };
        Check(UsageMath.Ratio(windowed) == 0.9m, "window ratio");

        using var details = new DetailsForm(api, windowed, stale: false, previewMode: true);
        details.CreateControl();
        Check(details.ClientSize.Width > 0 && details.Controls.Count > 0, "details form");

        using var closeProbe = new DetailsForm(api, windowed, stale: false);
        closeProbe.Bounds = new Rectangle(100, 100, 200, 200);
        Check(!closeProbe.ShouldCloseForOutsideClick(new Point(120, 120), MouseButtons.Left), "inside click stays open");
        Check(closeProbe.ShouldCloseForOutsideClick(new Point(10, 10), MouseButtons.Left), "outside click closes");

        var inferred = PresetIconCatalog.Find("Claude proxy", "https://api.anthropic.com/v1/messages");
        Check(inferred.HasValue && inferred.Value.IconPath == PresetIcons.Anthropic && inferred.Value.BrandColor == "#D97757", "preset icon inference");
        var azure = PresetIconCatalog.Find("OpenAI usage", "https://trayce.openai.azure.com/openai/deployments/gpt-4o");
        Check(azure.HasValue && azure.Value.IconPath == PresetIcons.AzureOpenAI, "preset icon azure host");
        var configWithMissingIcon = new TrayceConfig
        {
            Apis = new List<ApiConfig> { new() { Id = "claude", DisplayName = "Claude proxy", LogoText = "API", BrandColor = "#0078D4" } }
        };
        Check(PresetIconCatalog.Apply(configWithMissingIcon), "preset config apply changed");
        Check(configWithMissingIcon.Apis[0].LogoPath == PresetIcons.Anthropic && configWithMissingIcon.Apis[0].BrandColor == "#D97757", "preset config apply values");
        var customLogo = Path.GetTempFileName();
        try
        {
            var custom = new ApiConfig { Id = "openai", DisplayName = "OpenAI", LogoPath = customLogo, LogoText = "X", BrandColor = "#123456" };
            Check(!PresetIconCatalog.Apply(custom), "preset preserves custom values");
        }
        finally
        {
            File.Delete(customLogo);
        }

        using var controller = new DetailsPopupController();
        var first = new DetailsForm(api, windowed, stale: false, previewMode: true);
        controller.Show(first);
        Check(controller.HasActive, "details controller active");
        var second = new DetailsForm(api, windowed, stale: false, previewMode: true);
        controller.Show(second);
        Application.DoEvents();
        Check(first.IsDisposed, "details controller replaces active popup");
        controller.Dispose();
        Application.DoEvents();
        Check(second.IsDisposed, "details controller disposes active popup");

        var previewPath = Path.Combine(Path.GetTempPath(), "trayce-preview-" + Guid.NewGuid() + ".png");
        try
        {
            PreviewRenderer.Render(previewPath);
            Check(new FileInfo(previewPath).Length > 1000, "preview render");
        }
        finally
        {
            File.Delete(previewPath);
        }

        var config = new TrayceConfig
        {
            Apis = new List<ApiConfig>
            {
                new() { Id = "a" },
                new() { Id = "A" }
            }
        };

        try
        {
            ConfigStore.Validate(config);
            throw new InvalidOperationException("duplicate config was accepted");
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            ConfigStore.ValidateWindows("api", new[] { new UsageWindow { Label = "5h", Used = 1, Limit = 0 } });
            throw new InvalidOperationException("invalid limit was accepted");
        }
        catch (InvalidOperationException)
        {
        }

        var statePath = Path.Combine(Path.GetTempPath(), "trayce-self-test-" + Guid.NewGuid() + ".json");
        try
        {
            StateStore.Save(new Dictionary<string, UsageSnapshot> { ["api"] = usage }, statePath);
            Check(StateStore.Load(statePath)["api"].Tokens == 900, "state");
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
    }
}
