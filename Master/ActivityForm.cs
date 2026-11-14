using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// หน้าต่าง "กิจกรรม" ของเครื่องลูกหนึ่งเครื่อง (ธีมแบบ Discord)
/// ดึงสรุปจาก Agent ทุก 2 วินาที แสดง: โปรแกรมที่กำลังใช้ (แอคทีฟ), เวลาที่ใช้แต่ละโปรแกรม,
/// โปรแกรมที่เปิดอยู่, และไฟล์ที่ดาวน์โหลด/ส่งออก
/// </summary>
internal sealed class ActivityForm : Form
{
    private readonly HostEntry _host;
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly CancellationTokenSource _cts = new();
    private AgentClient? _client;

    private readonly Label _active = new();
    private readonly Label _sub = new();
    private readonly ListView _usage = new();
    private readonly ListBox _running = new();
    private readonly ListView _files = new();

    // แคชฟอนต์ตัวหนาของแถวที่แอคทีฟ: เดิม Render สร้าง new Font ทุกแถวทุก 2 วินาที แล้วไม่ Dispose
    // เก็บตามฟอนต์ฐาน (รองรับ DPI ที่เปลี่ยน) แล้ว Dispose ทั้งหมดตอนปิดฟอร์ม
    private readonly Dictionary<Font, Font> _boldFonts = new();

    private Font BoldFor(Font baseFont)
    {
        if (!_boldFonts.TryGetValue(baseFont, out var bold))
        {
            bold = new Font(baseFont, FontStyle.Bold);
            _boldFonts[baseFont] = bold;
        }
        return bold;
    }

