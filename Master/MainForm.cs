using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// หน้าจอหลัก: แสดงการ์ดเครื่องลูกเป็นตาราง + แถบเครื่องมือสั่งงาน
/// คำสั่งจะส่งไปยังเครื่องที่เลือกไว้ ถ้าไม่ได้เลือกเลยจะส่งไปทุกเครื่อง
/// เปิดด้วย --ui-preview (เฉพาะ DEBUG) จะแสดงข้อมูลจำลอง 6 เครื่องโดยไม่เชื่อมต่อเครือข่าย
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly MasterPki _pki;
    private readonly FlowLayoutPanel _panel;                 // ตารางการ์ดเครื่อง (เนื้อหาจริง)
    private readonly FlowLayoutPanel _channelList = new UiFlowPanel();   // รายการเครื่องในแถบซ้าย
    private readonly Label _status = new();
    private readonly Label _count = new() { AutoSize = true, Font = Theme.FontCaption, ForeColor = Theme.TextMuted };
    private readonly System.Windows.Forms.Timer _sidebarTimer = new() { Interval = 1500 };
    private readonly List<ITile> _tiles = new();
    private readonly Dictionary<ITile, ChatWindow> _chats = new();
    private readonly string _myName = $"ผู้ดูแลระบบ ({Environment.UserName})";
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Load(), Text = "Rain Drop Master", Visible = true };
    private readonly List<AlertMsg> _alertLog = new();
    private readonly List<ToastForm> _toasts = new();
    private readonly bool _uiPreview;                        // โหมดทดสอบ UI ด้วยข้อมูลจำลอง

    public MainForm(string[] args)
    {
        _uiPreview = args.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase);

#if !DEBUG
        // --ui-preview เปิดได้เฉพาะบิลด์ DEBUG เท่านั้น ป้องกันไม่ให้ติดไปกับตัวติดตั้งจริง
        _uiPreview = false;
