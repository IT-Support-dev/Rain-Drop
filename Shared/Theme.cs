using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RainDrop.Shared;

/// <summary>Central design tokens — Swiss/Minimal style (Phase U3).
///
/// พื้นเรียบ ไม่มีเงา ไม่มีมุมมน ไม่มีกรอบซ้อน ใช้เส้นบาง 1px สีจางเป็นตัวแบ่งโซน
/// สี: พื้นหลัง #121212 ทั้งหน้าต่าง พื้นผิวยกขึ้น #1A1A1A ข้อความขาว/เทา
/// สีเน้นเดียว #3B82F6 (ฟ้า) ใช้เฉพาะปุ่มหลักและสถานะที่เลือก
/// สถานะเครื่อง: จุดสี 8px + ข้อความ เท่านั้น (ไม่ใช้พื้นหลังแถบสี)
/// タイポกราฟี: Segoe UI 4 ระดับ (หัวเรื่อง 20 semibold, ชื่อเครื่อง 13 semibold, เนื้อหา 11, คำอธิบาย 9.5)
/// กริด 8px: Space1=8, Space2=16, Space3=24, Space4=32
/// ไอคอน: เส้น 1.5px ขนาด 16/20px สีตามข้อความ (ไม่ขยายเป็นกราฟิกใหญ่)</summary>
internal static class Theme
{
    // ---------- สีพื้นผิว (zone brightness) ----------
    internal static readonly Color Background = Color.FromArgb(18, 18, 18);   // #121212 พื้นหลังทั้งหน้าต่าง
    internal static readonly Color Surface = Color.FromArgb(26, 26, 26);      // #1A1A1A พื้นผิวยกขึ้นหนึ่งระดับ
    internal static readonly Color SurfaceAlt = Color.FromArgb(34, 34, 34);   // พื้นผิวระดับรอง (dialogs, controls)
    internal static readonly Color SurfaceHover = Color.FromArgb(42, 42, 42); // hover state
    internal static readonly Color SurfacePressed = Color.FromArgb(50, 50, 50); // pressed state
    internal static readonly Color Border = Color.FromArgb(63, 65, 71);       // เส้นขอบ 1px สีจาง
    internal static readonly Color Divider = Color.FromArgb(63, 65, 71);      // เส้นแบ่งโซน
    internal static readonly Color InputBackground = Color.FromArgb(18, 18, 18);
    internal static readonly Color PlaceholderText = Color.FromArgb(148, 155, 164);

    // Zone aliases for MainForm
    internal static readonly Color ZoneContent = Background;
    internal static readonly Color ZoneSidebar = Surface;
    internal static readonly Color ZoneHeader = SurfaceAlt;
    internal static readonly Color ZoneStatus = SurfaceHover;

    // ---------- ข้อความ ----------
    internal static readonly Color TextPrimary = Color.FromArgb(242, 243, 245);
    internal static readonly Color TextSecondary = Color.FromArgb(219, 222, 225);
    internal static readonly Color TextMuted = Color.FromArgb(148, 155, 164);
    internal static readonly Color TextDisabled = Color.FromArgb(161, 161, 170);
    internal static readonly Color OnAccent = Color.White;

    // ---------- สีเน้นเดียว + สถานะ ----------
    internal static readonly Color Accent = Color.FromArgb(37, 99, 235);   // #3B82F6 ฟ้า — ปุ่มหลัก / เลือกเท่านั้น
    internal static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    internal static readonly Color AccentPressed = Color.FromArgb(29, 78, 216);
    internal static readonly Color Online = Color.FromArgb(74, 222, 128);    // เขียว
    internal static readonly Color Waiting = Color.FromArgb(251, 191, 36);   // เหลือง
    internal static readonly Color Danger = Color.FromArgb(248, 113, 113);   // แดง
    internal static readonly Color DangerBackground = Color.FromArgb(153, 27, 27);
    internal static readonly Color DangerHover = Color.FromArgb(185, 28, 28);
    internal static readonly Color DangerPressed = Mix(DangerBackground, Color.Black, 0.2);
    internal const double BadgeBackgroundMix = 0.95;
    internal static readonly Color Offline = Color.FromArgb(148, 155, 164);   // เทา
    internal static readonly Color Link = Color.FromArgb(96, 165, 250);
    internal static readonly Color ChatBubbleMine = Mix(Accent, Surface, 0.65);
    internal static readonly Color ChatBubbleTheirs = SurfaceAlt;

    // ---------- กริด 8px (ทุกระยะห่างเป็นพหุคูณของ 8) ----------
    internal const int Space1 = 8;   // 8px
    internal const int Space2 = 16;  // 16px
    internal const int Space3 = 24;  // 24px
    internal const int Space4 = 32;  // 32px
    internal const int Space5 = 40;  // 40px (5×8)
    internal const int Space6 = 48;  // 48px (6×8)

    // ---------- มุม (Phase U3: ไม่มีมุมมน รัศมีเป็น 0) ----------
    internal const int RadiusSmall = 0;
    internal const int RadiusMedium = 0;
    internal const int RadiusLarge = 0;