    public ActivityForm(HostEntry host, MasterSettings settings, MasterKey key)
    {
        _host = host;
        _settings = settings;
        _key = key;

        Text = $"กิจกรรม - {host.Name} ({host.Address})";
        Icon = AppIcon.Load();
        Size = new Size(960, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = DiscordTheme.FontUi;
        BackColor = DiscordTheme.Content;
        ForeColor = DiscordTheme.TextNormal;

        BuildLayout();
    }

    private void BuildLayout()
    {
        // ----- แถบบน: โปรแกรมที่กำลังแอคทีฟ -----
        var top = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = DiscordTheme.ServerRail, Padding = new Padding(16, 10, 16, 10) };
        _active.Dock = DockStyle.Top;
        _active.Height = 28;
        _active.Text = "กำลังเชื่อมต่อ...";
        _active.ForeColor = DiscordTheme.TextBright;
        _active.Font = DiscordTheme.FontTitleBold;
        _sub.Dock = DockStyle.Top;
        _sub.Height = 20;
        _sub.ForeColor = DiscordTheme.TextMuted;
        top.Controls.Add(_sub);
        top.Controls.Add(_active);

        // ----- ล่าง: ไฟล์ดาวน์โหลด/ส่งออก -----
        var filesPanel = new Panel { Dock = DockStyle.Bottom, Height = 220, BackColor = DiscordTheme.Content, Padding = new Padding(10, 6, 10, 10) };
        filesPanel.Controls.Add(SectionHeader("ไฟล์ที่ดาวน์โหลด / ส่งออก (ล่าสุดอยู่บน)"));
        StyleListView(_files, ("เวลา", 120), ("ประเภท", 110), ("ไฟล์", 640));
        _files.Dock = DockStyle.Fill;
        var filesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 24, 0, 0) };
        filesHost.Controls.Add(_files);
        filesPanel.Controls.Add(filesHost);

        // ----- กลาง: เวลาใช้งาน (ซ้าย) + โปรแกรมที่เปิดอยู่ (ขวา) -----
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = DiscordTheme.Content, Padding = new Padding(10) };

        var right = new Panel { Dock = DockStyle.Right, Width = 300, BackColor = DiscordTheme.Content, Padding = new Padding(8, 0, 0, 0) };
        right.Controls.Add(SectionHeader("โปรแกรมที่เปิดอยู่"));
        _running.Dock = DockStyle.Fill;
        _running.BorderStyle = BorderStyle.None;
        _running.BackColor = DiscordTheme.Sidebar;
        _running.ForeColor = DiscordTheme.TextNormal;
        _running.IntegralHeight = false;
        var runHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 24, 0, 0) };
        runHost.Controls.Add(_running);
        right.Controls.Add(runHost);

        var left = new Panel { Dock = DockStyle.Fill, BackColor = DiscordTheme.Content, Padding = new Padding(0, 0, 8, 0) };
        left.Controls.Add(SectionHeader("เวลาที่ใช้แต่ละโปรแกรม (เรียงมากไปน้อย)"));
        StyleListView(_usage, ("โปรแกรม", 230), ("เวลาที่ใช้", 120), ("หน้าต่างล่าสุด", 300));
        _usage.Dock = DockStyle.Fill;
        var usageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 24, 0, 0) };
        usageHost.Controls.Add(_usage);
        left.Controls.Add(usageHost);

        mid.Controls.Add(left);
        mid.Controls.Add(right);

        Controls.Add(mid);
        Controls.Add(filesPanel);
        Controls.Add(top);
    }

    private static Label SectionHeader(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 24,
        Text = text,
        ForeColor = DiscordTheme.TextMuted,
        Font = DiscordTheme.FontSmallBold,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static void StyleListView(ListView lv, params (string Name, int Width)[] cols)
    {
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = false;
        lv.BorderStyle = BorderStyle.None;
        lv.BackColor = DiscordTheme.Sidebar;
        lv.ForeColor = DiscordTheme.TextNormal;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        foreach (var (name, width) in cols) lv.Columns.Add(name, width);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = PollLoopAsync();
    }

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
                    _active.Text = "กำลังเชื่อมต่อ...";
                    var (host, port) = _host.Resolve(_settings.Port);
                    _client = await AgentClient.ConnectAsync(host, port, _key, token);
                }

                var snap = await _client.RequestActivityAsync(token);
                if (IsDisposed) return;
                Render(snap);
                await Task.Delay(2000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsDisposed) break;
                _active.Text = "ขาดการเชื่อมต่อ";
                _sub.Text = ex.Message;
                _client?.Dispose();
                _client = null;
                try { await Task.Delay(3000, token); } catch { break; }
            }
        }
    }

    private void Render(ActivitySnapshot s)
    {
        _active.Text = string.IsNullOrEmpty(s.ForegroundProcess)
            ? "กำลังใช้งาน: (ไม่พบหน้าต่างที่แอคทีฟ)"
            : $"กำลังใช้งาน: {s.ForegroundProcess}";
        var uptime = s.SampledAt - s.StartedAt;
        _sub.Text = (string.IsNullOrEmpty(s.ForegroundTitle) ? "" : s.ForegroundTitle + "    •    ") +
                    $"เก็บข้อมูลมาแล้ว {FormatSpan(uptime)}";

        // เวลาใช้งานต่อโปรแกรม
        _usage.BeginUpdate();
        _usage.Items.Clear();
        foreach (var u in s.Usage)
        {
            var item = new ListViewItem(u.Process);
            item.SubItems.Add(FormatSeconds(u.ActiveSeconds));
            item.SubItems.Add(u.Title);
            if (u.Process.Equals(s.ForegroundProcess, StringComparison.OrdinalIgnoreCase))
            {
                item.ForeColor = DiscordTheme.Online;           // โปรแกรมที่แอคทีฟอยู่ตอนนี้
                item.Font = BoldFor(_usage.Font);
            }
            _usage.Items.Add(item);
        }
        _usage.EndUpdate();

        // โปรแกรมที่เปิดอยู่
        _running.BeginUpdate();
        _running.Items.Clear();
        foreach (var app in s.RunningApps) _running.Items.Add(app);
        _running.EndUpdate();

        // ไฟล์ (ล่าสุดอยู่บน — snapshot ส่งมาเรียงใหม่ไปเก่าแล้ว)
        _files.BeginUpdate();
        _files.Items.Clear();
        foreach (var f in s.FileEvents)
        {
            var item = new ListViewItem(f.Time.ToString("HH:mm:ss"));
            item.SubItems.Add(f.Kind);
            item.SubItems.Add(f.Path);
            if (f.Kind.Contains("USB")) item.ForeColor = Color.FromArgb(240, 178, 50);
            _files.Items.Add(item);
        }
        _files.EndUpdate();
    }

    private static string FormatSeconds(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} ชม. {t.Minutes} นาที" :
               t.TotalMinutes >= 1 ? $"{t.Minutes} นาที {t.Seconds} วิ" :
               $"{t.Seconds} วิ";
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} ชม. {t.Minutes} นาที" : $"{t.Minutes} นาที";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _client?.Dispose();
        foreach (var font in _boldFonts.Values) font.Dispose();
        _boldFonts.Clear();
        base.OnFormClosed(e);
    }
}
