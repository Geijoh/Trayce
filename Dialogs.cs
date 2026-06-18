using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Trayce;

/// <summary>Floating, auto-dismissing toast near the bottom-centre of the owner window.</summary>
internal static class Toaster
{
    public static void Show(IWin32Window? owner, string message)
    {
        var ownerForm = owner as Form;
        if (ownerForm is { IsDisposed: false } && ownerForm.Left < -5000) return; // skip during preview capture

        var toast = new ToastForm(message);
        toast.ShowFor(ownerForm);
    }
}

internal sealed class ToastForm : Form
{
    private readonly string message;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1900 };

    public ToastForm(string message)
    {
        this.message = message;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BackColor = UiPalette.Menu;
        Font = UiFont.Px(12.5f, bold: false);

        using var g = CreateGraphics();
        var size = g.MeasureString(message, Font);
        ClientSize = new Size((int)size.Width + 36, 40);

        timer.Tick += (_, _) => Close();
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowFor(Form? owner)
    {
        var area = owner is { IsDisposed: false, Visible: true }
            ? owner.Bounds
            : Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - Dpi.Scale(this, 64));
        Show();
        timer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindows11Corners(this);
        NativeChrome.ApplyWindows11Backdrop(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Menu);
        using var border = new Pen(UiPalette.Border2);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var text = new SolidBrush(UiPalette.Text);
        var size = g.MeasureString(message, Font);
        g.DrawString(message, Font, text, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
    }
}

/// <summary>Prototype-style logo chooser: description, dashed drop/browse area, sample-file list, Cancel/Choose.</summary>
internal sealed class LogoPickerForm : Form
{
    private static readonly string[] Samples = { "mark-512.png", "logo-light.svg", "logo-dark.svg", "icon.ico" };
    private readonly Panel dropArea;
    private readonly RoundedButton choose;
    private string? pickedName;

    public string? SelectedPath { get; private set; }

    public LogoPickerForm(ApiConfig api)
    {
        _ = api;
        Text = "Choose a logo image";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 448);
        BackColor = UiPalette.Bg;
        ForeColor = UiPalette.Text;
        Font = UiFont.Px(12.5f);
        AllowDrop = true;
        KeyPreview = true;
        Padding = new Padding(1);

        Controls.Add(new Label { Text = "Choose a logo image", AutoSize = true, Location = new Point(20, 18), Font = UiFont.Px(15f, bold: true), ForeColor = UiPalette.Text });
        Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Location = new Point(20, 48),
            Text = "Select a PNG, JPG, BMP, or ICO file. Trayce copies it to %APPDATA%\\Trayce\\logos and stores a stable relative path.",
            ForeColor = UiPalette.Text2,
            Font = UiFont.Px(12.5f)
        });

        dropArea = new Panel { Location = new Point(20, 116), Size = new Size(400, 64), BackColor = UiPalette.Card, Cursor = Cursors.Hand, AllowDrop = true };
        dropArea.Paint += PaintDrop;
        dropArea.Click += (_, _) => Browse();
        dropArea.DragEnter += OnDragEnter;
        dropArea.DragDrop += OnDragDrop;
        Controls.Add(dropArea);

        var list = new FlowLayoutPanel { Location = new Point(20, 192), Size = new Size(400, 152), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        foreach (var name in Samples) list.Controls.Add(SampleRow(name));
        Controls.Add(list);

        Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(400, 0), Location = new Point(20, 350), Text = "If the image can’t be loaded, Trayce falls back to the text initials.", ForeColor = UiPalette.Text3, Font = UiFont.Px(11f) });

        var footer = new Panel { Location = new Point(0, 392), Size = new Size(ClientSize.Width, 56), BackColor = UiPalette.Bg2 };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiPalette.Border });
        var cancel = new RoundedButton("Cancel") { Size = new Size(84, 32), Location = new Point(ClientSize.Width - 220, 12) };
        cancel.Click += (_, _) => Close();
        choose = new RoundedButton("Choose") { Accent = true, Size = new Size(96, 32), Location = new Point(ClientSize.Width - 116, 12), Enabled = false };
        choose.Click += (_, _) => { if (SelectedPath is not null) { DialogResult = DialogResult.OK; Close(); } };
        footer.Controls.Add(cancel);
        footer.Controls.Add(choose);
        Controls.Add(footer);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private Control SampleRow(string name)
    {
        var row = new Panel { Size = new Size(394, 33), Margin = new Padding(0, 0, 0, 5), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        row.Click += (_, _) => Browse();
        row.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(UiPalette.Backdrop(row, UiPalette.Bg));
            var picked = string.Equals(pickedName, name, StringComparison.OrdinalIgnoreCase);
            int S(int value) => Dpi.Scale(row, value);
            using (var back = new SolidBrush(picked ? UiPalette.ControlHover : UiPalette.Card)) UiPalette.FillRound(g, back, new Rectangle(0, 0, row.Width - 1, row.Height - 1), S(7));
            using (var border = new Pen(picked ? UiPalette.Accent : UiPalette.Border2)) UiPalette.DrawRound(g, border, new Rectangle(0, 0, row.Width - 1, row.Height - 1), S(7));
            IconPainter.Draw(g, "image", new Rectangle(S(11), (row.Height - S(16)) / 2, S(16), S(16)), UiPalette.Text2);
            using var text = new SolidBrush(UiPalette.Text);
            var font = UiFont.Px(13f, mono: true);
            var size = g.MeasureString(name, font);
            g.DrawString(name, font, text, S(36), (row.Height - size.Height) / 2f);
            if (picked) IconPainter.Draw(g, "check", new Rectangle(row.Width - S(28), (row.Height - S(15)) / 2, S(15), S(15)), UiPalette.Accent);
        };
        return row;
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

    private void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.ico|All files|*.*",
            Title = "Choose a logo image"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) Pick(dialog.FileName);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Pick(files[0]);
    }

    private void Pick(string path)
    {
        SelectedPath = path;
        pickedName = Path.GetFileName(path);
        choose.Enabled = true;
        Invalidate(true);
    }

    private void PaintDrop(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int S(int value) => Dpi.Scale(dropArea, value);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiPalette.Card);
        using var pen = new Pen(UiPalette.Border2) { DashStyle = DashStyle.Dash };
        UiPalette.DrawRound(g, pen, new Rectangle(0, 0, dropArea.Width - 1, dropArea.Height - 1), S(8));

        if (pickedName is not null)
        {
            IconPainter.Draw(g, "image", new Rectangle(dropArea.Width / 2 - S(70), dropArea.Height / 2 - S(8), S(16), S(16)), UiPalette.Accent);
            using var picked = new SolidBrush(UiPalette.Text);
            var font = UiFont.Px(12.5f, mono: true);
            g.DrawString(pickedName, font, picked, dropArea.Width / 2 - S(48), dropArea.Height / 2 - S(8));
            return;
        }

        IconPainter.Draw(g, "upload", new Rectangle(dropArea.Width / 2 - S(11), S(12), S(22), S(22)), UiPalette.Text3);
        using var text = new SolidBrush(UiPalette.Text3);
        var msg = "Drag an image here, or pick a sample file below";
        var size = g.MeasureString(msg, UiFont.Px(12f));
        g.DrawString(msg, UiFont.Px(12f), text, (dropArea.Width - size.Width) / 2f, S(40));
    }
}

