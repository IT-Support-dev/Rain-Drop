using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RainDrop.Shared;

internal enum ButtonKind { Primary, Secondary, Danger, Ghost, Outline }

/// <summary>ปุ่มสไตล์ Swiss/Minimal Light Theme (Phase U4).
/// .button → พื้นดำ ตัวอักษรขาว สี่เหลี่ยมคม padding ตามกริด hover #1F2937
/// .button--outline → ขอบ 1px ดำ พื้นโปร่ง hover พื้นดำตัวอักษรขาว
/// ปุ่มอันตรายใช้ outline สีดำ ห้ามใช้บล็อกสีแดง/ชมพู</summary>
internal sealed class UiButton : Button
{
    private ButtonKind _kind;
    private bool _hover;

    internal UiButton(string text, ButtonKind kind = ButtonKind.Secondary, UiIcon icon = UiIcon.None)
    {
        _kind = kind;
        IconKind = icon;
        Text = text;
        Font = Theme.FontBody;
        Height = Theme.ButtonHeight;
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        TabStop = true;
        Padding = new Padding(Theme.Space2, 0, Theme.Space2, 0);
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
    }

    private void ApplyColors()
    {
        Color back, fore, hover, down;
        switch (_kind)
        {
            case ButtonKind.Primary:
                back = Theme.Accent; fore = Theme.OnAccent; hover = Theme.AccentHover; down = Theme.AccentPressed;
                break;
            case ButtonKind.Danger:
                // อันตรายใช้ outline สีดำ ไม่ใช้บล็อกสีแดง/ชมพู ตาม spec Phase U4
                back = Color.Transparent; fore = Theme.TextPrimary; hover = Theme.SurfaceHover; down = Theme.SurfacePressed;
                FlatAppearance.BorderSize = 1;
                FlatAppearance.BorderColor = Theme.Border;
                break;
            case ButtonKind.Ghost:
                back = Color.Transparent; fore = Theme.TextSecondary; hover = Theme.SurfaceHover; down = Theme.SurfacePressed;
                break;
            case ButtonKind.Outline:
                back = Color.Transparent; fore = Theme.TextPrimary; hover = Theme.SurfaceHover; down = Theme.SurfacePressed;
                FlatAppearance.BorderSize = 1;
                FlatAppearance.BorderColor = Theme.Border;
                break;
            default:
                back = Theme.SurfaceAlt; fore = Theme.TextSecondary; hover = Theme.SurfaceHover; down = Theme.SurfacePressed;
                break;
        }
        BackColor = Enabled ? (_hover ? hover : back) : Theme.Surface;
        ForeColor = Enabled ? fore : Theme.TextDisabled;
        FlatAppearance.MouseOverBackColor = hover;
        FlatAppearance.MouseDownBackColor = down;
        if (Image is not null) Image = UiIcons.Get(IconKind, ForeColor, Theme.IconSize);
    }

