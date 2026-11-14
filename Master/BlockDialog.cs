using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// กล่องตั้งค่ารายการบล็อก: ชื่อโปรแกรม (บรรทัดละชื่อ) และเว็บ/ลิงก์ (บรรทัดละรายการ)
/// </summary>
internal static class BlockDialog
{
    public static BlockRules? Edit(IWin32Window owner, int machineCount, BlockRules? current)
    {
        using var form = new Form
        {
            Text = $"บล็อกโปรแกรม / เว็บ — {machineCount} เครื่อง",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 440),
            Font = new Font("Tahoma", 9),
            BackColor = DiscordTheme.Content,
            ForeColor = DiscordTheme.TextNormal,
        };

        Label Head(string t, int y) => new()
        {
            Text = t, AutoSize = true, Location = new Point(14, y),
            ForeColor = DiscordTheme.TextMuted, Font = new Font("Tahoma", 8.5f, FontStyle.Bold),
        };

        TextBox Box(int y, string value) => new()
        {
            Location = new Point(14, y), Size = new Size(532, 130), Multiline = true,
            ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10),
            BackColor = DiscordTheme.Sidebar, ForeColor = DiscordTheme.TextNormal,
            BorderStyle = BorderStyle.FixedSingle, Text = value,
        };

        form.Controls.Add(Head("ชื่อโปรแกรมที่ห้ามเปิด (บรรทัดละชื่อ เช่น  chrome, game.exe, LINE):", 12));
        var progBox = Box(32, current is null ? "" : string.Join(Environment.NewLine, current.Programs));
        form.Controls.Add(progBox);

        form.Controls.Add(Head("เว็บ/ลิงก์ที่ห้ามเข้า (บรรทัดละรายการ เช่น  facebook.com, youtube.com):", 176));
        var urlBox = Box(196, current is null ? "" : string.Join(Environment.NewLine, current.Urls));
        form.Controls.Add(urlBox);

        var note = new Label
        {
            Location = new Point(14, 334), Size = new Size(532, 36),
            ForeColor = DiscordTheme.TextMuted, Font = new Font("Tahoma", 8),
            Text = "โปรแกรมที่ถูกบล็อกจะถูกปิดทันทีถ้าเปิดขึ้น • การบล็อกเว็บทำผ่านไฟล์ hosts โดย Service (ต้องติดตั้งแบบมี Service)",
        };
        form.Controls.Add(note);

        var ok = new Button { Text = "บันทึกและส่ง", DialogResult = DialogResult.OK, Size = new Size(120, 32), Location = new Point(312, 392), FlatStyle = FlatStyle.Flat, BackColor = DiscordTheme.Blurple, ForeColor = Color.White };
        var clear = new Button { Text = "ล้างทั้งหมด", Size = new Size(110, 32), Location = new Point(14, 392), FlatStyle = FlatStyle.Flat, ForeColor = DiscordTheme.TextNormal };
        var cancel = new Button { Text = "ยกเลิก", DialogResult = DialogResult.Cancel, Size = new Size(100, 32), Location = new Point(440, 392), FlatStyle = FlatStyle.Flat, ForeColor = DiscordTheme.TextNormal };
        clear.Click += (_, _) => { progBox.Clear(); urlBox.Clear(); };
        form.Controls.Add(ok);
        form.Controls.Add(clear);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(owner) != DialogResult.OK) return null;

        static List<string> Lines(string s) => s
            .Replace("\r", "").Split('\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

        return new BlockRules(Lines(progBox.Text), Lines(urlBox.Text));
    }
}
