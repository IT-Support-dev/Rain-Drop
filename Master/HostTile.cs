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
/// การ์ดเครื่องลูกสไตล์ Light Swiss/Minimal (Phase U4)
/// .card → เส้นบน 2px สีดำ ไม่มีกรอบ/พื้นหลัง/เงา ช่องว่างใต้เส้นก่อนเนื้อหา 24px
/// Hover เปลี่ยนสีเส้นเป็น #B38B6D ภายใน 200ms (timer interpolate สี ถ้า UIEffectsEnabled == false เปลี่ยนทันที)
/// เลือกแล้ว: เส้นหนา 4px สีดำ (ไม่ใช้สีฟ้า)
/// แถวแรก: ชื่อเครื่อง (semibold) ชิดซ้าย + จุดสถานะและข้อความสถานะชิดขวา
/// แถวสอง: IP สีจาง ตามด้วยภาพหน้าจอ 16:9 ไม่มีขอบ
/// ถ้าไม่มีภาพ: พื้นผิวเรียบ + ข้อความ "ไม่มีภาพ" สีจางตรงกลาง ไม่มีไอคอนใหญ่
/// ปุ่มลัด (ควบคุม/แชท/ล็อก) โผล่เป็นข้อความตอน hover ที่มุมล่างขวา
/// </summary>
internal sealed class HostTile : UserControl, ITile
{
    private readonly PictureBox _pic;
    private readonly Label _name;
    private readonly Label _address;
    private readonly StatusBadge _badge;
    private readonly FlowLayoutPanel _shortcuts;
    private readonly Panel _topLine;           // เส้นบน 2px สีดำ / 4px สีดำเมื่อเลือก
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly MasterPki _pki;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 16 };
    private AgentClient? _client;
    private bool _selected;
    private bool _hasUnread;
    private bool _hovering;
    private int _hoverStep; // 0..20 สำหรับ interpolate สีเส้น

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
        BackColor = Theme.ZoneContent;
        DoubleBuffered = true;

        // เส้นบน: 2px สีดำ (เลือกแล้ว 4px สีดำ)
        _topLine = new Panel
        {
            Dock = DockStyle.Top,
            Height = 2,
            BackColor = Theme.TextPrimary,
        };

        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.ZoneContent,
            Padding = new Padding(Theme.Space2, Theme.Space3, Theme.Space2, Theme.Space2), // ช่องว่างใต้เส้น 24px
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
            AutoSize = false,
            Height = Theme.Space3,
            Text = host.Name,
            Font = Theme.FontDeviceName,    // 13pt semibold
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        // แถวสถานะ: จุดสี 8px + ข้อความสีเข้ม ชิดขวา
        var statusRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Space3, // เดิม 8px ทำให้ข้อความสถานะโดนตัดครึ่งแนวตั้ง ต้องสูงพอสำหรับตัวอักษร 9.5pt
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
            AutoSize = false,
            Height = Theme.Space2,
            Text = host.Address,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        // ปุ่มลัด: ข้อความโผล่ตอน hover มุมล่างขวา
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

        _hoverTimer.Tick += (_, _) =>
        {
            if (_hovering && _hoverStep < 20) _hoverStep++;
            else if (!_hovering && _hoverStep > 0) _hoverStep--;
            else { _hoverTimer.Stop(); }
            UpdateBorder();
        };

        void Select(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Selected = !Selected; }
        _pic.MouseClick += Select;
        _name.MouseClick += Select;
        _pic.DoubleClick += (_, _) => OpenRemoteRequested?.Invoke(this, EventArgs.Empty);
        void Hover(Control c)
        {
            c.MouseEnter += (_, _) =>
            {
                _hovering = true;
                _shortcuts.Visible = true;
                if (SystemInformation.UIEffectsEnabled) _hoverTimer.Start();
                else UpdateBorder();
            };
            c.MouseLeave += (_, _) =>
            {
                _hovering = false;
                if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
                {
                    _shortcuts.Visible = false;
                    if (SystemInformation.UIEffectsEnabled) _hoverTimer.Start();
                    else UpdateBorder();
                }
            };
            foreach (Control child in c.Controls) Hover(child);
        }
        Hover(this);
        AccessibleName = $"เครื่อง {host.Name}";
    }

    private void UpdateBorder()
    {
        if (_selected)
        {
            _topLine.Height = 4;
            _topLine.BackColor = Theme.TextPrimary;
        }
        else if (_hovering || _hoverStep > 0)
        {
            _topLine.Height = 2;
            // interpolate สีจากดำ → #B38B6D
            var t = _hoverStep / 20.0;
            _topLine.BackColor = InterpolateColor(Theme.TextPrimary, Theme.AccentLine, t);
        }
        else
        {
            _topLine.Height = 2;
            _topLine.BackColor = Theme.TextPrimary;
        }
    }

    private static Color InterpolateColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
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
            _hoverTimer.Dispose();
            _cts.Cancel();
            _client?.Dispose();
            _pic.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>การ์ดสำหรับโหมดตัวอย่าง UI: สไตล์เดียวกับ HostTile แต่ไม่เชื่อมต่อเครือข่าย</summary>
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
            Height = 2,
            BackColor = Theme.TextPrimary,
        };

        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.ZoneContent,
            Padding = new Padding(Theme.Space2, Theme.Space3, Theme.Space2, Theme.Space2),
        };

        _pic = new UiPictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Surface,
        };
        // ไม่ตั้ง _pic.Image ให้แสดงข้อความ "ไม่มีภาพ" เหมือน HostTile จริง
        // (เดิมใส่ไอคอน 16px แล้ว PictureBox Zoom ยืดจนเบลอเต็มการ์ด)
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
            AutoSize = false,
            Height = Theme.Space3,
            Text = host.Name,
            Font = Theme.FontDeviceName,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        var statusRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Space3, // เดิม 8px ทำให้ข้อความสถานะโดนตัดครึ่งแนวตั้ง ต้องสูงพอสำหรับตัวอักษร 9.5pt
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
            AutoSize = false,
            Height = Theme.Space2,
            Text = host.Address,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
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
        if (_selected)
        {
            _topLine.Height = 4;
            _topLine.BackColor = Theme.TextPrimary;
        }
        else
        {
            _topLine.Height = 2;
            _topLine.BackColor = Theme.TextPrimary;
        }
    }

    public void Start() { }
    public void Reconnect() { }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
