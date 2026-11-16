using System.Diagnostics;
using System.Text;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// หน้าจอหลัก: แสดงหน้าจอทุกเครื่องเป็นตาราง + แถบเครื่องมือสั่งงาน
/// คำสั่งจะส่งไปยังเครื่องที่เลือกไว้ ถ้าไม่ได้เลือกเลยจะส่งไปทุกเครื่อง
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly MasterPki _pki;
    private readonly FlowLayoutPanel _panel;                 // ตารางจอภาพ (เนื้อหาจริง)
    private readonly FlowLayoutPanel _channelList = new UiFlowPanel();   // รายการ "ช่อง" = เครื่องลูก (แถบซ้าย)
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _sidebarTimer = new() { Interval = 1500 };
    private readonly List<HostTile> _tiles = new();
    private readonly Dictionary<HostTile, ChatWindow> _chats = new();
    private readonly string _myName = $"ผู้ดูแลระบบ ({Environment.UserName})";
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Load(), Text = "Rain Drop Master", Visible = true };
    private readonly List<AlertMsg> _alertLog = new();
    private readonly List<ToastForm> _toasts = new();

    public MainForm()
    {
        Text = "Rain Drop Master";
        Icon = AppIcon.Load();
        Theme.ConfigureForm(this, new Size(900, 600));
        UiWindowPlacement.Attach(this, "master", new Size(1400, 860), new Size(900, 600));

        _settings = MasterSettings.Load();
        _key = MasterKey.LoadOrCreate();
        _pki = MasterPki.LoadOrCreate();

        _panel = new UiFlowPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
        var content = BuildContent();
        content.TabIndex = 1;
        Controls.Add(content);
        var sidebar = BuildSidebar();
        sidebar.TabIndex = 0;
        Controls.Add(sidebar);

        foreach (var host in _settings.Hosts)
            AddTile(host);
        ApplyFilter();

        _sidebarTimer.Tick += (_, _) => ApplyFilter();
        _sidebarTimer.Start();
        RefreshSidebars();
    }

    // ===================== โครงหน้าจอมืดแบบ token =====================

    private readonly TextBox _search = UiInput.Create();
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _count = new() { AutoSize = true, Font = Theme.FontCaption, ForeColor = Theme.TextMuted };
    private readonly Panel _empty = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private string _query = "";
    private MachineStatusFilter _statusFilter = MachineStatusFilter.All;

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.SidebarWidth, BackColor = Theme.Surface, Padding = new Padding(Theme.Space3) };
        var header = new Label { Dock = DockStyle.Top, Height = Theme.ToolbarHeight, Text = "Rain Drop", ForeColor = Theme.TextPrimary, Font = Theme.FontHeading, TextAlign = ContentAlignment.MiddleLeft };
        _search.Dock = DockStyle.Top;
        _search.TabIndex = 0;
        _search.AccessibleName = "ค้นหาเครื่อง";
        Theme.SetToolTip(_search, "ค้นหาชื่อเครื่องหรือ IP");
        _search.TextChanged += (_, _) => { _query = _search.Text; ApplyFilter(); };
        _filter.Dock = DockStyle.Top;
        _filter.TabIndex = 1;
        _filter.Font = Theme.FontBody;
        _filter.BackColor = Theme.InputBackground;
        _filter.ForeColor = Theme.TextPrimary;
        _filter.FlatStyle = FlatStyle.Flat;
        _filter.Items.AddRange(new object[] { "ทุกสถานะ", "ออนไลน์", "ออฟไลน์" });
        _filter.SelectedIndex = 0;
        _filter.AccessibleName = "กรองสถานะเครื่อง";
        _filter.SelectedIndexChanged += (_, _) => { _statusFilter = (MachineStatusFilter)_filter.SelectedIndex; ApplyFilter(); };
        _count.Dock = DockStyle.Top;
        _count.Padding = new Padding(0, Theme.Space2, 0, Theme.Space2);

        _channelList.Dock = DockStyle.Fill;
        _channelList.FlowDirection = FlowDirection.TopDown;
        _channelList.WrapContents = false;
        _channelList.AutoScroll = true;
        _channelList.BackColor = Theme.Surface;

        var settings = new UiButton("ตั้งค่า", ButtonKind.Ghost, UiIcon.Settings) { Dock = DockStyle.Bottom };
        settings.TabIndex = 4;
        settings.Click += (_, _) => ShowSettings();
        var community = new UiButton("ชุมชน", ButtonKind.Ghost, UiIcon.Chat) { Dock = DockStyle.Bottom };
        community.TabIndex = 3;
        community.Click += (_, _) => OpenCommunity();
        var add = new UiButton("เพิ่มเครื่อง", ButtonKind.Secondary, UiIcon.Plus) { Dock = DockStyle.Bottom };
        add.TabIndex = 2;
        add.Click += (_, _) => AddHost();

        sidebar.Controls.Add(_channelList);
        sidebar.Controls.Add(_count);
        sidebar.Controls.Add(_filter);
        sidebar.Controls.Add(_search);
        sidebar.Controls.Add(header);
        sidebar.Controls.Add(settings);
        sidebar.Controls.Add(community);
        sidebar.Controls.Add(add);
        return sidebar;
    }

    private Control BuildContent()
    {
        var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.ToolbarHeight, Padding = new Padding(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2), BackColor = Theme.Background, WrapContents = false, AutoScroll = true };
        void Tool(UiIcon icon, string tip, Action act, ButtonKind kind = ButtonKind.Ghost)
        {
            var b = new UiButton("", kind, icon) { Width = Theme.ButtonHeightLarge, Height = Theme.ButtonHeight };
            Theme.SetToolTip(b, tip);
            b.Click += (_, _) => act();
            toolbar.Controls.Add(b);
        }
        void Sep() => toolbar.Controls.Add(new Panel { Width = 1, Height = Theme.ButtonHeight, BackColor = Theme.Border, Margin = new Padding(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1) });
        Tool(UiIcon.Refresh, "ค้นหาเครื่องในเครือข่าย", () => _ = DiscoverAsync(userInitiated: true));
        Tool(UiIcon.Plus, "เพิ่มเครื่องเอง", AddHost);
        Sep();
        Tool(UiIcon.Monitor, "ควบคุมระยะไกล", OpenRemoteForSelected);
        Tool(UiIcon.Chat, "แชท", OpenChatForSelected);
        Tool(UiIcon.Message, "ส่งข้อความ", SendMessageToTargets);
        Sep();
        Tool(UiIcon.Lock, "ล็อกหน้าจอ", LockTargets);
        Tool(UiIcon.Unlock, "ปลดล็อก", () => Broadcast(c => c.Send(MsgType.Unlock)));
        Tool(UiIcon.Power, "ปิดเครื่อง", () => Power(PowerAction.Shutdown, "ปิดเครื่อง"), ButtonKind.Danger);
        Sep();
        Tool(UiIcon.Activity, "ดูกิจกรรม", OpenActivityForSelected);
        Tool(UiIcon.Block, "บล็อกโปรแกรม/เว็บ", OpenBlockSettings);
        Tool(UiIcon.More, "เพิ่มเติม", ShowMoreMenu);

        _panel.Padding = new Padding(Theme.Space3);
        _empty.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextMuted,
            Font = Theme.FontMedium,
            Text = "ยังไม่มีเครื่องที่แสดง\nกดค้นหาเพื่อหาเครื่องในแลน หรือเพิ่มเครื่องด้วย IP\nใช้ช่องค้นหา/ตัวกรองด้านซ้ายเพื่อหารายการ",
        });
        _status.Dock = DockStyle.Bottom;
        _status.Height = Theme.StatusHeight;
        _status.BackColor = Theme.Surface;
        _status.ForeColor = Theme.TextMuted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(Theme.Space3, 0, 0, 0);
        _status.Text = "เลือกเครื่องเพื่อสั่งงานรายเครื่อง ถ้าไม่เลือกจะส่งไปทุกเครื่อง";

        center.Controls.Add(_panel);
        center.Controls.Add(_empty);
        center.Controls.Add(_status);
        center.Controls.Add(toolbar);
        return center;
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add("เลือกทั้งหมด / ไม่เลือก", null, (_, _) => ToggleSelectAll());
        menu.Items.Add("ลบเครื่องที่เลือก", null, (_, _) => RemoveSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("เปิดเว็บ/โปรแกรม", null, (_, _) => RunOnTargets());
        menu.Items.Add("รีสตาร์ท", null, (_, _) => Power(PowerAction.Restart, "รีสตาร์ท"));
        menu.Items.Add("ล็อกออฟ", null, (_, _) => Power(PowerAction.Logoff, "ล็อกออฟ"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settings.AlertsEnabled ? "การแจ้งเตือน: เปิดอยู่" : "การแจ้งเตือน: ปิดอยู่", null, (_, _) => ToggleAlerts());
        menu.Items.Add("ดูการแจ้งเตือนล่าสุด", null, (_, _) => ShowAlertLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ส่งออก enrollment bundle...", null, (_, _) => ExportEnrollment());
        menu.Items.Add("เพิกถอนเครื่องที่เลือก", null, (_, _) => RevokeSelected());
        menu.Items.Add("ตรวจสอบ audit chain", null, (_, _) => VerifyAudit());
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(Cursor.Position);
    }

    private readonly Dictionary<HostTile, Label> _channelRows = new();
    private string _sidebarSignature = "";

    private void RefreshSidebars()
    {
        var sig = new StringBuilder();
        foreach (var t in _tiles)
            sig.Append(t.Host.Name).Append(':').Append(t.IsOnline).Append(':').Append(t.Selected).Append(':').Append(t.Visible).Append('|');
        string signature = sig.ToString();
        if (signature == _sidebarSignature) return;
        _sidebarSignature = signature;
        RefreshChannelRows();
        _count.Text = UiFilter.CountText(_tiles.Count(t => t.Visible), _tiles.Count);
    }

    private void ApplyFilter()
    {
        foreach (var tile in _tiles)
            tile.Visible = UiFilter.Matches(_query, _statusFilter, tile.IsOnline, tile.Host.Name, tile.Host.Address);
        _empty.Visible = !_tiles.Any(t => t.Visible);
        if (_empty.Visible) _empty.BringToFront();
        _sidebarSignature = "";
        RefreshSidebars();
    }

    private void RefreshChannelRows()
    {
        _channelList.SuspendLayout();
        try
        {
            bool membershipChanged = _channelRows.Count != _tiles.Count || _tiles.Any(t => !_channelRows.ContainsKey(t));
            if (membershipChanged)
            {
                foreach (var old in _channelRows.Values)
                {
                    _channelList.Controls.Remove(old);
                    old.Dispose();
                }
                _channelRows.Clear();
                foreach (var tile in _tiles)
                {
                    var row = CreateChannelRow(tile);
                    _channelRows[tile] = row;
                    _channelList.Controls.Add(row);
                }
            }
            foreach (var tile in _tiles)
            {
                var row = _channelRows[tile];
                row.Visible = tile.Visible;
                string text = tile.Host.Name;
                if (row.Text != text) row.Text = text;
                var fore = tile.Selected ? Theme.TextPrimary : tile.IsOnline ? Theme.TextSecondary : Theme.TextMuted;
                var back = tile.Selected ? Theme.SurfacePressed : Theme.Surface;
                if (row.ForeColor != fore) row.ForeColor = fore;
                if (row.BackColor != back) row.BackColor = back;
            }
        }
        finally { _channelList.ResumeLayout(); }
    }

    private Label CreateChannelRow(HostTile tile)
    {
        var row = new Label
        {
            Width = Theme.SidebarWidth - Theme.Space6,
            Height = Theme.ButtonHeight,
            Text = tile.Host.Name,
            Font = Theme.FontBody,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Theme.Space2, 0, 0, 0),
            Margin = new Padding(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            AutoEllipsis = true,
        };
        row.MouseEnter += (_, _) => { if (!tile.Selected) row.BackColor = Theme.SurfaceHover; };
        row.MouseLeave += (_, _) => { if (!tile.Selected) row.BackColor = Theme.Surface; };
        row.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                foreach (var t in _tiles) t.Selected = t == tile;
                _panel.ScrollControlIntoView(tile);
                RefreshSidebars();
            }
        };
        row.DoubleClick += (_, _) => OpenRemote(tile);
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add("ดูกิจกรรม", null, (_, _) => OpenActivity(tile));
        menu.Items.Add("ควบคุมระยะไกล", null, (_, _) => OpenRemote(tile));
        menu.Items.Add("แชท", null, (_, _) => OpenChat(tile));
        row.ContextMenuStrip = menu;
        row.Disposed += (_, _) => menu.Dispose();
        row.AccessibleName = tile.Host.Name;
        return row;
    }

    private void AddTile(HostEntry host)
    {
        var tile = new HostTile(host, _settings, _key, _pki);
        tile.OpenRemoteRequested += (_, _) => OpenRemote(tile);
        tile.LockRequested += (_, _) => { foreach (var t in _tiles) t.Selected = t == tile; LockTargets(); };
        tile.ChatRequested += (_, _) => OpenChat(tile);
        tile.ChatReceived += OnChatReceived;
        tile.VoiceReceived += OnVoiceReceived;
        tile.AlertReceived += OnAlertReceived;
        if (_settings.Blocklists.TryGetValue(host.Name, out var rules))
            tile.SetBlocklist(rules);
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add("แชท", null, (_, _) => OpenChat(tile));
        menu.Items.Add("ควบคุมระยะไกล", null, (_, _) => OpenRemote(tile));
        menu.Items.Add("ดูกิจกรรม", null, (_, _) => OpenActivity(tile));
        void AssignMenu(Control c)
        {
            c.ContextMenuStrip = menu;
            foreach (Control child in c.Controls) AssignMenu(child);
        }
        AssignMenu(tile);
        tile.Disposed += (_, _) => menu.Dispose();
        _tiles.Add(tile);
        _panel.Controls.Add(tile);
        tile.Start();
        ApplyFilter();
    }

    private void AddHost()
    {
        var values = Prompt.Show(this, "เพิ่มเครื่อง",
            ("ชื่อเครื่อง:", $"PC-{_tiles.Count + 1:00}"),
            ("IP / ชื่อโฮสต์:", "192.168.1."));
        if (values is null || string.IsNullOrWhiteSpace(values[1])) return;
        var host = new HostEntry { Name = values[0], Address = values[1] };
        _settings.Hosts.Add(host);
        _settings.IgnoredMachines.RemoveAll(n => n.Equals(host.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        AddTile(host);
    }

    private void RemoveSelected()
    {
        var selected = _tiles.Where(t => t.Selected).ToList();
        if (selected.Count == 0) { _status.Text = "กรุณาเลือกเครื่องที่จะลบก่อน"; return; }
        if (!UiDialog.Confirm(this, "ลบเครื่อง", $"ลบ {selected.Count} เครื่องออกจากรายการ?", "ลบ", danger: true)) return;
        foreach (var tile in selected)
        {
            _settings.Hosts.Remove(tile.Host);
            if (!_settings.IgnoredMachines.Contains(tile.Host.Name, StringComparer.OrdinalIgnoreCase))
                _settings.IgnoredMachines.Add(tile.Host.Name);
            _tiles.Remove(tile);
            _panel.Controls.Remove(tile);
            if (_chats.Remove(tile, out var chat)) chat.ForceClose();
            tile.Dispose();
        }
        _settings.Save();
        ApplyFilter();
    }

    private void ToggleSelectAll()
    {
        bool select = _tiles.Any(t => !t.Selected);
        foreach (var tile in _tiles) tile.Selected = select;
    }

    // ---------- ค้นหาเครื่องลูกอัตโนมัติ ----------

    private readonly CancellationTokenSource _discoveryCts = new();
    private bool _scanning;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_settings.AutoDiscover) _ = DiscoveryLoopAsync();
    }

    private async Task DiscoveryLoopAsync()
    {
        while (!_discoveryCts.IsCancellationRequested)
        {
            await DiscoverAsync(userInitiated: false);
            try { await Task.Delay(TimeSpan.FromSeconds(30), _discoveryCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DiscoverAsync(bool userInitiated)
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            if (userInitiated || _tiles.Count == 0) _status.Text = "กำลังค้นหาเครื่องลูกในเครือข่าย...";

            var found = await Discovery.ScanAsync(_settings.Port, _key.Fingerprint, TimeSpan.FromSeconds(2));
            if (IsDisposed) return;

            int added = 0, moved = 0;
            foreach (var item in found)
            {
                string ip = item.Address.ToString();
                string address = item.Reply.Port == _settings.Port ? ip : $"{ip}:{item.Reply.Port}";
                string machine = item.Reply.MachineName;

                // มีอยู่แล้วด้วย IP นี้
                if (_settings.Hosts.Any(h => h.Resolve(_settings.Port).Host == ip)) continue;

                // เครื่องเดิมแต่ IP เปลี่ยน (DHCP): อัปเดต IP ให้
                var existing = _settings.Hosts.FirstOrDefault(h => h.Name.Equals(machine, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Address = address;
                    _tiles.FirstOrDefault(t => t.Host == existing)?.Reconnect();
                    moved++;
                    continue;
                }

                if (_settings.IgnoredMachines.Contains(machine, StringComparer.OrdinalIgnoreCase)) continue;

                var host = new HostEntry { Name = machine, Address = address };
                _settings.Hosts.Add(host);
                AddTile(host);
                added++;
            }

            if (added > 0 || moved > 0) _settings.Save();

            if (userInitiated || added > 0 || _tiles.Count == 0)
            {
                _status.Text = found.Count == 0
                    ? "ไม่พบเครื่องลูกในวงแลนนี้ (เครื่องที่อยู่คนละวงเครือข่าย ให้กด \"เพิ่มเครื่องเอง\")"
                    : $"พบเครื่องลูก {found.Count} เครื่อง" + (added > 0 ? $" เพิ่มใหม่ {added} เครื่อง" : "");
            }
        }
        catch (Exception ex)
        {
            if (userInitiated) _status.Text = "ค้นหาไม่สำเร็จ: " + ex.Message;
        }
        finally
        {
            _scanning = false;
        }
    }

    // ---------- ควบคุมระยะไกล ----------

    private void OpenRemoteForSelected()
    {
        var tile = _tiles.FirstOrDefault(t => t.Selected);
        if (tile is null)
        {
            _status.Text = "กรุณาเลือกเครื่องที่จะควบคุม (หรือดับเบิลคลิกที่ภาพ)";
            return;
        }
        OpenRemote(tile);
    }

    private void OpenRemote(HostTile tile) => new RemoteForm(tile.Host, _settings, _key, _pki).Show();

    private void OpenActivity(HostTile tile) => new ActivityForm(tile.Host, _settings, _key, _pki).Show();

    private void OpenActivityForSelected()
    {
        var tile = _tiles.FirstOrDefault(t => t.Selected);
        if (tile is null) { _status.Text = "เลือกเครื่องที่จะดูกิจกรรมก่อน"; return; }
        OpenActivity(tile);
    }

    // ---------- แชท ----------

    private ChatWindow GetChat(HostTile tile)
    {
        if (_chats.TryGetValue(tile, out var existing) && !existing.IsDisposed)
            return existing;

        var chat = new ChatWindow($"แชท - {tile.Host.Name} ({tile.Host.Address})", _myName, text =>
        {
            if (tile.Client is not { } client) return false;
            client.SendJson(MsgType.ChatMessage, new ChatMsg(_myName, text, DateTime.Now));
            return true;
        },
        voice =>
        {
            if (tile.Client is not { } client) return false;
            client.SendJson(MsgType.VoiceMessage, voice);
            return true;
        });
        chat.Activated += (_, _) => tile.HasUnread = false;
        _chats[tile] = chat;
        return chat;
    }

    private void OpenChat(HostTile tile)
    {
        tile.HasUnread = false;
        GetChat(tile).ShowAndNotify(activate: true);
    }

    private void OpenChatForSelected()
    {
        var selected = _tiles.Where(t => t.Selected).ToList();
        if (selected.Count == 0)
        {
            _status.Text = "กรุณาเลือกเครื่องที่จะแชท (หรือคลิกขวาที่ภาพ)";
            return;
        }
        foreach (var tile in selected) OpenChat(tile);
    }

    /// <summary>มีข้อความจากเครื่องลูก: เปิดหน้าต่างแชทของเครื่องนั้น, กะพริบที่ Taskbar และทำกรอบเป็นสีส้ม</summary>
    private void OnChatReceived(HostTile tile, ChatMsg msg)
    {
        var chat = GetChat(tile);
        chat.Append(msg, mine: false);
        if (!chat.ContainsFocus) tile.HasUnread = true;
        chat.ShowAndNotify(activate: false);
        _status.Text = $"ข้อความใหม่จาก {tile.Host.Name}: {msg.Text}";
    }

    private void OnVoiceReceived(HostTile tile, VoiceMsg msg)
    {
        var chat = GetChat(tile);
        chat.AppendVoice(msg, mine: false);
        if (!chat.ContainsFocus) tile.HasUnread = true;
        chat.ShowAndNotify(activate: false);
        _status.Text = $"ข้อความเสียงใหม่จาก {tile.Host.Name} ({msg.Seconds:0} วินาที)";
    }

    // ---------- คำสั่งกลุ่ม ----------

    private List<HostTile> Targets()
    {
        var selected = _tiles.Where(t => t.Selected).ToList();
        return selected.Count > 0 ? selected : _tiles;
    }

    private void Broadcast(Action<AgentClient> action)
    {
        var targets = Targets();
        int sent = 0;
        foreach (var tile in targets)
        {
            if (tile.Client is { } client)
            {
                action(client);
                sent++;
            }
        }
        _status.Text = $"ส่งคำสั่งแล้ว {sent} เครื่อง" +
                       (sent < targets.Count ? $" (ออฟไลน์ {targets.Count - sent} เครื่อง)" : "");
    }

    private void LockTargets()
    {
        var values = Prompt.Show(this, "ล็อกหน้าจอ", ("ข้อความบนหน้าจอ:", "เครื่องนี้ถูกล็อกโดยผู้ดูแลระบบ"));
        if (values is null) return;
        int count = Targets().Count;
        if (count > 1 && !Confirm("ล็อกหน้าจอ", count)) return;
        Broadcast(c => c.SendJson(MsgType.Lock, new LockCmd(values[0])));
    }

    private void SendMessageToTargets()
    {
        var values = Prompt.Show(this, "ส่งข้อความ",
            ("หัวข้อ:", "ข้อความจากผู้ดูแลระบบ"),
            ("ข้อความ:", ""));
        if (values is null || values[1].Length == 0) return;
        Broadcast(c => c.SendJson(MsgType.ShowMessage, new MessageCmd(values[0], values[1])));
    }

    private void RunOnTargets()
    {
        var values = Prompt.Show(this, "เปิดเว็บ/โปรแกรม",
            ("URL หรือพาธโปรแกรม:", "https://"),
            ("อาร์กิวเมนต์ (ถ้ามี):", ""));
        if (values is null || values[0].Length == 0) return;
        Broadcast(c => c.SendJson(MsgType.RunCommand,
            new RunCmd(values[0], values[1].Length > 0 ? values[1] : null)));
    }

    private void Power(PowerAction action, string label)
    {
        int count = Targets().Count;
        if (!Confirm(label, count)) return;
        Broadcast(c => c.SendJson(MsgType.PowerAction, new PowerCmd(action)));
    }

    // ---------- ตั้งค่า ----------

    // ---------- การแจ้งเตือนจากเครื่องลูก ----------

    private void OnAlertReceived(HostTile tile, AlertMsg msg)
    {
        _alertLog.Insert(0, msg);
        if (_alertLog.Count > 200) _alertLog.RemoveAt(_alertLog.Count - 1);

        if (!_settings.AlertsEnabled) return;

        string title = $"{tile.Host.Name} — {msg.Kind}";
        _status.Text = $"{title}: {msg.Text}  ({msg.Time:HH:mm:ss})";
        ShowToast(title, msg.Text, msg.Kind);
        try { _tray.ShowBalloonTip(5000, title, msg.Text, ToolTipIcon.Warning); }
        catch (Exception ex)
        {
            // บอลลูนเป็นช่องทางเสริม การ์ด toast และแถบสถานะยังแจ้งผู้ใช้อยู่ จึงไม่โยนต่อ
            Diag.Warn("MainForm.OnAlertReceived", "แสดงบอลลูนแจ้งเตือนที่ถาดระบบไม่สำเร็จ", ex);
        }
    }

    private void ShowToast(string title, string text, string kind)
    {
        var accent = kind.Contains("USB") || kind.Contains("ส่งออก") ? Theme.Waiting
                   : kind.Contains("บล็อก") ? Theme.Danger
                   : Theme.AccentHover;
        var toast = new ToastForm(title, text, accent);
        toast.FormClosed += (_, _) => { _toasts.Remove(toast); RepositionToasts(); };
        _toasts.Insert(0, toast);
        RepositionToasts();
        toast.Show();
    }

    private void RepositionToasts()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int y = wa.Bottom - Theme.ToastMargin;
        foreach (var t in _toasts)
        {
            y -= t.Height + Theme.ToastGap;
            t.Location = new Point(wa.Right - t.Width - Theme.ToastMargin, y);
        }
    }

    private void ToggleAlerts()
    {
        _settings.AlertsEnabled = !_settings.AlertsEnabled;
        _settings.Save();
        _status.Text = _settings.AlertsEnabled ? "เปิดการแจ้งเตือนแล้ว" : "ปิดการแจ้งเตือนแล้ว";
    }

    private void ShowAlertLog()
    {
        string text = _alertLog.Count == 0
            ? "ยังไม่มีการแจ้งเตือน"
            : string.Join("\n", _alertLog.Take(50).Select(a => $"[{a.Time:HH:mm:ss}] {a.Kind}: {a.Text}"));
        UiDialog.Show(this, "การแจ้งเตือนล่าสุด", text);
    }

    // ---------- บล็อกโปรแกรม / เว็บ ----------

    private void OpenBlockSettings()
    {
        var targets = Targets();
        if (targets.Count == 0) { _status.Text = "ไม่มีเครื่องให้ตั้งค่า"; return; }

        // ใช้รายการของเครื่องแรกที่เลือกเป็นค่าตั้งต้นให้แก้ไข
        var first = targets[0];
        _settings.Blocklists.TryGetValue(first.Host.Name, out var current);
        var result = BlockDialog.Edit(this, targets.Count, current);
        if (result is null) return;

        foreach (var tile in targets)
        {
            _settings.Blocklists[tile.Host.Name] = result;
            tile.SetBlocklist(result);
        }
        _settings.Save();
        _status.Text = $"ตั้งรายการบล็อกให้ {targets.Count} เครื่องแล้ว (โปรแกรม {result.Programs.Count}, เว็บ {result.Urls.Count})";
    }

    // ---------- ห้องชุมชน (Hub) ----------

    private CommunityForm? _community;

    private void OpenCommunity()
    {
        if (_community is { IsDisposed: false })
        {
            _community.WindowState = FormWindowState.Normal;
            _community.Activate();
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.HubAddress))
        {
            var values = Prompt.Show(this, "ตั้งค่าเซิร์ฟเวอร์ชุมชน",
                ("ที่อยู่เซิร์ฟเวอร์ (IP ของเครื่องที่รัน Rain Drop Hub):", "192.168.1."),
                ("ชื่อที่จะแสดง:", _settings.DisplayName.Length > 0 ? _settings.DisplayName : $"ผู้ดูแล ({Environment.UserName})"));
            if (values is null || values[0].Length == 0) return;
            _settings.HubAddress = values[0];
            _settings.DisplayName = values[1];
            _settings.Save();
        }

        var (host, port) = new HostEntry { Address = _settings.HubAddress }.Resolve(HubProtocol.DefaultPort);
        string name = _settings.DisplayName.Length > 0 ? _settings.DisplayName : $"ผู้ดูแล ({Environment.UserName})";
        var hub = new HubClient(host, port, name, MemberRole.Admin, _key.Fingerprint);
        _community = new CommunityForm(hub, name);
        _community.FormClosed += (_, _) => { hub.Dispose(); _community = null; };
        _community.Show();
    }

    private void ShowSettings()
    {
        var values = Prompt.Show(this, "ตั้งค่า",
            ("พอร์ตของ Agent:", _settings.Port.ToString()),
            ("เซิร์ฟเวอร์ชุมชน (Hub):", _settings.HubAddress),
            ("ชื่อที่แสดงในชุมชน:", _settings.DisplayName));
        if (values is not null)
        {
            if (int.TryParse(values[0], out int port) && port is > 0 and < 65536 && port != _settings.Port)
            {
                _settings.Port = port;
                foreach (var tile in _tiles) tile.Reconnect();
            }
            _settings.HubAddress = values[1];
            _settings.DisplayName = values[2];
            _settings.Save();
        }

        var answer = UiDialog.Confirm(this, "กุญแจยืนยันตัวตน",
            $"ลายนิ้วมือกุญแจของ Master:  {_key.Fingerprint}\n" +
            "(ต้องตรงกับที่เครื่องลูกแสดง: คลิกขวาไอคอนโล่ → สถานะการเชื่อมต่อ)\n\n" +
            "ตั้งแต่ C2a การติดตั้ง Agent ใช้ enrollment bundle (.rdrop) ที่มีรหัสผ่าน แทน master-public.pem\n" +
            "เมนู เพิ่มเติม → ส่งออก enrollment bundle สำหรับเครื่องใหม่\n\n" +
            "CA public: " + MasterPki.CaPublicPath + "\n" +
            "กุญแจลับ CA/Master เข้ารหัส DPAPI CurrentUser ในโฟลเดอร์นี้ ห้ามลบหรือย้าย\n\nเปิดโฟลเดอร์เลยไหม?",
            "เปิดโฟลเดอร์");
        if (answer)
            Process.Start("explorer.exe", $"/select,\"{MasterPki.CaPublicPath}\"");
    }

    private void ExportEnrollment()
    {
        var values = Prompt.Show(this, "ส่งออก enrollment bundle",
            ("ชื่อเครื่องลูก (ต้องตรงกับชื่อ Windows):", ""),
            ("IP/ชื่อโฮสต์เพิ่มเติม คั่นด้วยจุลภาค:", ""));
        if (values is null || string.IsNullOrWhiteSpace(values[0])) return;
        var secret = Prompt.ShowWithSecrets(this, "รหัส enrollment bundle",
            ("รหัสอย่างน้อย 12 ตัวอักษร:", "", true));
        if (secret is null) return;
        using var dialog = new SaveFileDialog { Filter = "Rain Drop enrollment (*.rdrop)|*.rdrop", FileName = values[0] + ".rdrop" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            byte[] bundle = _pki.ExportEnrollment(values[0], values[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                secret[0], File.ReadAllText(MasterKey.PublicPath));
            File.WriteAllBytes(dialog.FileName, bundle);
            UiDialog.Show(this, "Enrollment", "ส่งออกแล้ว: " + dialog.FileName + "\nใช้ไฟล์นี้ตอนติดตั้ง Agent แทน master-public.pem\nรหัส bundle ใช้ครั้งเดียวตอนติดตั้ง ไม่ถูกเก็บในไฟล์");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "ส่งออกไม่สำเร็จ", ex.Message, DialogKind.Error);
        }
    }

    private void RevokeSelected()
    {
        var targets = Targets();
        if (targets.Count == 0 || !Confirm("เพิกถอนใบรับรอง ต้อง enroll ใหม่ก่อนใช้งานต่อ", targets.Count)) return;
        foreach (var tile in targets)
        {
            try
            {
                var snapshot = _pki.Revoke(tile.Host.Name);
                tile.Client?.SendJson(ControlMessages.RevocationList, snapshot);
                tile.Reconnect();
                _status.Text = "เพิกถอนแล้ว: " + tile.Host.Name;
            }
            catch (Exception ex)
            {
                UiDialog.Show(this, "เพิกถอนไม่สำเร็จ", tile.Host.Name + ": " + ex.Message, DialogKind.Error);
            }
        }
    }

    private void VerifyAudit()
    {
        var result = AuditLog.Verify(MasterPki.AuditFolder);
        UiDialog.Show(this, "Audit", result.Valid ? $"audit ถูกต้อง ({result.Records} รายการ)" : "audit ไม่ถูกต้อง: " + result.Error,
            result.Valid ? DialogKind.Info : DialogKind.Error);
    }

    private bool Confirm(string text, int count) =>
        UiDialog.Confirm(this, "ยืนยันคำสั่ง", $"{text}\nจะกระทบ {count} เครื่อง", "ยืนยัน", danger: true);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sidebarTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var t in _toasts.ToList()) t.Close();
        _discoveryCts.Cancel();
        foreach (var chat in _chats.Values) chat.ForceClose();
        foreach (var tile in _tiles) tile.Dispose();
        _pki.Dispose();
        _key.Dispose();
        base.OnFormClosed(e);
    }
}
