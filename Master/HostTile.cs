using System.Drawing.Drawing2D;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>สมาชิกร่วมของการ์ดเครื่องที่ใช้ทั้งโหมดจริงและโหมดตัวอย่าง</summary>
internal interface ITile : IDisposable
{
    HostEntry Host { get; }
    bool IsOnline { get; }
    bool Selected { get; set; }
    bool HasUnread { get; set; }
    bool Visible { get; set; }
    AgentClient? Client { get; }
    void Start();
    void Reconnect();
}

/// <summary>
/// การ์ดเครื่องลูกสไตล์ Swiss/Minimal (Phase U3)
/// ไม่มีกรอบ ไม่มีพื้นหลังต่างจากหน้า ใช้เส้นบนบาง 1px
/// แถวแรก: ชื่อเครื่อง (semibold) ชิดซ้าย + จุดสถานะและข้อความสถานะชิดขวา
/// แถวสอง: IP สีจาง ตามด้วยภาพหน้าจอ 16:9 ไม่มีขอบ
/// ถ้าไม่มีภาพ: พื้นผิวเรียบ #1A1A1A พร้อมข้อความ "ไม่มีภาพ" สีจางตรงกลาง ไม่มีไอคอนใหญ่
/// เลือกแล้ว: เส้นสีเน้นด้านบนหนา 2px แทนกรอบ
/// ปุ่มลัด (ควบคุม/แชท/ล็อก) โผล่เป็นข้อความตอน hover ที่มุมล่างขวา
/// </summary>
internal sealed class HostTile : UserControl, ITile
{
    private readonly PictureBox _pic;
    private readonly Label _name;
    private readonly Label _address;
    private readonly StatusBadge _badge;
    private readonly FlowLayoutPanel _shortcuts;
    private readonly Panel _topLine;           // เส้นบน 1px (ปกติ) / 2px สีเน้น (เลือก)
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly MasterPki _pki;
    private readonly CancellationTokenSource _cts = new();
    private AgentClient? _client;
    private bool _selected;
    private bool _hasUnread;

    public HostEntry Host { get; }
    public event EventHandler? OpenRemoteRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? ChatRequested;
    public event Action<HostTile, ChatMsg>? ChatReceived;
    public event Action<HostTile, VoiceMsg>? VoiceReceived;
    public event Action<HostTile, AlertMsg>? AlertReceived;

    private BlockRules? _blocklist;

    public void SetBlocklist(BlockRules rules)
    {
        _blocklist = rules;
        _client?.SendBlocklist(rules);
    }

