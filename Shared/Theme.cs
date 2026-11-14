using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RainDrop.Shared;

/// <summary>Central design tokens. UI code must use these instead of ad-hoc colors/sizes.</summary>
internal static class Theme
{
    // Surfaces
    internal static readonly Color Background = Color.FromArgb(30, 31, 34);
    internal static readonly Color Surface = Color.FromArgb(43, 45, 49);
    internal static readonly Color SurfaceAlt = Color.FromArgb(49, 51, 56);
    internal static readonly Color SurfaceHover = Color.FromArgb(53, 55, 60);
    internal static readonly Color SurfacePressed = Color.FromArgb(64, 68, 75);
    internal static readonly Color Border = Color.FromArgb(63, 65, 71);
    internal static readonly Color InputBackground = Color.FromArgb(30, 31, 34);

    // Text (contrast-tested against the surfaces where they are used)
    internal static readonly Color TextPrimary = Color.FromArgb(242, 243, 245);
    internal static readonly Color TextSecondary = Color.FromArgb(219, 222, 225);
    internal static readonly Color TextMuted = Color.FromArgb(181, 186, 193);
    internal static readonly Color TextDisabled = Color.FromArgb(148, 155, 164);
    internal static readonly Color OnAccent = Color.White;

    // One accent family + semantic status colors
    internal static readonly Color Accent = Color.FromArgb(71, 82, 196);
    internal static readonly Color AccentHover = Color.FromArgb(88, 101, 242);
    internal static readonly Color AccentPressed = Color.FromArgb(60, 69, 168);
    internal static readonly Color Online = Color.FromArgb(74, 222, 128);
    internal static readonly Color Waiting = Color.FromArgb(251, 191, 36);
    internal static readonly Color Danger = Color.FromArgb(248, 113, 113);
    internal static readonly Color DangerBackground = Color.FromArgb(153, 27, 27);
    internal static readonly Color DangerHover = Color.FromArgb(185, 28, 28);
    internal static readonly Color Offline = Color.FromArgb(181, 186, 193);

    // 4/8px spacing grid
    internal const int Space1 = 4;
    internal const int Space2 = 8;
    internal const int Space3 = 12;
    internal const int Space4 = 16;
    internal const int Space5 = 24;
    internal const int Space6 = 32;
    internal const int RadiusSmall = 4;
    internal const int RadiusMedium = 8;
    internal const int RadiusLarge = 12;
    internal const int ButtonHeight = 32;
    internal const int ButtonHeightLarge = 36;
    internal const int InputHeight = 32;
    internal const int IconSize = 16;
    internal const int IconSizeLarge = 20;
    internal const int SidebarWidth = 260;
    internal const int ToolbarHeight = 56;
    internal const int StatusHeight = 28;
    internal const int CardWidth = 340;
    internal const int CardHeight = 240;

    // Shared font cache. Controls do not own these fonts; never dispose them.
    internal static readonly Font FontBody = new("Segoe UI", 9F);
    internal static readonly Font FontBodyStrong = new("Segoe UI", 9F, FontStyle.Bold);
    internal static readonly Font FontSmall = new("Segoe UI", 8.5F);
    internal static readonly Font FontCaption = new("Segoe UI", 8F);
    internal static readonly Font FontCaptionStrong = new("Segoe UI", 8F, FontStyle.Bold);
    internal static readonly Font FontMedium = new("Segoe UI", 10F);
    internal static readonly Font FontMediumStrong = new("Segoe UI", 10F, FontStyle.Bold);
    internal static readonly Font FontTitle = new("Segoe UI", 12F, FontStyle.Bold);
    internal static readonly Font FontHeading = new("Segoe UI", 14F, FontStyle.Bold);
    internal static readonly Font FontChat = new("Segoe UI", 10F);
    internal static readonly Font FontChatItalic = new("Segoe UI", 10F, FontStyle.Italic);
    internal static readonly Font FontMono = new("Consolas", 10F);
    internal static readonly Font FontClock = new("Segoe UI", 32F, FontStyle.Bold);
    internal static readonly Font FontLockMessage = new("Segoe UI", 20F, FontStyle.Bold);

    private static readonly ConcurrentDictionary<int, SolidBrush> Brushes = new();
    private static readonly ConcurrentDictionary<int, Pen> Pens = new();
    private static readonly ToolTip ToolTip = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 6000 };

    internal static SolidBrush Brush(Color color) => Brushes.GetOrAdd(color.ToArgb(), _ => new SolidBrush(color));
    internal static Pen Pen(Color color, float width = 1F) =>
        Pens.GetOrAdd(unchecked(color.ToArgb() * 31 + (int)(width * 10)), _ => new Pen(color, width));

    internal static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * amount),
            (int)Math.Round(a.R + (b.R - a.R) * amount),
            (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }

    internal static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(0, radius * 2);
        var path = new GraphicsPath();
        if (d == 0 || bounds.Width <= d || bounds.Height <= d)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void SetToolTip(Control control, string text)
    {
        ToolTip.SetToolTip(control, text);
        if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = text;
    }

    internal static void ConfigureForm(Form form, Size? minimumSize = null)
    {
        form.Font = FontBody;
        form.BackColor = Background;
        form.ForeColor = TextPrimary;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.Icon = AppIcon.Load();
        if (minimumSize is { } min) form.MinimumSize = min;
        TryDarkTitleBar(form);
    }

    internal static void StyleInput(TextBox box)
    {
        box.Font = FontBody;
        box.BackColor = InputBackground;
        box.ForeColor = TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    internal static void StyleList(ListBox list)
    {
        list.Font = FontBody;
        list.BackColor = Surface;
        list.ForeColor = TextSecondary;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
    }

    internal static void StyleList(ListView list)
    {
        list.Font = FontBody;
        list.BackColor = Surface;
        list.ForeColor = TextSecondary;
        list.BorderStyle = BorderStyle.None;
        list.GridLines = false;
        list.FullRowSelect = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    }

    internal static void StyleMenu(ToolStrip menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.BackColor = Surface;
        menu.ForeColor = TextSecondary;
        menu.Font = FontBody;
        menu.Padding = new Padding(Space1);
    }

    internal static Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = FontCaptionStrong,
        ForeColor = TextMuted,
        Margin = new Padding(Space2, Space2, Space2, Space1),
    };

    internal static Size ScaleSize(Size size, float scale) =>
        new(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));

    private static void TryDarkTitleBar(Form form)
    {
        void Apply()
        {
            if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
            try
            {
                int enabled = 1;
                _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, Marshal.SizeOf<int>());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
                // Older Windows versions may not expose this attribute; the client area remains themed.
                Diag.Warn("Theme", "Dark title bar is unavailable on this Windows version", ex);
            }
        }
        if (form.IsHandleCreated) Apply();
        else form.HandleCreated += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        internal MenuRenderer() : base(new MenuColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextSecondary : TextDisabled;
            base.OnRenderItemText(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => SurfaceHover;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
