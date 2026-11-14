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
    private readonly MasterPki _pki;
    private readonly CancellationTokenSource _cts = new();
    private AgentClient? _client;

    private readonly Label _active = new();
    private readonly Label _sub = new();
    private readonly ListView _usage = new UiListView();
    private readonly ListBox _running = new UiListBox();
    private readonly ListView _files = new UiListView();

    // ใช้ฟอนต์ร่วมที่ cache ไว้ ไม่สร้าง/Dispose ต่อฟอร์ม

    public ActivityForm(HostEntry host, MasterSettings settings, MasterKey key, MasterPki pki)
    {
        _host = host;
        _settings = settings;
        _key = key;
        _pki = pki;

        Text = $"กิจกรรม - {host.Name} ({host.Address})";
        Icon = AppIcon.Load();
        Theme.ConfigureForm(this, new Size(720, 520));
        UiWindowPlacement.Attach(this, "activity-" + host.Name, new Size(960, 680), new Size(720, 520));

        BuildLayout();
    }

    private void BuildLayout()
    {
        // ----- แถบบน: โปรแกรมที่กำลังแอคทีฟ -----
        var top = new Panel { Dock = DockStyle.Top, Height = Theme.Space6 * 2 + Theme.Space2, BackColor = Theme.Surface, Padding = new Padding(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space2) };
        _active.Dock = DockStyle.Top;
        _active.Height = Theme.Space5 + Theme.Space1;
        _active.Text = "กำลังเชื่อมต่อ...";
        _active.ForeColor = Theme.TextPrimary;
        _active.Font = Theme.FontTitle;
        _sub.Dock = DockStyle.Top;
        _sub.Height = Theme.Space5;
        _sub.ForeColor = Theme.TextMuted;
        top.Controls.Add(_sub);
        top.Controls.Add(_active);

        // ----- ล่าง: ไฟล์ดาวน์โหลด/ส่งออก -----
        var filesPanel = new Panel { Dock = DockStyle.Bottom, Height = Theme.ActivityFilesHeight, BackColor = Theme.Background, Padding = new Padding(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space2) };
        filesPanel.Controls.Add(SectionHeader("ไฟล์ที่ดาวน์โหลด / ส่งออก (ล่าสุดอยู่บน)"));
        StyleListView(_files, ("เวลา", Theme.FileTimeColumnWidth), ("ประเภท", Theme.FileKindColumnWidth), ("ไฟล์", Theme.FilePathColumnWidth));
        _files.Dock = DockStyle.Fill;
        var filesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, Theme.SectionHeaderHeight, 0, 0) };
        filesHost.Controls.Add(_files);
        filesPanel.Controls.Add(filesHost);

        // ----- กลาง: เวลาใช้งาน (ซ้าย) + โปรแกรมที่เปิดอยู่ (ขวา) -----
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(Theme.Space2) };

        var right = new Panel { Dock = DockStyle.Right, Width = Theme.ActivityRunningWidth, BackColor = Theme.Background, Padding = new Padding(Theme.Space2, 0, 0, 0) };
        right.Controls.Add(SectionHeader("โปรแกรมที่เปิดอยู่"));
        _running.Dock = DockStyle.Fill;
        _running.BorderStyle = BorderStyle.None;
        Theme.StyleList(_running);
        var runHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, Theme.SectionHeaderHeight, 0, 0) };
        runHost.Controls.Add(_running);
        right.Controls.Add(runHost);

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 0, Theme.Space2, 0) };
        left.Controls.Add(SectionHeader("เวลาที่ใช้แต่ละโปรแกรม (เรียงมากไปน้อย)"));
        StyleListView(_usage, ("โปรแกรม", Theme.ActivityProgramColumnWidth), ("เวลาที่ใช้", Theme.ActivityTimeColumnWidth), ("หน้าต่างล่าสุด", Theme.ActivityTitleColumnWidth));
        _usage.Dock = DockStyle.Fill;
        var usageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, Theme.SectionHeaderHeight, 0, 0) };
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
        Height = Theme.SectionHeaderHeight,
        Text = text,
        ForeColor = Theme.TextMuted,
        Font = Theme.FontCaptionStrong,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static void StyleListView(ListView lv, params (string Name, int Width)[] cols)
    {
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = false;
        lv.BorderStyle = BorderStyle.None;
        Theme.StyleList(lv);
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
                    _client = await AgentClient.ConnectAsync(host, port, _key, _pki, _host.Name, token);
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
                try { await Task.Delay(3000, token); }
                catch (OperationCanceledException) { break; }
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
                item.ForeColor = Theme.Online;           // โปรแกรมที่แอคทีฟอยู่ตอนนี้
                item.Font = Theme.FontBodyStrong;
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
            if (f.Kind.Contains("USB")) item.ForeColor = Theme.Waiting;
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
        base.OnFormClosed(e);
    }
}
