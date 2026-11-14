namespace RainDrop.Shared;

/// <summary>
/// ชุดสี/ตัวช่วยจัดหน้าตาแบบ Discord (ธีมมืด) ใช้ตกแต่งหน้าต่างให้ดูเหมือนโปรแกรม Discord
/// เป็นแค่การตกแต่งหน้าตา — การทำงานจริงยังเป็นการดูจอ/ควบคุมเครื่องเหมือนเดิม
/// </summary>
public static class DiscordTheme
{
    public static readonly Color ServerRail = Color.FromArgb(30, 31, 34);   // #1E1F22 แถบเซิร์ฟเวอร์ซ้ายสุด
    public static readonly Color Sidebar = Color.FromArgb(43, 45, 49);       // #2B2D31 แถบช่อง/รายชื่อ
    public static readonly Color Content = Color.FromArgb(49, 51, 56);       // #313338 พื้นที่กลาง
    public static readonly Color Header = Color.FromArgb(49, 51, 56);
    public static readonly Color Blurple = Color.FromArgb(88, 101, 242);     // #5865F2 สีหลักของ Discord
    public static readonly Color BlurpleHover = Color.FromArgb(71, 82, 196);
    public static readonly Color TextNormal = Color.FromArgb(219, 222, 225); // #DBDEE1
    public static readonly Color TextMuted = Color.FromArgb(148, 155, 164);  // #949BA4
    public static readonly Color TextBright = Color.White;
    public static readonly Color Online = Color.FromArgb(35, 165, 90);       // #23A55A เขียวออนไลน์
    public static readonly Color Offline = Color.FromArgb(128, 132, 142);    // #80848E เทาออฟไลน์
    public static readonly Color Hover = Color.FromArgb(53, 55, 60);         // ไฮไลต์ตอนชี้เมาส์
    public static readonly Color Selected = Color.FromArgb(64, 68, 75);      // แถวที่ถูกเลือก

    // ---------- ฟอนต์ที่ใช้ร่วมกัน (แก้ GDI handle leak) ----------
    // เดิมทุกจุดเรียก new Font(...) แล้วไม่เคย Dispose โดยเฉพาะใน Paint/ลูปรีเฟรช
    // จึงสร้าง GDI handle ใหม่เรื่อย ๆ ตอนนี้ cache ไว้ชุดเดียวตลอดอายุโปรเซส
    // จงใจไม่ Dispose: เป็นชุดคงที่จำนวนน้อย ใช้ร่วมกันทุกฟอร์ม และ Control ไม่ได้เป็นเจ้าของ Font
    // (Control.Dispose ไม่ Dispose ฟอนต์ที่ assign ให้) จึงปลอดภัยที่จะแชร์
    public static readonly Font FontUi = new("Tahoma", 9);
    public static readonly Font FontUiBold = new("Tahoma", 9, FontStyle.Bold);
    public static readonly Font FontSmall = new("Tahoma", 8.5f);
    public static readonly Font FontSmallBold = new("Tahoma", 8.5f, FontStyle.Bold);
    public static readonly Font FontTiny = new("Tahoma", 8);
    public static readonly Font FontTinyBold = new("Tahoma", 8, FontStyle.Bold);
    public static readonly Font FontMedium = new("Tahoma", 9.5f);
    public static readonly Font FontMediumBold = new("Tahoma", 9.5f, FontStyle.Bold);
    public static readonly Font FontChat = new("Tahoma", 10);
    public static readonly Font FontSubTitleBold = new("Tahoma", 10.5f, FontStyle.Bold);
    public static readonly Font FontHeaderBold = new("Tahoma", 11, FontStyle.Bold);
    public static readonly Font FontServerInitials = new("Tahoma", 12, FontStyle.Bold);
    public static readonly Font FontTitleBold = new("Tahoma", 13, FontStyle.Bold);
    public static readonly Font FontMono = new("Consolas", 10);
    public static readonly Font FontLockScreen = new("Tahoma", 32, FontStyle.Bold);

    /// <summary>
    /// ToolTip ตัวเดียวใช้ร่วมกันทุกคอนโทรล (เดิม new ToolTip() ทุกครั้งที่สร้างไอคอน แล้วไม่ Dispose)
    /// ToolTip เป็น Component ที่สร้าง native window จึงต้องมีตัวเดียวตลอดอายุโปรเซส
    /// </summary>
    private static readonly ToolTip SharedToolTip = new();

    /// <summary>ปุ่มแบบ Discord: พื้นสีน้ำเงิน blurple ไม่มีขอบ</summary>
    public static Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Blurple,
            ForeColor = TextBright,
            Font = FontUiBold,
            Cursor = Cursors.Hand,
            AutoSize = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = BlurpleHover;
        return b;
    }

    /// <summary>ปุ่มไอคอน/คำสั่งแบบจาง ๆ บนแถบหัวเรื่อง</summary>
    public static Button HeaderButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Content,
            ForeColor = TextMuted,
            Font = FontSmall,
            Cursor = Cursors.Hand,
            AutoSize = true,
            Margin = new Padding(2, 4, 2, 4),
            Padding = new Padding(6, 3, 6, 3),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Hover;
        b.MouseEnter += (_, _) => b.ForeColor = TextNormal;
        b.MouseLeave += (_, _) => b.ForeColor = TextMuted;
        return b;
    }

    /// <summary>วงกลมไอคอนเซิร์ฟเวอร์ในแถบซ้ายสุด (วาดตัวอักษรย่อ)</summary>
    public static Panel ServerIcon(string initials, Color? fill = null, string? tooltip = null)
    {
        var p = new Panel { Size = new Size(48, 48), Margin = new Padding(12, 6, 12, 6), Cursor = Cursors.Hand };
        var color = fill ?? Color.FromArgb(53, 55, 60);
        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 0, 0, 47, 47);
            // ใช้ฟอนต์ที่ cache ไว้ (เดิม new Font ทุกครั้งที่ Paint = GDI handle รั่วทุกครั้ง redraw)
            TextRenderer.DrawText(e.Graphics, initials, FontServerInitials,
                new Rectangle(0, 0, 48, 48), TextBright,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        if (tooltip is not null) SharedToolTip.SetToolTip(p, tooltip);
        return p;
    }
}
