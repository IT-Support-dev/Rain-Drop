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
        // ลบ focus rectangle สีขาวที่ติดปุ่มตามคำแนะนำจากภาพจริง;
        // การชี้ชัดโฟกัสทำผ่านสีพื้น hover/pressed แทน
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
    internal static TextBox Create(string text = "", bool multiline = false, bool secret = false, string? placeholder = null)
    {
        var box = new PlaceholderTextBox { Text = text, Multiline = multiline, UseSystemPasswordChar = secret, Watermark = placeholder };
        Theme.StyleInput(box);
        if (!multiline) box.Height = Theme.InputHeight;
        return box;
    }

    /// <summary>ช่องค้นหาพร้อมไอคอนแว่นขยายฝั่งซ้ายและ placeholder สีจาง</summary>
    internal static Panel SearchBox(string placeholder, Action<string>? onTextChanged = null)
    {
        var container = new Panel
        {
            Height = Theme.InputHeight,
            BackColor = Theme.InputBackground,
        };
        using var iconPath = Theme.RoundedRect(new Rectangle(0, 0, Theme.InputHeight, Theme.InputHeight), Theme.RadiusSmall);
        container.Region = new Region(iconPath);

        var icon = new PictureBox
        {
            Image = UiIcons.Get(UiIcon.Monitor, Theme.PlaceholderText, Theme.IconSize),
            Size = new Size(Theme.IconSize, Theme.IconSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(Theme.Space2, (Theme.InputHeight - Theme.IconSize) / 2),
            BackColor = Color.Transparent,
        };

        var box = new PlaceholderTextBox
        {
            Watermark = placeholder,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontBody,
            Location = new Point(Theme.InputHeight + Theme.Space2, 0),
            Size = new Size(200, Theme.InputHeight),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
        };
        box.GotFocus += (_, _) => box.BackColor = Theme.Surface;
        box.LostFocus += (_, _) => box.BackColor = Theme.InputBackground;
        if (onTextChanged is not null) box.TextChanged += (_, _) => onTextChanged(box.Text);

        container.Controls.Add(box);
        container.Controls.Add(icon);
        return container;
    }

    private sealed class PlaceholderTextBox : TextBox
    {
        private string? _placeholder;
        private bool _showPlaceholder;

        /// <summary>ข้อความตัวอย่างที่แสดงเมื่อช่องว่างและไม่มีโฟกัส (แยกจาก TextBox.PlaceholderText ของ .NET เพื่อหลีกเลี่ยงความขัดแย้งเรื่อง nullability)</summary>
        public string? Watermark
        {
            get => _placeholder;
            set { _placeholder = value; UpdatePlaceholder(); Invalidate(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdatePlaceholder();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            UpdatePlaceholder();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _showPlaceholder = false;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            UpdatePlaceholder();
        }

        private void UpdatePlaceholder()
        {
            _showPlaceholder = string.IsNullOrEmpty(Text) && !Focused && _placeholder is { Length: > 0 };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_showPlaceholder)
            {
                TextRenderer.DrawText(e.Graphics, _placeholder!, Font,
                    new Rectangle(Theme.Space2, 0, Width - Theme.Space4, Height),
                    Theme.PlaceholderText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }
}

/// <summary>ปุ่มทรีสต์สำหรับกรองสถานะ: ทั้งหมด / ออนไลน์ / ออฟไลน์ สลับเลือกได้อันเดียว</summary>
internal sealed class UiSegmentedFilter : FlowLayoutPanel
{
    private readonly List<UiButton> _buttons = new();
    private int _selectedIndex;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set { _selectedIndex = value; UpdateStates(); }
    }

    public event EventHandler<int>? SelectionChanged;

    internal UiSegmentedFilter(string[] labels, int selectedIndex = 0)
    {
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = true;
        _selectedIndex = selectedIndex;
        foreach (var (label, i) in labels.Select((l, idx) => (l, idx)))
        {
            var b = new UiButton(label, ButtonKind.Secondary)
            {
                Height = Theme.ButtonHeight,
                Margin = new Padding(0, 0, Theme.Space1, 0),
                TabIndex = i,
            };
            b.Click += (_, _) => { SelectedIndex = i; SelectionChanged?.Invoke(this, i); };
            _buttons.Add(b);
            Controls.Add(b);
        }
        UpdateStates();
    }

    private void UpdateStates()
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            bool sel = i == _selectedIndex;
            b.Kind = sel ? ButtonKind.Primary : ButtonKind.Secondary;
            b.ForeColor = sel ? Theme.OnAccent : Theme.TextSecondary;
        }
    }
}

/// <summary>รายการเมนูแถบซ้ายแบบเดียวกันทุกแถว: ไอคอนชิดซ้ายเท่ากัน ข้อความน้ำหนักปกติ โฮเวอร์เต็มความกว้าง</summary>
internal sealed class UiSidebarItem : Panel
{
    private readonly Label _label;
    private bool _selected;

    public UiSidebarItem(UiIcon icon, string text)
    {
        Height = Theme.ButtonHeightLarge;
        Margin = new Padding(0, Theme.Space1, 0, Theme.Space1);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;

        var iconBox = new PictureBox
        {
            Image = UiIcons.Get(icon, Theme.TextSecondary, Theme.IconSizeLarge),
            Size = new Size(Theme.IconSizeLarge, Theme.IconSizeLarge),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(Theme.Space2, (Theme.ButtonHeightLarge - Theme.IconSizeLarge) / 2),
            BackColor = Color.Transparent,
        };

        _label = new Label
        {
            Text = text,
            Font = Theme.FontBody, // น้ำหนักปกติ ไม่ใช่ตัวหนา
            ForeColor = Theme.TextSecondary,
            Location = new Point(Theme.InputHeight + Theme.Space3, 0),
            AutoSize = false,
            Height = Theme.ButtonHeightLarge,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
        };

        Controls.Add(iconBox);
        Controls.Add(_label);

        MouseEnter += (_, _) => { if (!_selected) { BackColor = Theme.SurfaceHover; _label.ForeColor = Theme.TextPrimary; } };
        MouseLeave += (_, _) => { if (!_selected) { BackColor = Color.Transparent; _label.ForeColor = Theme.TextSecondary; } };
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            BackColor = _selected ? Theme.SurfacePressed : Color.Transparent;
            _label.ForeColor = _selected ? Theme.TextPrimary : Theme.TextSecondary;
        }
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
