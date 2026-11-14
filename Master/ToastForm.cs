using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>การ์ดแจ้งเตือนมุมขวาล่าง หายเองใน 6 วินาที คลิกเพื่อปิด ไม่แย่งโฟกัส</summary>
internal sealed class ToastForm : Form
{
    private readonly System.Windows.Forms.Timer _life = new() { Interval = 6000 };

    public ToastForm(string title, string text, Color accent)
    {
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(Theme.ToastWidth, Theme.ToastHeight);
        BackColor = Theme.Surface;
        Font = Theme.FontBody;
        Padding = new Padding(Theme.Space3);

        var icon = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = Theme.IconSizeLarge,
            Image = UiIcons.Get(UiIcon.Bell, accent, Theme.IconSizeLarge),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space5,
            Text = title,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontBodyStrong,
            AutoEllipsis = true,
        };
        var textLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = Theme.TextSecondary,
            Font = Theme.FontSmall,
            AutoEllipsis = true,
        };
        Controls.Add(textLabel);
        Controls.Add(titleLabel);
        Controls.Add(icon);
        foreach (Control c in new Control[] { this, icon, titleLabel, textLabel })
            c.Click += (_, _) => Close();
        AccessibleName = title;
        _life.Tick += (_, _) => { _life.Stop(); Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadiusMedium);
        Region = new Region(path);
        _life.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Dispose();
        Region = null;
        base.OnFormClosed(e);
    }
}
