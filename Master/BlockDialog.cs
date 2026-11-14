using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>กล่องตั้งค่ารายการบล็อก ธีมเดียวกับแอป; ตรรกะแยกบรรทัด/ส่งค่าเหมือนเดิม</summary>
internal static class BlockDialog
{
    public static BlockRules? Edit(IWin32Window owner, int machineCount, BlockRules? current)
    {
        using var form = new Form();
        Theme.ConfigureForm(form, new Size(Theme.BlockDialogWidth, Theme.BlockDialogHeight));
        form.Text = $"บล็อกโปรแกรม / เว็บ — {machineCount} เครื่อง";
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.StartPosition = FormStartPosition.CenterParent;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.ClientSize = new Size(Theme.BlockDialogWidth, Theme.BlockDialogHeight);
        form.Padding = new Padding(Theme.Space4);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(Theme.SectionLabel("ชื่อโปรแกรมที่ห้ามเปิด (บรรทัดละชื่อ เช่น chrome, game.exe, LINE)"));
        var progBox = UiInput.Create(current is null ? "" : string.Join(Environment.NewLine, current.Programs), multiline: true);
        progBox.Dock = DockStyle.Fill;
        progBox.ScrollBars = ScrollBars.Vertical;
        progBox.Font = Theme.FontMono;
        progBox.AccessibleName = "ชื่อโปรแกรมที่ห้ามเปิด";
        progBox.TabIndex = 0;
        layout.Controls.Add(progBox);

        layout.Controls.Add(Theme.SectionLabel("เว็บ/ลิงก์ที่ห้ามเข้า (บรรทัดละรายการ เช่น facebook.com, youtube.com)"));
        var urlBox = UiInput.Create(current is null ? "" : string.Join(Environment.NewLine, current.Urls), multiline: true);
        urlBox.Dock = DockStyle.Fill;
        urlBox.ScrollBars = ScrollBars.Vertical;
        urlBox.Font = Theme.FontMono;
        urlBox.AccessibleName = "เว็บที่ห้ามเข้า";
        urlBox.TabIndex = 1;
        layout.Controls.Add(urlBox);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.FontCaption,
            MaximumSize = new Size(Theme.DialogTextWidth, 0),
            Text = "โปรแกรมที่ถูกบล็อกจะถูกปิดทันทีถ้าเปิดขึ้น การบล็อกเว็บทำผ่านไฟล์ hosts โดย Service (ต้องติดตั้งแบบมี Service)",
        });

        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new UiButton("ยกเลิก") { Width = Theme.DialogButtonWidth, TabIndex = 3 };
        var ok = new UiButton("บันทึกและส่ง", ButtonKind.Primary) { Width = Theme.ButtonWidthWide, TabIndex = 2 };
        var clear = new UiButton("ล้างทั้งหมด", ButtonKind.Ghost) { Width = Theme.DialogButtonWidth, TabIndex = 4 };
        cancel.Click += (_, _) => { form.DialogResult = DialogResult.Cancel; form.Close(); };
        ok.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); };
        clear.Click += (_, _) => { progBox.Clear(); urlBox.Clear(); };
        actions.Controls.Add(cancel);
        actions.Controls.Add(ok);
        actions.Controls.Add(clear);
        layout.Controls.Add(actions);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(owner) != DialogResult.OK) return null;

        static List<string> Lines(string s) => s
            .Replace("\r", "").Split('\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

        return new BlockRules(Lines(progBox.Text), Lines(urlBox.Text));
    }
}
