using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// แจ้งเตือนเด้งมุมขวาล่างแบบ Discord หายเองใน 6 วินาที คลิกเพื่อปิด
/// </summary>
internal sealed class ToastForm : Form
{
    private readonly System.Windows.Forms.Timer _life = new() { Interval = 6000 };

    public ToastForm(string title, string text, Color accent)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(340, 76);
        BackColor = DiscordTheme.Sidebar;

        var bar = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = accent };
        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "🔔  " + title,
            ForeColor = Color.White,
            Font = new Font("Tahoma", 9.5f, FontStyle.Bold),
            Padding = new Padding(10, 6, 8, 0),
        };
        var textLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = DiscordTheme.TextNormal,
            Font = new Font("Tahoma", 9),
            Padding = new Padding(12, 0, 8, 6),
            AutoEllipsis = true,
        };

        Controls.Add(textLabel);
        Controls.Add(titleLabel);
        Controls.Add(bar);

        foreach (Control c in new Control[] { this, bar, titleLabel, textLabel })
            c.Click += (_, _) => Close();

        _life.Tick += (_, _) => { _life.Stop(); Close(); };
    }

    protected override bool ShowWithoutActivation => true; // ไม่แย่งโฟกัสหน้าต่างอื่น

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _life.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Dispose();
        base.OnFormClosed(e);
    }
}
