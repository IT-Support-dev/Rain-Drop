using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>หน้าต่างสถานะการเชื่อมต่อ แทน MessageBox ข้อความยาว</summary>
internal sealed class StatusForm : Form
{
    internal StatusForm(string machine, string ips, string port, string keyState, string masters, string? error)
    {
        Text = "Rain Drop Agent - สถานะการเชื่อมต่อ";
        Theme.ConfigureForm(this, new Size(480, 360));
        UiWindowPlacement.Attach(this, "agent-status", new Size(560, 420), new Size(480, 360));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(Theme.Space5);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        void Row(string label, string value)
        {
            layout.Controls.Add(new Label { Text = label, Font = Theme.FontCaptionStrong, ForeColor = Theme.TextMuted, AutoSize = true, Margin = new Padding(0, Theme.Space3, 0, Theme.Space1) });
            layout.Controls.Add(new Label { Text = value, Font = Theme.FontBody, ForeColor = Theme.TextPrimary, AutoSize = true, MaximumSize = new Size(500, 0) });
        }
        Row("ชื่อเครื่อง", machine);
        Row("IP ของเครื่องนี้", ips);
        Row("พอร์ต", port);
        Row("กุญแจของ Master", keyState);
        Row("Master ที่เชื่อมต่ออยู่", masters);
        if (error is not null) Row("ปัญหาที่พบ", error);
        var close = new UiButton("ปิด", ButtonKind.Primary) { Width = 100, Anchor = AnchorStyles.Right };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close);
        Controls.Add(layout);
        CancelButton = close;
    }
}