internal static class PresetIcons
{
    public const string Anthropic = "assets/presets/anthropic.svg";
    public const string OpenAI = "assets/presets/openai.svg";
    public const string GoogleGemini = "assets/presets/google-gemini.svg";
    public const string MistralAI = "assets/presets/mistral-ai.svg";
    public const string XAIGrok = "assets/presets/xai-grok.svg";
    public const string Perplexity = "assets/presets/perplexity.svg";
    public const string DeepSeek = "assets/presets/deepseek.svg";
    public const string Cohere = "assets/presets/cohere.svg";
    public const string Groq = "assets/presets/groq.svg";
    public const string TogetherAI = "assets/presets/together-ai.svg";
    public const string OpenRouter = "assets/presets/openrouter.svg";
    public const string HuggingFace = "assets/presets/hugging-face.svg";
    public const string Replicate = "assets/presets/replicate.svg";
    public const string FireworksAI = "assets/presets/fireworks-ai.svg";
    public const string AzureOpenAI = "assets/presets/azure-openai.svg";
    public const string AWSBedrock = "assets/presets/aws-bedrock.svg";
    public const string ElevenLabs = "assets/presets/elevenlabs.svg";
    public const string StabilityAI = "assets/presets/stability-ai.svg";

    public static string? ForId(string id) => id.ToLowerInvariant() switch
    {
        "anthropic" => Anthropic,
        "openai" => OpenAI,
        "googlegemini" => GoogleGemini,
        "mistralai" => MistralAI,
        "xaigrok" => XAIGrok,
        "perplexity" => Perplexity,
        "deepseek" => DeepSeek,
        "cohere" => Cohere,
        "groq" => Groq,
        "togetherai" => TogetherAI,
        "openrouter" => OpenRouter,
        "huggingface" => HuggingFace,
        "replicate" => Replicate,
        "fireworksai" => FireworksAI,
        "azureopenai" => AzureOpenAI,
        "awsbedrock" or "aws" => AWSBedrock,
        "elevenlabs" => ElevenLabs,
        "stabilityai" => StabilityAI,
        _ => null
    };
}

internal readonly record struct ApiPreset(string Name, string Provider, string Initials, string BrandColor, string IconPath);