    public AgentClient? Client => _client is { IsConnected: true } c ? c : null;
    public bool IsOnline { get; private set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateBorder(); }
    }

    public bool HasUnread
    {
        get => _hasUnread;
        set { _hasUnread = value; UpdateBorder(); }
    }

    public HostTile(HostEntry host, MasterSettings settings, MasterKey key, MasterPki pki)
    {
        Host = host;
        _settings = settings;
        _key = key;
        _pki = pki;

        Size = new Size(Theme.CardWidth, Theme.CardHeight);
        Margin = new Padding(Theme.Space2);
        Padding = Padding.Empty;
        BackColor = Theme.ZoneContent;   // เดียวกับพื้นหน้า ไม่มีพื้นหลังต่างกัน
        DoubleBuffered = true;

        // เส้นบน: ปกติ 1px สีจาง เลือกแล้ว 2px สีเน้น
        _topLine = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.Border,
        };

        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.ZoneContent,
            Padding = new Padding(Theme.Space2),
        };

        // ภาพหน้าจอ 16:9 ไม่มีขอบ (ถ้าไม่มีภาพ แสดงพื้นผิวเรียบ + ข้อความ "ไม่มีภาพ")
        _pic = new UiPictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Surface,
        };
        _pic.Paint += (_, e) =>
        {
            if (_pic.Image is null)
            {
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var msg = "ไม่มีภาพ";
                var size = TextRenderer.MeasureText(msg, Theme.FontBody);
                var x = (_pic.Width - size.Width) / 2;
                var y = (_pic.Height - size.Height) / 2;
                TextRenderer.DrawText(e.Graphics, msg, Theme.FontBody, new Point(x, y), Theme.TextMuted);
            }
        };

        _name = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space3,
            Text = host.Name,
            Font = Theme.FontDeviceName,    // 13pt semibold
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        // แถวสถานะ: จุดสี 8px + ข้อความ ชิดขวา
        var statusRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Space1,
            BackColor = Color.Transparent,
        };
        _badge = new StatusBadge(StatusKind.Waiting, "รอเชื่อมต่อ")
        {
            Dock = DockStyle.Right,
        };
        statusRow.Controls.Add(_badge);

        _address = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space2,
            Text = host.Address,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        // ปุ่มลัด: ข้อความโผล่ตอน hover ที่มุมล่างขวา
        _shortcuts = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.ButtonHeight,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            Visible = false,
        };
        var chat = new UiButton("แชท", ButtonKind.Ghost, UiIcon.Chat) { Width = Theme.ButtonHeightLarge };
        Theme.SetToolTip(chat, "แชท");
        chat.Click += (_, _) => ChatRequested?.Invoke(this, EventArgs.Empty);
        var remote = new UiButton("ควบคุม", ButtonKind.Ghost, UiIcon.Monitor) { Width = Theme.ButtonHeightLarge };
        Theme.SetToolTip(remote, "ควบคุมระยะไกล");
        remote.Click += (_, _) => OpenRemoteRequested?.Invoke(this, EventArgs.Empty);
        var lockBtn = new UiButton("ล็อก", ButtonKind.Ghost, UiIcon.Lock) { Width = Theme.ButtonHeightLarge };
        Theme.SetToolTip(lockBtn, "ล็อกหน้าจอ");
        lockBtn.Click += (_, _) => LockRequested?.Invoke(this, EventArgs.Empty);
        _shortcuts.Controls.Add(lockBtn);
        _shortcuts.Controls.Add(remote);
        _shortcuts.Controls.Add(chat);

        surface.Controls.Add(_pic);
        surface.Controls.Add(_shortcuts);
        surface.Controls.Add(_address);
        surface.Controls.Add(statusRow);
        surface.Controls.Add(_name);

        Controls.Add(_topLine);
        Controls.Add(surface);

        void Select(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Selected = !Selected; }
        _pic.MouseClick += Select;
        _name.MouseClick += Select;
        _pic.DoubleClick += (_, _) => OpenRemoteRequested?.Invoke(this, EventArgs.Empty);
        void Hover(Control c)
        {
            c.MouseEnter += (_, _) => _shortcuts.Visible = true;
            c.MouseLeave += (_, _) => { if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) _shortcuts.Visible = false; };
            foreach (Control child in c.Controls) Hover(child);
        }
        Hover(this);
        AccessibleName = $"เครื่อง {host.Name}";
    }

    private void UpdateBorder()
    {
        _topLine.Height = _selected ? 2 : 1;
        _topLine.BackColor = _selected ? Theme.Accent : Theme.Border;
        if (_hasUnread)
            _topLine.BackColor = Theme.Waiting;
    }

    public void Start() => _ = PollLoopAsync();
    public void Reconnect() => _client?.Dispose();

    private async Task PollLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_client is not { IsConnected: true })
                {
                    _client?.Dispose();
                    _client = null;
                    SetStatus("กำลังเชื่อมต่อ", StatusKind.Waiting);
                    var (host, port) = Host.Resolve(_settings.Port);
                    _client = await AgentClient.ConnectAsync(host, port, _key, _pki, Host.Name, token);
                    _client.ChatReceived += msg =>
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(() => ChatReceived?.Invoke(this, msg));
                    };
                    _client.VoiceReceived += msg =>
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(() => VoiceReceived?.Invoke(this, msg));
                    };
                    _client.AlertReceived += msg =>
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(() => AlertReceived?.Invoke(this, msg));
                    };
                    if (_blocklist is not null) _client.SendBlocklist(_blocklist);
                }

                var bmp = await _client.RequestScreenAsync(480, 55, token);
                var old = _pic.Image;
                _pic.Image = bmp;
                old?.Dispose();
                IsOnline = true;
                SetStatus("ออนไลน์", StatusKind.Online);

                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                IsOnline = false;
                SetStatus("ออฟไลน์", StatusKind.Error);
                Theme.SetToolTip(_badge, ex.Message);
                try { await Task.Delay(3000, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void SetStatus(string status, StatusKind kind)
    {
        if (IsDisposed) return;
        _badge.Set(kind, status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _client?.Dispose();
            _pic.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>การ์ดสำหรับโหมดตัวอย่าง UI: สไตล์เดียวกับ Hosttile แต่ไม่เชื่อมต่อเครือข่าย</summary>
internal sealed class PreviewHostTile : UserControl, ITile
{
    private readonly PictureBox _pic;
    private readonly Label _name;
    private readonly Label _address;
    private readonly StatusBadge _badge;
    private readonly Panel _topLine;
    private bool _selected;
    private bool _hasUnread;

    public HostEntry Host { get; }
    public bool IsOnline { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateBorder(); }
    }
    public bool HasUnread
    {
        get => _hasUnread;
        set { _hasUnread = value; UpdateBorder(); }
    }
    public new bool Visible { get => base.Visible; set => base.Visible = value; }
    public AgentClient? Client => null;

    public PreviewHostTile(HostEntry host, StatusKind kind)
    {
        Host = host;
        IsOnline = kind is StatusKind.Online;

        Size = new Size(Theme.CardWidth, Theme.CardHeight);
        Margin = new Padding(Theme.Space2);
        Padding = Padding.Empty;
        BackColor = Theme.ZoneContent;
        DoubleBuffered = true;

        _topLine = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.Border,
        };

        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.ZoneContent,
            Padding = new Padding(Theme.Space2),
        };

        _pic = new UiPictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Surface,
        };
        _pic.Image = UiIcons.Get(UiIcon.Computer, Theme.TextDisabled, Theme.IconSize); // ไอคอนเล็กตาม spec
        _pic.Paint += (_, e) =>
        {
            if (_pic.Image is null)
            {
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var msg = "ไม่มีภาพ";
                var size = TextRenderer.MeasureText(msg, Theme.FontBody);
                var x = (_pic.Width - size.Width) / 2;
                var y = (_pic.Height - size.Height) / 2;
                TextRenderer.DrawText(e.Graphics, msg, Theme.FontBody, new Point(x, y), Theme.TextMuted);
            }
        };

        _name = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space3,
            Text = host.Name,
            Font = Theme.FontDeviceName,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        var statusRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Space1,
            BackColor = Color.Transparent,
        };
        var statusText = kind switch
        {
            StatusKind.Online => "ออนไลน์",
            StatusKind.Waiting => "รอเชื่อมต่อ",
            StatusKind.Error => "ผิดพลาด",
            _ => "ออฟไลน์",
        };
        _badge = new StatusBadge(kind, statusText) { Dock = DockStyle.Right };
        statusRow.Controls.Add(_badge);

        _address = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space2,
            Text = host.Address,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        surface.Controls.Add(_pic);
        surface.Controls.Add(_address);
        surface.Controls.Add(statusRow);
        surface.Controls.Add(_name);

        Controls.Add(_topLine);
        Controls.Add(surface);

        void Select(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Selected = !Selected; }
        _pic.MouseClick += Select;
        _name.MouseClick += Select;
        AccessibleName = $"เครื่อง {host.Name}";
    }

    private void UpdateBorder()
    {
        _topLine.Height = _selected ? 2 : 1;
        _topLine.BackColor = _selected ? Theme.Accent : Theme.Border;
        if (_hasUnread)
            _topLine.BackColor = Theme.Waiting;
    }

    public void Start() { }
    public void Reconnect() { }

    protected override void Dispose(bool disposing)
    {
        // _pic.Image เป็นไอคอนจาก UiIcons cache — ไม่ต้อง Dispose
        base.Dispose(disposing);
    }
}