    // ---------- ขนาดคอมโพเนนต์ ----------
    internal const int ButtonHeight = 32;
    internal const int ButtonHeightLarge = 36;
    internal const int InputHeight = 32;
    internal const int IconSize = 16;
    internal const int IconSizeLarge = 20;
    internal const int SidebarWidth = 260;
    internal const int ToolbarHeight = 48;   // Phase U3: แถวเดียว 48px
    internal const int StatusHeight = 28;
    internal const int CardWidth = 340;
    internal const int CardHeight = 240;
    internal const int DialogMinWidth = 420;
    internal const int DialogMinHeight = 200;
    internal const int DialogFieldWidth = 420;
    internal const int DialogTextWidth = 520;
    internal const int DialogButtonWidth = 112;

    // ---------- ฟอนต์ (4 ระดับตาม spec) ----------
    // หัวเรื่อง 20pt semibold | ชื่อเครื่อง 13pt semibold | เนื้อหา 11pt | คำอธิบาย 9.5pt สีจาง
    internal static readonly Font FontHeading = new("Segoe UI", 20F, FontStyle.Bold);
    internal static readonly Font FontDeviceName = new("Segoe UI", 13F, FontStyle.Bold);
    internal static readonly Font FontBody = new("Segoe UI", 11F);
    internal static readonly Font FontCaption = new("Segoe UI", 9.5F);
    internal static readonly Font FontCaptionStrong = new("Segoe UI", 9.5F, FontStyle.Bold);

    // ฟอนต์เดิมที่ยังคงใช้ในส่วนอื่นของโปรแกรม (คงไว้เพื่อไม่ให้ build พัง)
    internal static readonly Font FontBodyStrong = new("Segoe UI", 11F, FontStyle.Bold);
    internal static readonly Font FontSmall = new("Segoe UI", 8.5F);
    internal static readonly Font FontMedium = new("Segoe UI", 10F);
    internal static readonly Font FontTitle = new("Segoe UI", 12F, FontStyle.Bold);
    internal static readonly Font FontChat = new("Segoe UI", 10F);
    internal static readonly Font FontChatItalic = new("Segoe UI", 10F, FontStyle.Italic);
    internal static readonly Font FontMono = new("Consolas", 10F);
    internal static readonly Font FontClock = new("Segoe UI", 32F, FontStyle.Bold);
    internal static readonly Font FontLockMessage = new("Segoe UI", 20F, FontStyle.Bold);
    internal static readonly Font FontSecureDesktop = new("Segoe UI", 28F);

    // ---------- ค่าเดิมที่คงไว้สำหรับส่วนอื่นของระบบ ----------
    internal const int ButtonWidthNarrow = 80;
    internal const int ButtonWidthWide = 150;
    internal const int ToastWidth = 360;
    internal const int ToastHeight = 88;
    internal const int ToastMargin = 16;
    internal const int ToastGap = 8;
    internal const int BubbleIndent = 120;
    internal const int ChatBubbleIndent = 50;
    internal const int ChatTextWidth = 420;
    internal const int ChatTextPadding = 90;
    internal const int MinChatTextWidth = 160;
    internal const int AttachmentMaxWidth = 260;
    internal const int AttachmentMaxHeight = 200;
    internal const int ActivityFilesHeight = 240;
    internal const int ActivityRunningWidth = 300;
    internal const int SectionHeaderHeight = 24;
    internal const int ActivityProgramColumnWidth = 230;
    internal const int ActivityTimeColumnWidth = 120;
    internal const int ActivityTitleColumnWidth = 300;
    internal const int FileTimeColumnWidth = 120;
    internal const int FileKindColumnWidth = 110;
    internal const int FilePathColumnWidth = 640;
    internal const int BlockDialogWidth = 600;
    internal const int BlockDialogHeight = 500;

    // ---------- แคชแปรงและปากกา ----------
    private static readonly ConcurrentDictionary<int, Lazy<SolidBrush>> Brushes = new();
    private static readonly ConcurrentDictionary<(int Argb, float Width), Lazy<Pen>> Pens = new();
    private static readonly ToolTip ToolTip = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 6000 };
    private static readonly MenuRenderer SharedMenuRenderer = new();

    internal static SolidBrush Brush(Color color) =>
        Brushes.GetOrAdd(color.ToArgb(), _ => new Lazy<SolidBrush>(() => new SolidBrush(color))).Value;
    internal static Pen Pen(Color color, float width = 1F) =>
        Pens.GetOrAdd((color.ToArgb(), width), _ => new Lazy<Pen>(() => new Pen(color, width))).Value;

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

    /// <summary>สร้างเส้นทางสี่เหลี่ยม (Phase U3: รัศมี 0 = สี่เหลี่ยมเหลี่ยม ไม่มน)</summary>
    internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        path.AddRectangle(bounds);
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
        box.BorderStyle = BorderStyle.None; // Phase U3: underline input — ขีดใต้อย่างเดียว
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
        menu.Renderer = SharedMenuRenderer;
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
