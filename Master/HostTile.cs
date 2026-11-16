using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// การ์ดเครื่องลูก 1 เครื่อง: ชื่อ สถานะ IP และภาพย่อ
/// ตรรกะเชื่อมต่อ/ดึงภาพเหมือนเดิม; ปุ่มลัดแสดงเมื่อชี้เมาส์
/// </summary>
internal sealed class HostTile : UserControl
{
    private readonly PictureBox _pic;
    private readonly Label _name;
    private readonly Label _address;
    private readonly StatusBadge _badge;
    private readonly FlowLayoutPanel _shortcuts;
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

    private void UpdateBorder() => BackColor =
        _hasUnread ? Theme.Waiting :
        _selected ? Theme.AccentHover :
        Theme.Border;

    public HostTile(HostEntry host, MasterSettings settings, MasterKey key, MasterPki pki)
    {
        Host = host;
        _settings = settings;
        _key = key;
        _pki = pki;

        Size = new Size(Theme.CardWidth, Theme.CardHeight);
        Margin = new Padding(Theme.Space2);
        Padding = new Padding(Theme.Space1);
        BackColor = Theme.Border;
        DoubleBuffered = true;

        var surface = new UiCard { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space2) };
        _pic = new UiPictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Background };
        _name = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space5,
            Text = host.Name,
            Font = Theme.FontBodyStrong,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _address = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Space4,
            Text = host.Address,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _badge = new StatusBadge(StatusKind.Waiting, "รอเชื่อมต่อ") { Dock = DockStyle.Top, Margin = new Padding(0, Theme.Space1, 0, Theme.Space1) };
        _shortcuts = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.ButtonHeight + Theme.Space2,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Surface,
            Visible = false,
        };
        var chat = new UiButton("", ButtonKind.Ghost, UiIcon.Chat) { Width = Theme.ButtonHeight };
        Theme.SetToolTip(chat, "แชท");
        chat.Click += (_, _) => ChatRequested?.Invoke(this, EventArgs.Empty);
        var remote = new UiButton("", ButtonKind.Ghost, UiIcon.Monitor) { Width = Theme.ButtonHeight };
        Theme.SetToolTip(remote, "ควบคุมระยะไกล");
        remote.Click += (_, _) => OpenRemoteRequested?.Invoke(this, EventArgs.Empty);
        var lockBtn = new UiButton("", ButtonKind.Ghost, UiIcon.Lock) { Width = Theme.ButtonHeight };
        Theme.SetToolTip(lockBtn, "ล็อกหน้าจอ");
        lockBtn.Click += (_, _) => LockRequested?.Invoke(this, EventArgs.Empty);
        _shortcuts.Controls.Add(chat);
        _shortcuts.Controls.Add(remote);
        _shortcuts.Controls.Add(lockBtn);

        surface.Controls.Add(_pic);
        surface.Controls.Add(_shortcuts);
        surface.Controls.Add(_badge);
        surface.Controls.Add(_address);
        surface.Controls.Add(_name);
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