    internal UiIcon IconKind { get; set; }
    internal ButtonKind Kind { get => _kind; set { _kind = value; ApplyColors(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // วาด focus ring: วงกรอบ 2px ดำ ห่างขอบ 3px
        if (Focused && Enabled)
        {
            using var pen = new Pen(Theme.TextPrimary, 2F);
            var rect = new Rectangle(3, 3, Width - 7, Height - 7);
            e.Graphics.DrawRectangle(pen, rect);
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

/// <summary>สถานะเครื่องสไตล์ Light Swiss: จุดสี 8px + ข้อความสีเข้ม ไม่มีพื้นหลังแถบสี</summary>
internal sealed class StatusBadge : Control
{
    private StatusKind _kind;
    private string _text = "";
    internal StatusBadge(StatusKind kind, string text)
    {
        _kind = kind; _text = text;
        Font = Theme.FontCaption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; // เดิมเป็นพื้นเทา default ทับพื้นการ์ด
        UpdateSize();
    }
    internal void Set(StatusKind kind, string text)
    {
        if (_kind == kind && _text == text) return;
        _kind = kind; _text = text; UpdateSize(); Invalidate();
    }
    private void UpdateSize()
    {
        Width = Theme.Space1 + Theme.Space1 + TextRenderer.MeasureText(_text, Font).Width;
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
        // วาดจุดสี 8px ด้านซ้าย
        e.Graphics.FillEllipse(Theme.Brush(color), 0, (Height - Theme.Space1) / 2, Theme.Space1, Theme.Space1);
        // ข้อความสถานะใช้สีเข้ม (dark text) ผ่าน contrast ≥ 4.5
        TextRenderer.DrawText(e.Graphics, _text, Font, new Rectangle(Theme.Space1, 0, Width - Theme.Space1, Height),
            Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

internal enum StatusKind { Online, Waiting, Error, Offline }

internal static class UiInput
{
    internal static TextBox Create(string text = "", bool multiline = false, bool secret = false, string? placeholder = null)
    {
        var box = new PlaceholderTextBox { Text = text, Multiline = multiline, UseSystemPasswordChar = secret, Watermark = placeholder };
        Theme.StyleInput(box);
        if (!multiline) box.Height = Theme.InputHeight;
        return box;
    }

    /// <summary>ช่องค้นหาแบบเส้นล่างอย่างเดียว (underline input)
    /// placeholder "ค้นหาเครื่อง" ไอคอนแว่น 16px ซ้าย ไม่มีหัวข้อแยก ไม่มีพื้นหลังขอบกล่อง</summary>
    internal static Panel SearchBox(string placeholder, Action<string>? onTextChanged = null)
    {
        var container = new Panel
        {
            Height = Theme.InputHeight,
            BackColor = Color.Transparent,
        };

        var icon = new PictureBox
        {
            Image = UiIcons.Get(UiIcon.Search, Theme.PlaceholderText, Theme.IconSize),
            Size = new Size(Theme.IconSize, Theme.IconSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(0, (Theme.InputHeight - Theme.IconSize) / 2),
            BackColor = Color.Transparent,
        };

        var box = new PlaceholderTextBox
        {
            Watermark = placeholder,
            BorderStyle = BorderStyle.None,
            // TextBox ใส่ Color.Transparent ไม่ได้ (ArgumentException ตอนรันไทม์) ใช้สีพื้น sidebar แทน
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontBody,
            Location = new Point(Theme.IconSize + Theme.Space1, 0),
            Size = new Size(200, Theme.InputHeight),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
        };
        box.GotFocus += (_, _) => box.ForeColor = Theme.TextPrimary;
        box.LostFocus += (_, _) => box.ForeColor = Theme.TextPrimary;
        if (onTextChanged is not null) box.TextChanged += (_, _) => onTextChanged(box.Text);

        var underline = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Theme.Border,
        };

        container.Controls.Add(box);
        container.Controls.Add(icon);
        container.Controls.Add(underline);
        return container;
    }

    private sealed class PlaceholderTextBox : TextBox
    {
        private string? _placeholder;
        private bool _showPlaceholder;

        public string? Watermark
        {
            get => _placeholder;
            set { _placeholder = value; UpdatePlaceholder(); Invalidate(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdatePlaceholder();
            // TextBox ไม่เรียก OnPaint (UserPaint ปิดอยู่) จึงต้องใช้ EM_SETCUEBANNER ของ Windows
            // แสดง placeholder แทน ไม่งั้นช่องค้นหาจะว่างเปล่าไม่มีคำใบ้
            if (_placeholder is { Length: > 0 } && !Multiline)
                SendMessageCue(Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, _placeholder);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageCue(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

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
                    new Rectangle(Theme.Space1, 0, Width - Theme.Space2, Height),
                    Theme.PlaceholderText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }
}

/// <summary>ตัวกรองสถานะเป็นข้อความ tab: ทั้งหมด / ออนไลน์ / ออฟไลน์
/// ตัวที่เลือกมีขีดเส้นใต้ 2px สีดำ ข้อความต้องไม่ถูกตัด</summary>
internal sealed class UiSegmentedFilter : FlowLayoutPanel
{
    private readonly List<FilterTabLabel> _labels = new();
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
            var lbl = new FilterTabLabel
            {
                Text = label,
                Font = Theme.FontBody,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                Margin = new Padding(0, 0, Theme.Space2, 0),
                Padding = new Padding(0, 0, 0, 4), // เว้นให้เส้นใต้ 2px ไม่ทับตัวอักษร
                Cursor = Cursors.Hand,
                Tag = i,
            };
            lbl.MouseEnter += (_, _) => { if (i != _selectedIndex) lbl.ForeColor = Theme.TextPrimary; };
            lbl.MouseLeave += (_, _) => { if (i != _selectedIndex) lbl.ForeColor = Theme.TextSecondary; };
            lbl.Click += (_, _) => { SelectedIndex = i; SelectionChanged?.Invoke(this, i); };
            _labels.Add(lbl);
            Controls.Add(lbl);
        }
        UpdateStates();
    }

    private void UpdateStates()
    {
        for (int i = 0; i < _labels.Count; i++)
        {
            var lbl = _labels[i];
            bool sel = i == _selectedIndex;
            lbl.ForeColor = sel ? Theme.TextPrimary : Theme.TextSecondary;
            lbl.Font = sel ? Theme.FontBodyStrong : Theme.FontBody;
            lbl.Underlined = sel;
            lbl.Invalidate();
        }
    }

    // แท็บที่เลือกมีขีดเส้นใต้ 2px สีดำตาม spec (Label เป็น UserPaint จึงวาดทับตัวเองได้ตรง ๆ)
    private sealed class FilterTabLabel : Label
    {
        internal bool Underlined { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Underlined)
                e.Graphics.DrawLine(Theme.Pen(Theme.TextPrimary, 2F), 0, Height - 1, Width, Height - 1);
        }
    }
}

/// <summary>รายการแถบซ้าย: ไอคอนชิดซ้าย ข้อความน้ำหนักปกติ โฮเวอร์เต็มความกว้าง</summary>
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
            Location = new Point(Theme.Space1, (Theme.ButtonHeightLarge - Theme.IconSizeLarge) / 2),
            BackColor = Color.Transparent,
        };

        _label = new Label
        {
            Text = text,
            Font = Theme.FontBody,
            ForeColor = Theme.TextSecondary,
            Location = new Point(Theme.IconSizeLarge + Theme.Space2, 0),
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

    // FlowLayoutPanel วัดขนาดที่ต้องการแบบเรียงลูกทุกตัวในแถวเดียวเมื่อไม่ได้ระบุความกว้าง
    // ScrollableControl เอาค่านี้ไปคำนวณ AutoScroll extent ทำให้โชว์ scrollbar แนวนอนค้าง
    // แม้เนื้อหาจริง (wrap แล้ว) จะกว้างไม่ถึงขอบ — จำกัดความกว้างตอนวัดให้เท่าที่มีจริงจึงคำนวณถูก
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (ClientSize.Width > 0 && (proposedSize.Width == 0 || proposedSize.Width > ClientSize.Width))
            proposedSize.Width = ClientSize.Width;
        return base.GetPreferredSize(proposedSize);
    }
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