/// <summary>"Add API" service-preset picker — a grid of common AI services plus a blank/custom option.</summary>
internal sealed class PresetPickerForm : Form
{
    private static readonly ApiPreset[] Presets =
    {
        new("Anthropic", "Claude API", "A", "#D97757", PresetIcons.Anthropic),
        new("OpenAI", "GPT API", "OAI", "#10A37F", PresetIcons.OpenAI),
        new("Google Gemini", "Gemini API", "G", "#1A73E8", PresetIcons.GoogleGemini),
        new("Mistral AI", "Mistral API", "M", "#FA520F", PresetIcons.MistralAI),
        new("xAI Grok", "Grok API", "xAI", "#111111", PresetIcons.XAIGrok),
        new("Perplexity", "Sonar API", "P", "#20808D", PresetIcons.Perplexity),
        new("DeepSeek", "DeepSeek API", "DS", "#4D6BFE", PresetIcons.DeepSeek),
        new("Cohere", "Command API", "Co", "#39594D", PresetIcons.Cohere),
        new("Groq", "LPU Inference", "Gq", "#F55036", PresetIcons.Groq),
        new("Together AI", "Inference API", "T", "#0F6FFF", PresetIcons.TogetherAI),
        new("OpenRouter", "Router API", "OR", "#6566F1", PresetIcons.OpenRouter),
        new("Hugging Face", "Inference API", "HF", "#FF9D00", PresetIcons.HuggingFace),
        new("Replicate", "Predictions", "R", "#1A1A1A", PresetIcons.Replicate),
        new("Fireworks AI", "Inference API", "Fw", "#5019C5", PresetIcons.FireworksAI),
        new("Azure OpenAI", "Azure API", "Az", "#0078D4", PresetIcons.AzureOpenAI),
        new("AWS Bedrock", "Bedrock API", "AWS", "#FF9900", PresetIcons.AWSBedrock),
        new("ElevenLabs", "Audio API", "11", "#0B0B0B", PresetIcons.ElevenLabs),
        new("Stability AI", "Image API", "St", "#A21CAF", PresetIcons.StabilityAI)
    };

    /// <summary>The chosen preset, or null with DialogResult.OK meaning "Blank / custom".</summary>
    public ApiPreset? Preset { get; private set; }

    public PresetPickerForm()
    {
        Text = "Add an API";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(608, 500);
        BackColor = UiPalette.Bg;
        ForeColor = UiPalette.Text;
        Font = UiFont.Px(13f);
        ShowInTaskbar = false;
        KeyPreview = true;
        Padding = new Padding(1);

        Controls.Add(new Label { Text = "Add an API", AutoSize = true, Location = new Point(20, 18), Font = UiFont.Px(15f, bold: true), ForeColor = UiPalette.Text });
        Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Location = new Point(20, 46),
            Text = "Pick a service to start from — its logo mark and brand color are filled in automatically. Everything stays editable afterward.",
            ForeColor = UiPalette.Text2,
            Font = UiFont.Px(12.5f)
        });

        var grid = new FlowLayoutPanel { Location = new Point(18, 78), Size = new Size(572, 360), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent };
        foreach (var preset in Presets) grid.Controls.Add(Card(preset));
        Controls.Add(grid);

        var footer = new Panel { Location = new Point(0, 444), Size = new Size(ClientSize.Width, 56), BackColor = UiPalette.Bg2 };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiPalette.Border });
        var blank = new RoundedButton("Blank / custom") { Glyph = "plus", Size = new Size(150, 32), Location = new Point(20, 12) };
        blank.Click += (_, _) => { Preset = null; DialogResult = DialogResult.OK; Close(); };
        var cancel = new RoundedButton("Cancel") { Size = new Size(84, 32), Location = new Point(ClientSize.Width - 104, 12) };
        cancel.Click += (_, _) => Close();
        footer.Controls.Add(blank);
        footer.Controls.Add(cancel);
        Controls.Add(footer);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private Control Card(ApiPreset preset)
    {
        var card = new Panel { Size = new Size(180, 52), Margin = new Padding(0, 0, 8, 8), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        card.Click += (_, _) => { Preset = preset; DialogResult = DialogResult.OK; Close(); };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(UiPalette.Backdrop(card, UiPalette.Bg));
            int S(int value) => Dpi.Scale(card, value);
            using (var back = new SolidBrush(UiPalette.Card)) UiPalette.FillRound(g, back, new Rectangle(0, 0, card.Width - 1, card.Height - 1), S(8));
            using (var border = new Pen(UiPalette.Border2)) UiPalette.DrawRound(g, border, new Rectangle(0, 0, card.Width - 1, card.Height - 1), S(8));

            var badge = new Rectangle(S(11), (card.Height - S(30)) / 2, S(30), S(30));
            Color color;
            try { color = ColorTranslator.FromHtml(preset.BrandColor); } catch { color = UiPalette.Accent2; }
            using (var b = new SolidBrush(color)) UiPalette.FillRound(g, b, badge, S(8));
            var preview = new ApiConfig
            {
                DisplayName = preset.Name,
                LogoPath = preset.IconPath,
                LogoText = preset.Initials,
                BrandColor = preset.BrandColor
            };
            Logo.Draw(g, preview, Rectangle.Inflate(badge, -S(6), -S(6)), Color.White, small: true);

            using var name = new SolidBrush(UiPalette.Text);
            using var prov = new SolidBrush(UiPalette.Text3);
            g.DrawString(preset.Name, UiFont.Px(12.5f, bold: true), name, badge.Right + S(11), S(8));
            g.DrawString(preset.Provider, UiFont.Px(10.5f), prov, badge.Right + S(11), S(28));
        };
        return card;
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

