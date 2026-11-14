using System.Diagnostics;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// หน้าต่างดู/ควบคุมเครื่องลูกเครื่องเดียวแบบภาพละเอียด
/// เปิดการเชื่อมต่อแยกจาก thumbnail เพื่อดึงภาพเร็วที่สุดเท่าที่เครือข่ายไหว
/// ค่าเริ่มต้นเป็น "ดูอย่างเดียว" ต้องกดปุ่มโหมดควบคุมก่อนถึงจะส่งเมาส์/คีย์บอร์ด
/// </summary>
internal sealed class RemoteForm : Form
{
    private readonly HostEntry _host;
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly PictureBox _pic;
    private readonly ToolStripButton _controlButton;
    private readonly ToolStripLabel _statusLabel;
    private readonly CancellationTokenSource _cts = new();
    private AgentClient? _client;
    private long _lastMoveTicks;

    public RemoteForm(HostEntry host, MasterSettings settings, MasterKey key)
    {
        _host = host;
        _settings = settings;
        _key = key;

        Text = $"ควบคุมระยะไกล - {host.Name} ({host.Address})";
        Icon = AppIcon.Load();
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Font = new Font("Tahoma", 9);

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };

        _controlButton = new ToolStripButton("โหมดควบคุม: ปิด (ดูอย่างเดียว)") { CheckOnClick = true };
        _controlButton.CheckedChanged += (_, _) =>
            _controlButton.Text = _controlButton.Checked ? "โหมดควบคุม: เปิด" : "โหมดควบคุม: ปิด (ดูอย่างเดียว)";

        _statusLabel = new ToolStripLabel("กำลังเชื่อมต่อ...") { Alignment = ToolStripItemAlignment.Right };

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add(_controlButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("ล็อกเครื่องนี้", null, (_, _) =>
            _client?.SendJson(MsgType.Lock, new LockCmd("เครื่องนี้ถูกล็อกโดยผู้ดูแลระบบ"))));
        toolbar.Items.Add(new ToolStripButton("ปลดล็อก", null, (_, _) => _client?.Send(MsgType.Unlock)));
        toolbar.Items.Add(_statusLabel);

        Controls.Add(_pic);
        Controls.Add(toolbar);

        _pic.MouseMove += (_, e) =>
        {
            // จำกัดอัตราส่งเมาส์ไม่เกิน ~60 ครั้ง/วินาที
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastMoveTicks, now).TotalMilliseconds < 16) return;
            _lastMoveTicks = now;
            SendMouse(e.Location, MouseAction.Move);
        };
        _pic.MouseDown += (_, e) => SendMouse(e.Location, MouseAction.Down, ButtonIndex(e.Button));
        _pic.MouseUp += (_, e) => SendMouse(e.Location, MouseAction.Up, ButtonIndex(e.Button));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = StreamLoopAsync();
    }

    private async Task StreamLoopAsync()
    {
        var token = _cts.Token;
        var sw = Stopwatch.StartNew();
        int frames = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_client is not { IsConnected: true })
                {
                    _client?.Dispose();
                    _client = null;
                    _statusLabel.Text = "กำลังเชื่อมต่อ...";
                    var (host, port) = _host.Resolve(_settings.Port);
                    _client = await AgentClient.ConnectAsync(host, port, _key, token);
                }

                var bmp = await _client.RequestScreenAsync(1920, 65, token);
                var old = _pic.Image;
                _pic.Image = bmp;
                old?.Dispose();

                frames++;
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    _statusLabel.Text = $"{frames * 1000.0 / sw.ElapsedMilliseconds:0.0} fps  |  {bmp.Width}x{bmp.Height}";
                    frames = 0;
                    sw.Restart();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsDisposed) break;
                _statusLabel.Text = "ขาดการเชื่อมต่อ - " + ex.Message;
                _client?.Dispose();
                _client = null;
                try { await Task.Delay(2000, token); } catch { break; }
            }
        }
    }

    // ---------- แปลงพิกัดบน PictureBox (โหมด Zoom) เป็นสัดส่วน 0..1 ของหน้าจอเครื่องลูก ----------
    private bool TryMap(Point p, out double x, out double y)
    {
        x = y = 0;
        if (_pic.Image is null) return false;

        var img = _pic.Image.Size;
        var box = _pic.ClientSize;
        double scale = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
        double w = img.Width * scale, h = img.Height * scale;
        double offsetX = (box.Width - w) / 2, offsetY = (box.Height - h) / 2;

        x = (p.X - offsetX) / w;
        y = (p.Y - offsetY) / h;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }

    private bool CanControl => _controlButton.Checked && _client is { IsConnected: true };

    private void SendMouse(Point p, MouseAction action, int button = 0, int delta = 0)
    {
        if (!CanControl || !TryMap(p, out double x, out double y)) return;
        _client!.SendJson(MsgType.MouseEvent, new MouseCmd(x, y, action, button, delta));
    }

    private static int ButtonIndex(MouseButtons b) => b switch
    {
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => 0,
    };

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SendMouse(_pic.PointToClient(Cursor.Position), MouseAction.Wheel, 0, e.Delta);
        base.OnMouseWheel(e);
    }

    // ดักทุกปุ่มรวมถึง Tab/ลูกศร/Alt ก่อนที่ WinForms จะเอาไปใช้เอง
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (CanControl)
        {
            _client!.SendJson(MsgType.KeyEvent, new KeyCmd((int)(keyData & Keys.KeyCode), true));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (CanControl)
        {
            _client!.SendJson(MsgType.KeyEvent, new KeyCmd((int)e.KeyCode, false));
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _client?.Dispose();
        _pic.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