#endif

        Text = "Rain Drop Master";
        Icon = AppIcon.Load();
        Theme.ConfigureForm(this, new Size(900, 600));
        UiWindowPlacement.Attach(this, "master", new Size(1400, 860), new Size(900, 600));

        if (_uiPreview)
        {
            // โหมดตัวอย่าง: ไม่เขียนตั้งค่า ไม่เชื่อมต่อเครือข่าย ใช้ข้อมูลจำลอง
            // สร้าง CA คู่เดียวแล้วออก client cert จากคู่นั้น เพื่อให้ _pki และ _key ไม่ null
            // (ทั้งคู่ไม่ถูกใช้จริงในโหมดตัวอย่าง เพราะทุกเส้นทางคำสั่งจะคืนเร็วด้วยข้อความ "โหมดตัวอย่าง")
            var ca = RepoCertificates.CreateCa();
            var caKey = ca.GetECDsaPrivateKey()!;
            var clientCert = RepoCertificates.IssueClient(ca, caKey);
            _settings = new MasterSettings { AutoDiscover = false };
            _key = MasterKey.CreateForPreview(ECDsa.Create(ECCurve.NamedCurves.nistP256));
            _pki = MasterPki.CreateForPreview(caKey, ca, clientCert);

            AddMockTiles();
        }
        else
        {
            _settings = MasterSettings.Load();
            _key = MasterKey.LoadOrCreate();
            _pki = MasterPki.LoadOrCreate();

            foreach (var host in _settings.Hosts)
                AddTile(host);
        }

        _panel = new UiFlowPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.ZoneContent };
        ApplyDarkScrollbars(_panel);

        var content = BuildContent();
        content.TabIndex = 1;
        Controls.Add(content);
        var sidebar = BuildSidebar();
        sidebar.TabIndex = 0;
        Controls.Add(sidebar);

        ApplyFilter();

        _sidebarTimer.Tick += (_, _) => ApplyFilter();
        _sidebarTimer.Start();
        RefreshSidebars();
    }

    // ===================== โหมดตัวอย่าง UI =====================

    private void AddMockTiles()
    {
        var mocks = new[]
        {
            ("สำนักงานใหญ่", "192.168.1.10", true,  StatusKind.Online),
            ("แผนกบัญชี",    "192.168.1.23", false, StatusKind.Offline),
            ("แผนกไอที",     "192.168.1.41", true,  StatusKind.Online),
            ("คลังสินค้า",   "192.168.1.77", false, StatusKind.Waiting),
            ("ห้องประชุม",   "192.168.1.91", false, StatusKind.Error),
            ("ผู้บริหาร",     "192.168.1.55", true,  StatusKind.Online),
        };
        foreach (var (name, address, online, kind) in mocks)
        {
            var host = new HostEntry { Name = name, Address = address };
            var tile = new PreviewHostTile(host, kind);
            _tiles.Add(tile);
            _panel.Controls.Add(tile);
        }
    }

    // ===================== โครงหน้าจอแบ่งโซนสี =====================

    private Control BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = Theme.SidebarWidth,
            BackColor = Theme.ZoneSidebar,
            Padding = new Padding(Theme.Space3),
        };

        // หัวเรื่องพร้อมโลโก้หยดน้ำ (ขนาดสมดุลกับข้อความ)
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Space6 * 2,
            AutoSize = false,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        var logo = new PictureBox
        {
            Image = AppIcon.Load().ToBitmap(),
            Size = new Size(Theme.IconSizeLarge * 2, Theme.IconSizeLarge * 2),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        var title = new Label
        {
            Text = "Rain Drop",
            Font = Theme.FontTitle,          // ลดลงเหลือ 12pt สมดุลกับโลโก้
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(Theme.Space2, 0, 0, 0),
        };
        header.Controls.Add(logo);
        header.Controls.Add(title);

        // ช่องค้นหา: UiInput แบบวาดเอง มี placeholder + ไอคอนแว่น
        var searchLabel = Theme.SectionLabel("ค้นหาเครื่อง");
        var search = UiInput.SearchBox("ค้นหาเครื่อง...", text => { _query = text; ApplyFilter(); });
        search.Dock = DockStyle.Top;
        search.Height = Theme.InputHeight;
        Theme.SetToolTip(search, "ค้นหาชื่อเครื่องหรือ IP");

        // ตัวกรองสถานะ: ปุ่ม chip แบบ segmented แทน ComboBox
        var filterLabel = Theme.SectionLabel("สถานะ");
        var filter = new UiSegmentedFilter(new[] { "ทั้งหมด", "ออนไลน์", "ออฟไลน์" }, 0);
        filter.SelectionChanged += (_, i) => { _statusFilter = (MachineStatusFilter)i; ApplyFilter(); };

        _count.Dock = DockStyle.Top;
        _count.Padding = new Padding(0, Theme.Space2, 0, Theme.Space2);
        _count.ForeColor = Theme.TextMuted;

        _channelList.Dock = DockStyle.Fill;
        _channelList.FlowDirection = FlowDirection.TopDown;
        _channelList.WrapContents = false;
        _channelList.AutoScroll = true;
        _channelList.BackColor = Theme.ZoneSidebar;
        ApplyDarkScrollbars(_channelList);

        // ปุ่มท้ายแถบ: ตั้งค่า / ชุมชน เป็น ghost ธรรมดา
        var settings = new UiButton("ตั้งค่า", ButtonKind.Ghost, UiIcon.Settings) { Dock = DockStyle.Bottom };
        settings.TabIndex = 4;
        settings.Click += (_, _) => ShowSettings();
        var community = new UiButton("ชุมชน", ButtonKind.Ghost, UiIcon.Chat) { Dock = DockStyle.Bottom };
        community.TabIndex = 3;
        community.Click += (_, _) => OpenCommunity();

        // ปุ่ม "เพิ่มเครื่อง" เป็น primary เต็มความกว้างอยู่เหนือรายการปุ่มอื่น
        var add = new UiButton("เพิ่มเครื่อง", ButtonKind.Primary, UiIcon.Plus) { Dock = DockStyle.Bottom };
        add.Height = Theme.ButtonHeightLarge;
        add.Margin = new Padding(0, Theme.Space2, 0, 0);
        add.TabIndex = 2;
        add.Click += (_, _) => AddHost();

        sidebar.Controls.Add(_channelList);
        sidebar.Controls.Add(_count);
        sidebar.Controls.Add(filter);
        sidebar.Controls.Add(search);
        sidebar.Controls.Add(searchLabel);
        sidebar.Controls.Add(header);
        sidebar.Controls.Add(settings);
        sidebar.Controls.Add(community);
        sidebar.Controls.Add(add);

        // เส้นแบ่งท้ายแถบซ้าย
        var divider = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.Divider };
        sidebar.Controls.Add(divider);
        divider.BringToFront();

        return sidebar;
    }

    private Control BuildContent()
    {
        var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };

        // แถบเครื่องมือ: แบ่งกลุ่มด้วยช่องว่าง ปุ่มหลักมีข้อความกำกับ ไอคอน 20px พื้นที่กด ≥ 36px
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.ToolbarHeight,
            Padding = new Padding(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2),
            BackColor = Theme.ZoneHeader,
            WrapContents = false,
            AutoScroll = true,
        };
        ApplyDarkScrollbars(toolbar);

        void Group(string label)
        {
            var g = new Label
            {
                Text = label,
                Font = Theme.FontCaptionStrong,
                ForeColor = Theme.TextMuted,
                AutoSize = true,
                Margin = new Padding(Theme.Space3, Theme.Space1, Theme.Space1, Theme.Space1),
                Height = Theme.ButtonHeight,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            toolbar.Controls.Add(g);
        }

        void Tool(UiIcon icon, string text, string tip, Action act, ButtonKind kind = ButtonKind.Ghost, bool showLabel = false)
        {
            var container = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(Theme.Space2, 0, Theme.Space2, 0),
            };
            // ปุ่มหลักเป็นไอคอนอย่างเดียว ข้อความกำกับอยู่ใต้ปุ่ม
            var b = new UiButton("", kind, icon)
            {
                Width = Theme.ButtonHeightLarge,
                Height = Theme.ButtonHeightLarge,
                Margin = new Padding(0, 0, 0, Theme.Space1),
            };
            Theme.SetToolTip(b, tip);
            b.Click += (_, _) => act();
            container.Controls.Add(b);
            if (showLabel && !string.IsNullOrEmpty(text))
            {
                var lbl = new Label
                {
                    Text = text,
                    Font = Theme.FontCaption,
                    ForeColor = Theme.TextSecondary,
                    AutoSize = true,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Margin = new Padding(0, 0, 0, 0),
                };
                container.Controls.Add(lbl);
            }
            toolbar.Controls.Add(container);
        }

        void Sep() => toolbar.Controls.Add(new Panel
        {
            Width = 1,
            Height = Theme.ButtonHeightLarge,
            BackColor = Theme.Divider,
            Margin = new Padding(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1),
        });

        Group("ค้นหา");
        Tool(UiIcon.Refresh, "ค้นหา", "ค้นหาเครื่องในเครือข่าย", () => _ = DiscoverAsync(userInitiated: true), showLabel: true);
        Tool(UiIcon.Plus, "เพิ่ม", "เพิ่มเครื่องเองด้วย IP", AddHost, showLabel: true);
        Sep();

        Group("ควบคุม");
        Tool(UiIcon.Monitor, "ควบคุม", "ควบคุมระยะไกลเครื่องที่เลือก", OpenRemoteForSelected, showLabel: true);
        Tool(UiIcon.Chat, "แชท", "แชทกับเครื่องที่เลือก", OpenChatForSelected, showLabel: true);
        Tool(UiIcon.Message, "ข้อความ", "ส่งข้อความไปยังเครื่องที่เลือก", SendMessageToTargets, showLabel: true);
        Sep();

        Group("การล็อก");
        Tool(UiIcon.Lock, "ล็อก", "ล็อกหน้าจอเครื่องที่เลือก", LockTargets, showLabel: true);
        Tool(UiIcon.Unlock, "ปลดล็อก", "ปลดล็อกเครื่องที่เลือก", () => Broadcast(c => c.Send(MsgType.Unlock)), showLabel: true);
        Sep();

        Group("พลังงาน");
        var powerMenu = new ContextMenuStrip();
        Theme.StyleMenu(powerMenu);
        var shutdown = new UiButton("ปิดเครื่อง", ButtonKind.Danger, UiIcon.Power)
        {
            Width = Theme.ButtonHeightLarge,
            Height = Theme.ButtonHeightLarge,
            ContextMenuStrip = powerMenu,
        };
        Theme.SetToolTip(shutdown, "ปิดเครื่อง / รีสตาร์ท / ออกจากระบบ");
        powerMenu.Items.Add("ปิดเครื่อง", null, (_, _) => Power(PowerAction.Shutdown, "ปิดเครื่อง"));
        powerMenu.Items.Add("รีสตาร์ท", null, (_, _) => Power(PowerAction.Restart, "รีสตาร์ท"));
        powerMenu.Items.Add("ออกจากระบบ", null, (_, _) => Power(PowerAction.Logoff, "ออกจากระบบ"));
        powerMenu.Closed += (_, _) => powerMenu.Dispose();
        shutdown.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                powerMenu.Show(shutdown, new Point(0, shutdown.Height));
            }
        };
        var powerContainer = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(Theme.Space2, 0, Theme.Space2, 0),
        };
        powerContainer.Controls.Add(shutdown);
        var powerLbl = new Label
        {
            Text = "พลังงาน",
            Font = Theme.FontCaption,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        powerContainer.Controls.Add(powerLbl);
        toolbar.Controls.Add(powerContainer);
        Sep();

        Group("ดูแลระบบ");
        Tool(UiIcon.Activity, "กิจกรรม", "ดูกิจกรรมเครื่องที่เลือก", OpenActivityForSelected, showLabel: true);
        Tool(UiIcon.Block, "บล็อก", "ตั้งค่าบล็อกโปรแกรม/เว็บ", OpenBlockSettings, showLabel: true);
        Tool(UiIcon.More, "เพิ่มเติม", "เมนูเพิ่มเติม", ShowMoreMenu, showLabel: true);

        // พื้นที่แสดงการ์ดเครื่อง
        _panel.Padding = new Padding(Theme.Space3);

        // Empty state ออกแบบใหม่: ไอคอนใหญ่ หัวข้อใหญ่ คำอธิบาย ปุ่มสองปุ่มกึ่งกลาง
        _empty = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };
        var emptyContainer = new Panel
        {
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var emptyIcon = new PictureBox
        {
            Image = UiIcons.Get(UiIcon.Computer, Theme.TextMuted, 64),
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, Theme.Space4),
        };
        var emptyHeading = new Label
        {
            Text = "ยังไม่มีเครื่องลูก",
            Font = Theme.FontHeading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, Theme.Space2),
        };
        var emptyDesc = new Label
        {
            Text = "กด \"ค้นหา\" เพื่อหาเครื่องในเครือข่าย หรือกด \"เพิ่ม\" เพื่อเพิ่มเครื่องด้วย IP",
            Font = Theme.FontBody,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            MaximumSize = new Size(340, 0),
            Margin = new Padding(0, 0, 0, Theme.Space6),
        };
        var emptyButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        var btnSearch = new UiButton("ค้นหาในเครือข่าย", ButtonKind.Primary, UiIcon.Refresh)
        {
            Height = Theme.ButtonHeightLarge,
            Width = Theme.ButtonWidthWide,
            Margin = new Padding(0, 0, Theme.Space2, 0),
        };
        btnSearch.Click += (_, _) => _ = DiscoverAsync(userInitiated: true);
        var btnAdd = new UiButton("เพิ่มด้วย IP", ButtonKind.Secondary, UiIcon.Plus)
        {
            Height = Theme.ButtonHeightLarge,
            Width = Theme.ButtonWidthWide,
        };
        btnAdd.Click += (_, _) => AddHost();
        emptyButtons.Controls.Add(btnSearch);
        emptyButtons.Controls.Add(btnAdd);

        emptyContainer.Controls.Add(emptyIcon);
        emptyContainer.Controls.Add(emptyHeading);
        emptyContainer.Controls.Add(emptyDesc);
        emptyContainer.Controls.Add(emptyButtons);

        // จัด empty state กึ่งกลางพื้นที่
        var emptyCenter = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var emptyWrapper = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.None,
        };
        emptyWrapper.Controls.Add(emptyContainer);
        emptyWrapper.Dock = DockStyle.None;
        emptyCenter.Controls.Add(emptyWrapper);
        emptyWrapper.Location = new Point(0, 0); // จะจัดตำแหน่งใน Resize
        emptyCenter.Resize += (_, _) =>
        {
            emptyWrapper.Left = (emptyCenter.Width - emptyWrapper.Width) / 2;
            emptyWrapper.Top = (emptyCenter.Height - emptyWrapper.Height) / 2;
        };
        _empty.Controls.Add(emptyCenter);

        _status.Dock = DockStyle.Bottom;
        _status.Height = Theme.StatusHeight;
        _status.BackColor = Theme.ZoneStatus;
        _status.ForeColor = Theme.TextSecondary;
        _status.Font = Theme.FontBody; // ≥ 9pt
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(Theme.Space3, 0, 0, 0);
        _status.Text = "เลือกเครื่องเพื่อสั่งงานรายเครื่อง ถ้าไม่เลือกจะส่งไปทุกเครื่อง";
        var statusDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };
        center.Controls.Add(statusDivider);

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

    private readonly Dictionary<ITile, Label> _channelRows = new();
    private string _sidebarSignature = "";
    private string _query = "";
    private MachineStatusFilter _statusFilter = MachineStatusFilter.All;
    private Panel _empty = new() { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };

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
                var back = tile.Selected ? Theme.SurfacePressed : Theme.ZoneSidebar;
                if (row.ForeColor != fore) row.ForeColor = fore;
                if (row.BackColor != back) row.BackColor = back;
            }
        }
        finally { _channelList.ResumeLayout(); }
    }

    private Label CreateChannelRow(ITile tile)
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
        row.MouseLeave += (_, _) => { if (!tile.Selected) row.BackColor = Theme.ZoneSidebar; };
        row.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                foreach (var t in _tiles) t.Selected = t == tile;
                _panel.ScrollControlIntoView((Control)tile);
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
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถเพิ่มเครื่องจริงในโหมดตัวอย่างได้");
            return;
        }
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
            if (!_settings.IgnoredMachines.Any(m => m.Equals(tile.Host.Name, StringComparison.OrdinalIgnoreCase)))
                _settings.IgnoredMachines.Add(tile.Host.Name);
            _tiles.Remove(tile);
            _panel.Controls.Remove((Control)tile);
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
        if (!_uiPreview && _settings.AutoDiscover) _ = DiscoveryLoopAsync();
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
        if (_uiPreview || _scanning) return;
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

                if (_settings.Hosts.Any(h => h.Resolve(_settings.Port).Host == ip)) continue;

                var existing = _settings.Hosts.FirstOrDefault(h => h.Name.Equals(machine, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Address = address;
                    _tiles.FirstOrDefault(t => t.Host == existing)?.Reconnect();
                    moved++;
                    continue;
                }

                if (_settings.IgnoredMachines.Any(m => m.Equals(machine, StringComparison.OrdinalIgnoreCase))) continue;

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

    private void OpenRemote(ITile tile)
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถควบคุมระยะไกลในโหมดตัวอย่างได้");
            return;
        }
        new RemoteForm(tile.Host, _settings, _key, _pki).Show();
    }

    private void OpenActivity(ITile tile) => new ActivityForm(tile.Host, _settings, _key, _pki).Show();

    private void OpenActivityForSelected()
    {
        var tile = _tiles.FirstOrDefault(t => t.Selected);
        if (tile is null) { _status.Text = "เลือกเครื่องที่จะดูกิจกรรมก่อน"; return; }
        OpenActivity(tile);
    }

    // ---------- แชท ----------

    private ChatWindow GetChat(ITile tile)
    {
        if (_chats.TryGetValue(tile, out var existing) && !existing.IsDisposed)
            return existing;

        var chat = new ChatWindow($"แชท - {tile.Host.Name} ({tile.Host.Address})", _myName, text =>
        {
            if (_uiPreview) return false;
            if (tile.Client is not { } client) return false;
            client.SendJson(MsgType.ChatMessage, new ChatMsg(_myName, text, DateTime.Now));
            return true;
        },
        voice =>
        {
            if (_uiPreview) return false;
            if (tile.Client is not { } client) return false;
            client.SendJson(MsgType.VoiceMessage, voice);
            return true;
        });
        chat.Activated += (_, _) => tile.HasUnread = false;
        _chats[tile] = chat;
        return chat;
    }

    private void OpenChat(ITile tile)
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

    private void OnChatReceived(ITile tile, ChatMsg msg)
    {
        if (_uiPreview) return;
        var chat = GetChat(tile);
        chat.Append(msg, mine: false);
        if (!chat.ContainsFocus) tile.HasUnread = true;
        chat.ShowAndNotify(activate: false);
        _status.Text = $"ข้อความใหม่จาก {tile.Host.Name}: {msg.Text}";
    }

    private void OnVoiceReceived(ITile tile, VoiceMsg msg)
    {
        if (_uiPreview) return;
        var chat = GetChat(tile);
        chat.AppendVoice(msg, mine: false);
        if (!chat.ContainsFocus) tile.HasUnread = true;
        chat.ShowAndNotify(activate: false);
        _status.Text = $"ข้อความเสียงใหม่จาก {tile.Host.Name} ({msg.Seconds:0} วินาที)";
    }

    // ---------- คำสั่งกลุ่ม ----------

    private List<ITile> Targets()
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
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถล็อกหน้าจอในโหมดตัวอย่างได้");
            return;
        }
        var values = Prompt.Show(this, "ล็อกหน้าจอ", ("ข้อความบนหน้าจอ:", "เครื่องนี้ถูกล็อกโดยผู้ดูแลระบบ"));
        if (values is null) return;
        int count = Targets().Count;
        if (count > 1 && !Confirm("ล็อกหน้าจอ", count)) return;
        Broadcast(c => c.SendJson(MsgType.Lock, new LockCmd(values[0])));
    }

    private void SendMessageToTargets()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถส่งข้อความในโหมดตัวอย่างได้");
            return;
        }
        var values = Prompt.Show(this, "ส่งข้อความ",
            ("หัวข้อ:", "ข้อความจากผู้ดูแลระบบ"),
            ("ข้อความ:", ""));
        if (values is null || values[1].Length == 0) return;
        Broadcast(c => c.SendJson(MsgType.ShowMessage, new MessageCmd(values[0], values[1])));
    }

    private void RunOnTargets()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถเปิดโปรแกรมในโหมดตัวอย่างได้");
            return;
        }
        var values = Prompt.Show(this, "เปิดเว็บ/โปรแกรม",
            ("URL หรือพาธโปรแกรม:", "https://"),
            ("อาร์กิวเมนต์ (ถ้ามี):", ""));
        if (values is null || values[0].Length == 0) return;
        Broadcast(c => c.SendJson(MsgType.RunCommand,
            new RunCmd(values[0], values[1].Length > 0 ? values[1] : null)));
    }

    private void Power(PowerAction action, string label)
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถสั่งจัดการพลังงานในโหมดตัวอย่างได้");
            return;
        }
        int count = Targets().Count;
        if (!Confirm(label, count)) return;
        Broadcast(c => c.SendJson(MsgType.PowerAction, new PowerCmd(action)));
    }

    // ---------- ตั้งค่า ----------

    private void ShowSettings()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถเปลี่ยนการตั้งค่าในโหมดตัวอย่างได้");
            return;
        }
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

    // ---------- การแจ้งเตือนจากเครื่องลูก ----------

    private void OnAlertReceived(ITile tile, AlertMsg msg)
    {
        if (_uiPreview) return;
        _alertLog.Insert(0, msg);
        if (_alertLog.Count > 200) _alertLog.RemoveAt(_alertLog.Count - 1);

        if (!_settings.AlertsEnabled) return;

        string title = $"{tile.Host.Name} — {msg.Kind}";
        _status.Text = $"{title}: {msg.Text}  ({msg.Time:HH:mm:ss})";
        ShowToast(title, msg.Text, msg.Kind);
        try { _tray.ShowBalloonTip(5000, title, msg.Text, ToolTipIcon.Warning); }
        catch (Exception ex)
        {
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
        if (_uiPreview) return;
        _settings.AlertsEnabled = !_settings.AlertsEnabled;
        _settings.Save();
        _status.Text = _settings.AlertsEnabled ? "เปิดการแจ้งเตือนแล้ว" : "ปิดการแจ้งเตือนแล้ว";
    }

    private void ShowAlertLog()
    {
        if (_uiPreview) return;
        string text = _alertLog.Count == 0
            ? "ยังไม่มีการแจ้งเตือน"
            : string.Join("\n", _alertLog.Take(50).Select(a => $"[{a.Time:HH:mm:ss}] {a.Kind}: {a.Text}"));
        UiDialog.Show(this, "การแจ้งเตือนล่าสุด", text);
    }

    // ---------- บล็อกโปรแกรม / เว็บ ----------

    private void OpenBlockSettings()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถตั้งค่าบล็อกในโหมดตัวอย่างได้");
            return;
        }
        var targets = Targets();
        if (targets.Count == 0) { _status.Text = "ไม่มีเครื่องให้ตั้งค่า"; return; }

        var first = targets[0];
        _settings.Blocklists.TryGetValue(first.Host.Name, out var current);
        var result = BlockDialog.Edit(this, targets.Count, current);
        if (result is null) return;

        foreach (var tile in targets)
        {
            _settings.Blocklists[tile.Host.Name] = result;
            if (tile is HostTile ht) ht.SetBlocklist(result);
        }
        _settings.Save();
        _status.Text = $"ตั้งรายการบล็อกให้ {targets.Count} เครื่องแล้ว (โปรแกรม {result.Programs.Count}, เว็บ {result.Urls.Count})";
    }

    // ---------- ห้องชุมชน (Hub) ----------

    private CommunityForm? _community;

    private void OpenCommunity()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถเปิดห้องชุมชนในโหมดตัวอย่างได้");
            return;
        }
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

    private void ExportEnrollment()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถส่งออก enrollment bundle ในโหมดตัวอย่างได้");
            return;
        }
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
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถเพิกถอนในโหมดตัวอย่างได้");
            return;
        }
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
        if (_uiPreview) return;
        var result = AuditLog.Verify(MasterPki.AuditFolder);
        UiDialog.Show(this, "Audit", result.Valid ? $"audit ถูกต้อง ({result.Records} รายการ)" : "audit ไม่ถูกต้อง: " + result.Error,
            result.Valid ? DialogKind.Info : DialogKind.Error);
    }

    private bool Confirm(string text, int count) =>
        UiDialog.Confirm(this, "ยืนยันคำสั่ง", $"{text}\nจะกระทบ {count} เครื่อง", "ยืนยัน", danger: true);

    // ---------- Scrollbar แบบบางสีเข้มสำหรับคอนเทนเนอร์ที่เลื่อนได้ ----------

    private static void ApplyDarkScrollbars(Control ctl)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SetWindowTheme(ctl.Handle, "DarkMode_Explorer", null);
        }
        catch { /* best-effort */ }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sidebarTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var t in _toasts.ToList()) t.Close();
        _discoveryCts.Cancel();
        foreach (var chat in _chats.Values) chat.ForceClose();
        foreach (var tile in _tiles) tile.Dispose();
        if (!_uiPreview)
        {
            _pki.Dispose();
            _key.Dispose();
        }
        base.OnFormClosed(e);
    }
}
