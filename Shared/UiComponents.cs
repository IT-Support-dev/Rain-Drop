using System.Drawing.Drawing2D;

namespace RainDrop.Shared;

internal enum ButtonKind { Primary, Secondary, Danger, Ghost }

internal sealed class UiButton : Button
{
    private ButtonKind _kind;
    private bool _hover;

    internal UiButton(string text, ButtonKind kind = ButtonKind.Secondary, UiIcon icon = UiIcon.None)
    {
        _kind = kind;
        IconKind = icon;
        Text = text;
        Font = Theme.FontBodyStrong;
        Height = Theme.ButtonHeight;
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        TabStop = true;
        Padding = new Padding(Theme.Space3, 0, Theme.Space3, 0);
        Margin = new Padding(Theme.Space1);
        FlatAppearance.BorderSize = 0;
        ApplyColors();
        if (icon != UiIcon.None)
        {
            Image = UiIcons.Get(icon, ForeColor, Theme.IconSize);
            TextImageRelation = TextImageRelation.ImageBeforeText;
            if (text.Length == 0)
            {
                Width = Theme.ButtonHeight;
                Padding = Padding.Empty;
                TextImageRelation = TextImageRelation.Overlay;
                ImageAlign = ContentAlignment.MiddleCenter;
            }
        }
        MouseEnter += (_, _) => { _hover = true; ApplyColors(); };
        MouseLeave += (_, _) => { _hover = false; ApplyColors(); };
        EnabledChanged += (_, _) => ApplyColors();
        Resize += (_, _) => ApplyRegion();
        ApplyRegion();
    }

    private void ApplyColors()
    {
        Color back, fore, hover;
        switch (_kind)
        {
            case ButtonKind.Primary:
                back = Theme.Accent; fore = Theme.OnAccent; hover = Theme.AccentHover;
                FlatAppearance.MouseDownBackColor = Theme.AccentPressed;
                break;
            case ButtonKind.Danger:
                back = Theme.DangerBackground; fore = Theme.OnAccent; hover = Theme.DangerHover;
                FlatAppearance.MouseDownBackColor = Theme.DangerPressed;
                break;
            case ButtonKind.Ghost:
                back = Color.Transparent; fore = Theme.TextSecondary; hover = Theme.SurfaceHover;
                FlatAppearance.MouseDownBackColor = Theme.SurfacePressed;
                break;
            default:
                back = Theme.SurfaceAlt; fore = Theme.TextSecondary; hover = Theme.SurfaceHover;
                FlatAppearance.MouseDownBackColor = Theme.SurfacePressed;
                break;
        }
        BackColor = Enabled ? (_hover ? hover : back) : Theme.Surface;
        ForeColor = Enabled ? fore : Theme.TextDisabled;
        FlatAppearance.MouseOverBackColor = hover;
        if (Image is not null) Image = UiIcons.Get(IconKind, ForeColor, Theme.IconSize);
    }

    internal UiIcon IconKind { get; set; }
    internal ButtonKind Kind { get => _kind; set { _kind = value; ApplyColors(); } }

    private void ApplyRegion()
    {
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadiusSmall);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && Enabled)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Theme.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), Theme.RadiusSmall);
            e.Graphics.DrawPath(Theme.Pen(Theme.TextPrimary, 1.5F), path);
        }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Region = null;
        base.Dispose(disposing);
    }
}

internal sealed class UiCard : Panel
{
    internal UiCard()
    {
        BackColor = Theme.Surface;
        Padding = new Padding(Theme.Space4);
        Margin = new Padding(Theme.Space2);
        DoubleBuffered = true;
        Resize += (_, _) => ApplyRegion();
        ApplyRegion();
    }
    private void ApplyRegion()
    {
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadiusMedium);
        Region = new Region(path);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusMedium);
        e.Graphics.DrawPath(Theme.Pen(Theme.Border), path);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Region = null;
        base.Dispose(disposing);
    }
}

internal enum StatusKind { Online, Waiting, Error, Offline }

internal sealed class StatusBadge : Control
{
    private StatusKind _kind;
    private string _text = "";
    internal StatusBadge(StatusKind kind, string text)
    {
        _kind = kind; _text = text;
        Height = Theme.Space5;
        Font = Theme.FontCaptionStrong;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateSize();
    }
    internal void Set(StatusKind kind, string text)
    {
        if (_kind == kind && _text == text) return;
        _kind = kind; _text = text; UpdateSize(); Invalidate();
    }
    private void UpdateSize()
    {
        Width = TextRenderer.MeasureText(_text, Font).Width + Theme.Space5;
        AccessibleName = _text;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Color color = _kind switch
        {
            StatusKind.Online => Theme.Online,
            StatusKind.Waiting => Theme.Waiting,
            StatusKind.Error => Theme.Danger,
            _ => Theme.Offline,
        };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusSmall);
        e.Graphics.FillPath(Theme.Brush(Theme.Mix(color, Theme.Surface, Theme.BadgeBackgroundMix)), path);
        e.Graphics.FillEllipse(Theme.Brush(color), Theme.Space2, Height / 2 - 3, 6, 6);
        TextRenderer.DrawText(e.Graphics, _text, Font, new Rectangle(Theme.Space4, 0, Width - Theme.Space4, Height), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

internal static class UiInput
{
    internal static TextBox Create(string text = "", bool multiline = false, bool secret = false)
    {
        var box = new TextBox { Text = text, Multiline = multiline, UseSystemPasswordChar = secret };
        Theme.StyleInput(box);
        if (!multiline) box.Height = Theme.InputHeight;
        return box;
    }
}

internal sealed class UiFlowPanel : FlowLayoutPanel
{
    internal UiFlowPanel() { DoubleBuffered = true; }
}

internal sealed class UiPictureBox : PictureBox
{
    internal UiPictureBox() { DoubleBuffered = true; }
}

internal sealed class UiListView : ListView
{
    internal UiListView() { DoubleBuffered = true; }
}

internal sealed class UiListBox : ListBox
{
    internal UiListBox() { DoubleBuffered = true; }
}
