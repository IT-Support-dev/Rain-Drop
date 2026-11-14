using System.Diagnostics;
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
    private readonly FlowLayoutPanel _panel;                 // ตารางจอภาพ (เนื้อหาจริง)
    private readonly FlowLayoutPanel _channelList = new();   // รายการ "ช่อง" = เครื่องลูก (แถบซ้าย)
    private readonly FlowLayoutPanel _memberList = new();    // รายชื่อออนไลน์ (แถบขวา)
    private readonly Label _status = new();
    private readonly Label _memberHeader = new();
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
        Size = new Size(1400, 860);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9);

        _settings = MasterSettings.Load();
        _key = MasterKey.LoadOrCreate();

        BackColor = DiscordTheme.Content;
        _panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DiscordTheme.Content, Padding = new Padding(10) };

        Controls.Add(BuildContent());   // Fill (กลาง)
        Controls.Add(BuildMembers());   // Right
        Controls.Add(BuildSidebar());   // Left (แถบช่อง)
        Controls.Add(BuildServerRail()); // Left (แถบเซิร์ฟเวอร์ ซ้ายสุด)

        foreach (var host in _settings.Hosts)
            AddTile(host);

        _sidebarTimer.Tick += (_, _) => RefreshSidebars();
        _sidebarTimer.Start();
        RefreshSidebars();
    }

    // ===================== หน้าตาแบบ Discord =====================

    /// <summary>แถบเซิร์ฟเวอร์ซ้ายสุด (ตกแต่งเลียนแบบ Discord)</summary>
    private Control BuildServerRail()
    {
        var rail = new Panel { Dock = DockStyle.Left, Width = 72, BackColor = DiscordTheme.ServerRail };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = DiscordTheme.ServerRail, Padding = new Padding(0, 12, 0, 0) };

        var logo = DiscordTheme.ServerIcon("RD", DiscordTheme.Blurple, "Rain Drop");
        var classroom = DiscordTheme.ServerIcon("ห้อง", Color.FromArgb(87, 150, 120), "ห้องเรียน");
        var add = DiscordTheme.ServerIcon("＋", Color.FromArgb(53, 55, 60), "เพิ่มเครื่องเอง");
        add.Click += (_, _) => AddHost();

        flow.Controls.Add(logo);
        flow.Controls.Add(new Panel { Size = new Size(48, 2), Margin = new Padding(12, 2, 12, 6), BackColor = Color.FromArgb(53, 55, 60) });
        flow.Controls.Add(classroom);
        flow.Controls.Add(add);
        rail.Controls.Add(flow);
        return rail;
    }

    /// <summary>แถบช่อง = รายการเครื่องลูก + แผงผู้ใช้ด้านล่าง</summary>
    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 240, BackColor = DiscordTheme.Sidebar };

        var header = new Label { Dock = DockStyle.Top, Height = 48, Text = "  Rain Drop", ForeColor = DiscordTheme.TextBright, Font = new Font("Tahoma", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, BackColor = DiscordTheme.Sidebar };
        var cat = new Label { Dock = DockStyle.Top, Height = 26, Text = "▾  เครื่องในเครือข่าย", ForeColor = DiscordTheme.TextMuted, Font = new Font("Tahoma", 8, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 6, 0, 0) };

        _channelList.Dock = DockStyle.Fill;
        _channelList.FlowDirection = FlowDirection.TopDown;
        _channelList.WrapContents = false;
        _channelList.AutoScroll = true;
        _channelList.BackColor = DiscordTheme.Sidebar;
        _channelList.Padding = new Padding(6, 2, 6, 2);

        // แผงผู้ใช้ด้านล่าง (เลียนแบบ Discord)
        var user = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = DiscordTheme.ServerRail };
        var avatar = DiscordTheme.ServerIcon("A", DiscordTheme.Blurple);
        avatar.Size = new Size(34, 34);
        avatar.Margin = new Padding(8, 9, 6, 9);
        var uname = new Label { Dock = DockStyle.Fill, Text = "ผู้ดูแลระบบ\n● ออนไลน์", ForeColor = DiscordTheme.TextNormal, Font = new Font("Tahoma", 8), TextAlign = ContentAlignment.MiddleLeft };
        var gear = DiscordTheme.HeaderButton("⚙");
        gear.Dock = DockStyle.Right;
        gear.Click += (_, _) => ShowSettings();
        user.Controls.Add(uname);
        user.Controls.Add(gear);
        var av = new Panel { Dock = DockStyle.Left, Width = 48, BackColor = DiscordTheme.ServerRail };
        av.Controls.Add(avatar);
        user.Controls.Add(av);

        sidebar.Controls.Add(_channelList);
        sidebar.Controls.Add(user);
        sidebar.Controls.Add(cat);
        sidebar.Controls.Add(header);
        return sidebar;
    }

    /// <summary>พื้นที่กลาง: แถบหัวเรื่อง + ปุ่มคำสั่ง + ตารางจอภาพ</summary>
    private Control BuildContent()
    {
        var center = new Panel { Dock = DockStyle.Fill, BackColor = DiscordTheme.Content };

        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = DiscordTheme.Content };
        var title = new Label { Dock = DockStyle.Left, Width = 230, Text = "#  จอภาพเครื่องลูก", ForeColor = DiscordTheme.TextBright, Font = new Font("Tahoma", 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, BackColor = DiscordTheme.Content, Padding = new Padding(2, 7, 2, 7) };
        void HB(string text, Action act) { var b = DiscordTheme.HeaderButton(text); b.Click += (_, _) => act(); actions.Controls.Add(b); }
        HB("🔄 ค้นหา", () => _ = DiscoverAsync(userInitiated: true));
        HB("＋ เพิ่ม", AddHost);
        HB("🖥 ควบคุม", OpenRemoteForSelected);
        HB("🔒 ล็อก", LockTargets);
        HB("🔓 ปลดล็อก", () => Broadcast(c => c.Send(MsgType.Unlock)));
        HB("📊 กิจกรรม", OpenActivityForSelected);
        HB("🚫 บล็อก", OpenBlockSettings);
        HB("💬 แชท", OpenChatForSelected);
        HB("📢 ข้อความ", SendMessageToTargets);
        HB("⏻ ปิดเครื่อง", () => Power(PowerAction.Shutdown, "ปิดเครื่อง"));
        HB("⋯ เพิ่มเติม", ShowMoreMenu);

        header.Controls.Add(actions);
        header.Controls.Add(title);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;
        _status.BackColor = DiscordTheme.ServerRail;
        _status.ForeColor = DiscordTheme.TextMuted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 0, 0);
        _status.Text = "คลิกช่องทางซ้ายเพื่อเลือกเครื่อง, ดับเบิลคลิกเพื่อควบคุม";

        center.Controls.Add(_panel);
        center.Controls.Add(_status);
        center.Controls.Add(header);
        return center;
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("ลบเครื่องที่เลือก", null, (_, _) => RemoveSelected());
        menu.Items.Add("เลือกทั้งหมด / ไม่เลือก", null, (_, _) => ToggleSelectAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("เปิดเว็บ/โปรแกรม", null, (_, _) => RunOnTargets());
        menu.Items.Add("บล็อกโปรแกรม / เว็บ...", null, (_, _) => OpenBlockSettings());
        menu.Items.Add("ชุมชน (แชท/คอลเสียง)", null, (_, _) => OpenCommunity());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settings.AlertsEnabled ? "การแจ้งเตือน: เปิดอยู่ (กดเพื่อปิด)" : "การแจ้งเตือน: ปิดอยู่ (กดเพื่อเปิด)",
            null, (_, _) => ToggleAlerts());
        menu.Items.Add("ดูการแจ้งเตือนล่าสุด", null, (_, _) => ShowAlertLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("รีสตาร์ท", null, (_, _) => Power(PowerAction.Restart, "รีสตาร์ท"));
        menu.Items.Add("ล็อกออฟ", null, (_, _) => Power(PowerAction.Logoff, "ล็อกออฟ"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ตั้งค่า / กุญแจ", null, (_, _) => ShowSettings());
        menu.Show(Cursor.Position);
    }

    /// <summary>แถบขวา: รายชื่อออนไลน์ (เลียนแบบ Discord)</summary>
    private Control BuildMembers()
    {
        var members = new Panel { Dock = DockStyle.Right, Width = 220, BackColor = DiscordTheme.Sidebar };
        _memberHeader.Dock = DockStyle.Top;
        _memberHeader.Height = 32;
        _memberHeader.Text = "ออนไลน์";
        _memberHeader.ForeColor = DiscordTheme.TextMuted;
        _memberHeader.Font = new Font("Tahoma", 8, FontStyle.Bold);
        _memberHeader.TextAlign = ContentAlignment.MiddleLeft;
        _memberHeader.Padding = new Padding(14, 8, 0, 0);

        _memberList.Dock = DockStyle.Fill;
        _memberList.FlowDirection = FlowDirection.TopDown;
        _memberList.WrapContents = false;
        _memberList.AutoScroll = true;
        _memberList.BackColor = DiscordTheme.Sidebar;
        _memberList.Padding = new Padding(8, 2, 8, 2);

        members.Controls.Add(_memberList);
        members.Controls.Add(_memberHeader);
        return members;
    }

    // ===================== อัปเดตแถบช่อง/รายชื่อจากสถานะเครื่องจริง =====================

    private void RefreshSidebars()
    {
        int online = _tiles.Count(t => t.IsOnline);

        _channelList.SuspendLayout();
        _channelList.Controls.Clear();
        foreach (var tile in _tiles)
            _channelList.Controls.Add(ChannelRow(tile));
        _channelList.ResumeLayout();

        _memberHeader.Text = $"ออนไลน์ — {online}";
        _memberList.SuspendLayout();
        _memberList.Controls.Clear();
        _memberList.Controls.Add(MemberLabel("ผู้ดูแลระบบ", true, admin: true));
        foreach (var tile in _tiles.OrderByDescending(t => t.IsOnline).ThenBy(t => t.Host.Name))
            _memberList.Controls.Add(MemberLabel(tile.Host.Name, tile.IsOnline, admin: false));
        _memberList.ResumeLayout();
    }

    private Control ChannelRow(HostTile tile)
    {
        var row = new Label
        {
            Width = 210,
            Height = 30,
            Text = $"#  {tile.Host.Name}",
            ForeColor = tile.Selected ? DiscordTheme.TextBright : (tile.IsOnline ? DiscordTheme.TextNormal : DiscordTheme.TextMuted),
            BackColor = tile.Selected ? DiscordTheme.Selected : DiscordTheme.Sidebar,
            Font = new Font("Tahoma", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Margin = new Padding(0, 1, 0, 1),
            Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => { if (!tile.Selected) row.BackColor = DiscordTheme.Hover; };
        row.MouseLeave += (_, _) => { if (!tile.Selected) row.BackColor = DiscordTheme.Sidebar; };
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
        menu.Items.Add("ดูกิจกรรม", null, (_, _) => OpenActivity(tile));
        menu.Items.Add("ควบคุมระยะไกล", null, (_, _) => OpenRemote(tile));
        menu.Items.Add("แชท", null, (_, _) => OpenChat(tile));
        row.ContextMenuStrip = menu;
        return row;
    }

    private static Control MemberLabel(string name, bool online, bool admin)
    {
        return new Label
        {
            Width = 196,
            Height = 28,
            Text = "●  " + name,
            ForeColor = online ? DiscordTheme.TextNormal : DiscordTheme.TextMuted,
            BackColor = DiscordTheme.Sidebar,
            Font = new Font("Tahoma", 9),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Margin = new Padding(0, 1, 0, 1),
        };
    }

    private void AddTile(HostEntry host)
    {
        var tile = new HostTile(host, _settings, _key);
        tile.OpenRemoteRequested += (_, _) => OpenRemote(tile);
        tile.ChatReceived += OnChatReceived;
        tile.VoiceReceived += OnVoiceReceived;
        tile.AlertReceived += OnAlertReceived;

        // ส่งรายการบล็อกที่เคยตั้งไว้ของเครื่องนี้ (จะถูกส่งเมื่อเชื่อมต่อ)
        if (_settings.Blocklists.TryGetValue(host.Name, out var rules))
            tile.SetBlocklist(rules);

        var menu = new ContextMenuStrip();
        menu.Items.Add("แชท", null, (_, _) => OpenChat(tile));
        menu.Items.Add("ควบคุมระยะไกล", null, (_, _) => OpenRemote(tile));
        menu.Items.Add("ดูกิจกรรม", null, (_, _) => OpenActivity(tile));
        foreach (Control c in tile.Controls) c.ContextMenuStrip = menu;
        _tiles.Add(tile);
        _panel.Controls.Add(tile);
        tile.Start();
    }

    // ---------- จัดการรายชื่อเครื่อง ----------

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
        if (selected.Count == 0)
        {
            _status.Text = "กรุณาเลือกเครื่องที่จะลบก่อน";
            return;
        }
        if (!Confirm($"ลบ {selected.Count} เครื่องออกจากรายการ?")) return;

        foreach (var tile in selected)
        {
            _settings.Hosts.Remove(tile.Host);
            if (!_settings.IgnoredMachines.Contains(tile.Host.Name, StringComparer.OrdinalIgnoreCase))
                _settings.IgnoredMachines.Add(tile.Host.Name); // กันไม่ให้การค้นหาอัตโนมัติเพิ่มกลับมา
            _tiles.Remove(tile);
            _panel.Controls.Remove(tile);
            if (_chats.Remove(tile, out var chat)) chat.ForceClose();
            tile.Dispose();
        }
        _settings.Save();
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

    private void OpenRemote(HostTile tile) => new RemoteForm(tile.Host, _settings, _key).Show();

    private void OpenActivity(HostTile tile) => new ActivityForm(tile.Host, _settings, _key).Show();

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
        if (!Confirm($"ยืนยัน{label} {count} เครื่อง?")) return;
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
        _status.Text = $"🔔 {title}: {msg.Text}  ({msg.Time:HH:mm:ss})";
        ShowToast(title, msg.Text, msg.Kind);
        try { _tray.ShowBalloonTip(5000, title, msg.Text, ToolTipIcon.Warning); } catch { }
    }

    private void ShowToast(string title, string text, string kind)
    {
        var accent = kind.Contains("USB") || kind.Contains("ส่งออก") ? Color.FromArgb(240, 178, 50)
                   : kind.Contains("บล็อก") ? Color.FromArgb(237, 66, 69)
                   : DiscordTheme.Blurple;
        var toast = new ToastForm(title, text, accent);
        toast.FormClosed += (_, _) => { _toasts.Remove(toast); RepositionToasts(); };
        _toasts.Insert(0, toast);
        RepositionToasts();
        toast.Show();
    }

    private void RepositionToasts()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int y = wa.Bottom - 16;
        foreach (var t in _toasts)
        {
            y -= t.Height + 8;
            t.Location = new Point(wa.Right - t.Width - 16, y);
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
        MessageBox.Show(text, "การแจ้งเตือนล่าสุด", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        var answer = MessageBox.Show(this,
            $"ลายนิ้วมือกุญแจของ Master:  {_key.Fingerprint}\n" +
            "(ต้องตรงกับที่เครื่องลูกแสดง: คลิกขวาไอคอนโล่ → สถานะการเชื่อมต่อ)\n\n" +
            "กุญแจสาธารณะของ Master อยู่ที่:\n" + MasterKey.PublicPath +
            "\n\nถ้าติดตั้งเครื่องลูกด้วย RainDrop-Agent-Setup.exe ที่ build มาพร้อมกัน ไม่ต้องทำอะไรเพิ่ม" +
            "\nถ้าติดตั้งเอง ให้คัดลอกไฟล์นี้ไปวางข้าง RainDrop.Agent.exe แล้วเปลี่ยนชื่อเป็น agent-public.pem" +
            "\n\nห้ามคัดลอก master-private.pem ออกไปที่อื่น\n\nเปิดโฟลเดอร์เลยไหม?",
            "กุญแจยืนยันตัวตน", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes)
            Process.Start("explorer.exe", $"/select,\"{MasterKey.PublicPath}\"");
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, "ยืนยัน", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sidebarTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var t in _toasts.ToList()) t.Close();
        _discoveryCts.Cancel();
        foreach (var chat in _chats.Values) chat.ForceClose();
        foreach (var tile in _tiles) tile.Dispose();
        base.OnFormClosed(e);
    }
}
