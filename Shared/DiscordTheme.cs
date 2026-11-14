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

    /// <summary>ปุ่มแบบ Discord: พื้นสีน้ำเงิน blurple ไม่มีขอบ</summary>
    public static Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Blurple,
            ForeColor = TextBright,
            Font = new Font("Tahoma", 9, FontStyle.Bold),
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
            Font = new Font("Tahoma", 8.5f),
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
            TextRenderer.DrawText(e.Graphics, initials, new Font("Tahoma", 12, FontStyle.Bold),
                new Rectangle(0, 0, 48, 48), TextBright,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        if (tooltip is not null) new ToolTip().SetToolTip(p, tooltip);
        return p;
    }
}
