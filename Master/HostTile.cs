using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// กรอบแสดงภาพหน้าจอย่อของเครื่องลูก 1 เครื่อง
/// วนขอภาพทุก 1 วินาที และเชื่อมต่อใหม่อัตโนมัติเมื่อหลุด
/// คลิก = เลือก/ยกเลิกเลือก, ดับเบิลคลิก = เปิดหน้าต่างควบคุม
/// </summary>
internal sealed class HostTile : UserControl
{
    private readonly PictureBox _pic;
    private readonly Label _label;
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly CancellationTokenSource _cts = new();
    private AgentClient? _client;
    private bool _selected;
    private bool _hasUnread;

    public HostEntry Host { get; }
    public event EventHandler? OpenRemoteRequested;

    /// <summary>มีข้อความแชทจากเครื่องนี้ (เรียกบน UI thread แล้ว)</summary>
    public event Action<HostTile, ChatMsg>? ChatReceived;

    /// <summary>มีข้อความเสียงจากเครื่องนี้ (เรียกบน UI thread แล้ว)</summary>
    public event Action<HostTile, VoiceMsg>? VoiceReceived;

    /// <summary>มีการแจ้งเตือนจากเครื่องนี้ (USB/ไฟล์/บล็อก) (เรียกบน UI thread แล้ว)</summary>
    public event Action<HostTile, AlertMsg>? AlertReceived;

    private BlockRules? _blocklist;

    /// <summary>ตั้งรายการบล็อกของเครื่องนี้ ส่งทันทีถ้าออนไลน์ และส่งซ้ำทุกครั้งที่เชื่อมต่อใหม่</summary>
    public void SetBlocklist(BlockRules rules)
    {
        _blocklist = rules;
        _client?.SendBlocklist(rules);
    }

    /// <summary>คืนค่าเฉพาะเมื่อออนไลน์อยู่</summary>
    public AgentClient? Client => _client is { IsConnected: true } c ? c : null;

    /// <summary>เครื่องนี้ออนไลน์อยู่หรือไม่ (ใช้โดยแถบช่อง/รายชื่อแบบ Discord)</summary>
    public bool IsOnline { get; private set; }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateBorder();
        }
    }

    /// <summary>มีข้อความแชทที่ยังไม่ได้อ่าน: กรอบจะเป็นสีส้ม</summary>
    public bool HasUnread
    {
        get => _hasUnread;
        set
        {
            _hasUnread = value;
            UpdateBorder();
        }
    }

    private void UpdateBorder() => BackColor =
        _hasUnread ? Color.FromArgb(240, 178, 50) :
        _selected ? DiscordTheme.Blurple :
        Color.FromArgb(43, 45, 49);

    public HostTile(HostEntry host, MasterSettings settings, MasterKey key)
    {
        Host = host;
        _settings = settings;
        _key = key;

        Size = new Size(340, 230);
        Margin = new Padding(6);
        Padding = new Padding(3);
        BackColor = Color.FromArgb(43, 45, 49);

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 25, 28) };
        _label = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(30, 31, 34),
            ForeColor = Color.FromArgb(148, 155, 164),
        };
        Controls.Add(_pic);
        Controls.Add(_label);

        // เลือกด้วยปุ่มซ้ายเท่านั้น (ปุ่มขวาใช้เปิดเมนูแชท)
        _pic.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Selected = !Selected; };
        _label.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Selected = !Selected; };
        _pic.DoubleClick += (_, _) => OpenRemoteRequested?.Invoke(this, EventArgs.Empty);

        SetStatus("รอเชื่อมต่อ", Color.Gold);
    }

    public void Start() => _ = PollLoopAsync();

    /// <summary>ตัดการเชื่อมต่อปัจจุบัน ลูปจะเชื่อมต่อใหม่เองด้วยค่าตั้งล่าสุด</summary>
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
                    SetStatus("กำลังเชื่อมต่อ...", Color.Gold);
                    var (host, port) = Host.Resolve(_settings.Port);
                    _client = await AgentClient.ConnectAsync(host, port, _key, token);
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
                    // ส่งรายการบล็อกที่ตั้งไว้ซ้ำทุกครั้งที่เชื่อมต่อใหม่
                    if (_blocklist is not null) _client.SendBlocklist(_blocklist);
                }

                var bmp = await _client.RequestScreenAsync(480, 55, token);
                var old = _pic.Image;
                _pic.Image = bmp;
                old?.Dispose();
                IsOnline = true;
                SetStatus("ออนไลน์", DiscordTheme.Online);

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
                SetStatus("ออฟไลน์ - " + ex.Message, Color.FromArgb(237, 66, 69));
                try { await Task.Delay(3000, token); } catch { break; }
            }
        }
    }

    private void SetStatus(string status, Color color)
    {
        if (IsDisposed) return;
        _label.ForeColor = color;
        _label.Text = $" ● {Host.Name} ({Host.Address})  {status}";
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
