namespace RainDrop.Shared;

/// <summary>กล่องกรอกข้อมูลแบบง่าย รองรับหลายช่อง</summary>
public static class Prompt
{
    public static string[]? Show(IWin32Window? owner, string title, params (string Label, string Value)[] fields)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Font = new Font("Tahoma", 9),
        };

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            var box = new TextBox { Text = value, Width = 340 };
            boxes.Add(box);
            table.Controls.Add(box);
        }

        var ok = new Button { Text = "ตกลง", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "ยกเลิก", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(new Label { AutoSize = true });
        table.Controls.Add(buttons);

        form.Controls.Add(table);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK
            ? boxes.Select(b => b.Text.Trim()).ToArray()
            : null;
    }
}
