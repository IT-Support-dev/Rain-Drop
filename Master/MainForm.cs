using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// หน้าจอหลัก: Light Swiss/Minimal (Phase U4)
/// พื้นหลัง #FFFFFF ข้อความ #000000 แถบซ้าย #F5F1E8 เส้นแบ่ง #E5E7EB
/// แถบซ้ายไม่มีรายการเครื่องซ้ำกับพื้นที่หลัก เหลือ: โลโก้+ชื่อ, ค้นหา, ตัวกรอง, จำนวน, ตั้งค่า/ชุมชน
/// เปิดด้วย --ui-preview (เฉพาะ Debug หรือ env RAINDROP_UI_PREVIEW=1) จะแสดงข้อมูลจำลอง 6 เครื่อง
/// โดยไม่เชื่อมต่อเครือข่าย ไม่เขียนตั้งค่า
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MasterSettings _settings;
    private readonly MasterKey _key;
    private readonly MasterPki _pki;
    private readonly PairingCodeStore _codeStore = new();
    private CsrListener? _enrollmentListener;
    private readonly FlowLayoutPanel _panel;                 // ตารางการ์ดเครื่อง (เนื้อหาจริง)
    private readonly Panel _scrollHost;                      // แผงครอบที่ทำหน้าที่ scroll ให้ _panel
    private readonly Label _status = new();
    private readonly Label _count = new() { AutoSize = true, Font = Theme.FontCaption, ForeColor = Theme.TextSecondary };
    private readonly System.Windows.Forms.Timer _sidebarTimer = new() { Interval = 1500 };
    private readonly List<ITile> _tiles = new();
    private readonly Dictionary<ITile, ChatWindow> _chats = new();
    private readonly string _myName = $"ผู้ดูแลระบบ ({Environment.UserName})";
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Load(), Text = "Rain Drop Master", Visible = true };
    private readonly List<AlertMsg> _alertLog = new();
    private readonly List<ToastForm> _toasts = new();
    private readonly bool _uiPreview;

    public MainForm(string[] args)
    {
        bool envPreview = string.Equals(Environment.GetEnvironmentVariable("RAINDROP_UI_PREVIEW"), "1", StringComparison.OrdinalIgnoreCase);
#if DEBUG
        _uiPreview = args.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase) || envPreview;
#else
        _uiPreview = envPreview;
#endif

        Text = "Rain Drop Master";
        Icon = AppIcon.Load();
        Theme.ConfigureForm(this, new Size(900, 600));
        UiWindowPlacement.Attach(this, "master", new Size(1400, 860), new Size(900, 600));

        // แผงครอบ scroll: FlowLayoutPanel+AutoScroll มีบั๊กคำนวณ extent แนวนอนจากการเรียงแถวเดียว
        // (คิดว่าเนื้อหากว้าง ~2,300px ทั้งที่ wrap แล้วกว้าง ~760px) จึงโชว์ scrollbar แนวนอนค้างใต้การ์ด
        // แก้โดยให้ Panel ธรรมดาเป็นผู้เลื่อน แล้วให้ UiFlowPanel AutoSize ตามเนื้อหาจริง (Dock=Top)
        _scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.ZoneContent };
        ApplyDarkScrollbars(_scrollHost);
        _panel = new UiFlowPanel { Dock = DockStyle.Top, AutoSize = true, AutoScroll = false, BackColor = Theme.ZoneContent };
        _scrollHost.Controls.Add(_panel);
        _panel.Padding = new Padding(Theme.Space3);

        if (_uiPreview)
        {
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

        var sidebar = BuildSidebar();
        sidebar.TabIndex = 0;

        var content = BuildContent();
        content.TabIndex = 1;

        // ต้องเพิ่ม content ก่อน sidebar — WinForms จัด Dock จาก control ที่ add ท้ายสุดก่อน
        // ถ้า add sidebar ก่อน content (Dock=Fill) จะยืดเต็มหน้าต่างแล้วถูก sidebar ทับด้านซ้าย 260px
        // ทำให้ปุ่มซ้ายสุดของ toolbar และข้อความแถบสถานะโดนซ่อนใต้แถบซ้าย
        Controls.Add(content);
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
            _panel.Controls.Add((Control)tile);
        }
    }

    // ===================== แถบซ้าย (Sidebar) - ไม่มีรายการเครื่องซ้ำ =====================

    private Control BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = Theme.SidebarWidth,
            BackColor = Theme.ZoneSidebar,
            Padding = new Padding(Theme.Space3),
        };

        // โลโก้ + ชื่อหัวโปรแกรม
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.ButtonHeightLarge,
            AutoSize = false,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        var logo = new PictureBox
        {
            Image = AppIcon.Load().ToBitmap(),
            Size = new Size(Theme.IconSizeLarge, Theme.IconSizeLarge),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        var title = new Label
        {
            Text = "Rain Drop",
            Font = Theme.FontDeviceName,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(Theme.Space2, 0, 0, 0),
            UseMnemonic = false,
        };
        header.Controls.Add(logo);
        header.Controls.Add(title);

        // ช่องค้นหา: เส้นล่างอย่างเดียว placeholder "ค้นหาเครื่อง" ไม่มีหัวข้อแยก
        var search = UiInput.SearchBox("ค้นหาเครื่อง...", text => { _query = text; ApplyFilter(); });
        search.Dock = DockStyle.Top;
        search.Height = Theme.InputHeight;
        Theme.SetToolTip(search, "ค้นหาชื่อเครื่องหรือ IP");

        // ตัวกรองสถานะ: ข้อความ tab "ทั้งหมด · ออนไลน์ · ออฟไลน์" ขีดเส้นใต้ 2px ตัวที่เลือก
        var filter = new UiSegmentedFilter(new[] { "ทั้งหมด", "ออนไลน์", "ออฟไลน์" }, 0);
        // ต้อง Dock=Top ไม่งั้นลอยที่ (0,0) ทับโลโก้ "Rain Drop" และช่องค้นหา
        filter.Dock = DockStyle.Top;
        filter.SelectionChanged += (_, i) => { _statusFilter = (MachineStatusFilter)i; ApplyFilter(); };

        _count.Dock = DockStyle.Top;
        _count.Padding = new Padding(0, Theme.Space2, 0, Theme.Space2);
        _count.ForeColor = Theme.TextSecondary;

        // ปุ่มเพิ่มเครื่องลูกใหม่ (C2b pairing code)
        var btnAddDevice = new UiButton("เพิ่มเครื่องลูกใหม่", ButtonKind.Primary, UiIcon.Plus)
        {
            Dock = DockStyle.Bottom,
            Height = Theme.ButtonHeightLarge,
            Margin = new Padding(0, Theme.Space2, 0, Theme.Space2),
        };
        btnAddDevice.Click += async (_, _) => await AddDeviceClicked();
        sidebar.Controls.Add(btnAddDevice);

        // ปุ่มท้ายแถบ: ตั้งค่า / ชุมชน เป็นข้อความธรรมดา ชิดซ้ายแนวเดียวกับทุกอย่าง
        var settings = new UiButton("ตั้งค่า", ButtonKind.Ghost, UiIcon.Settings)
        {
            Dock = DockStyle.Bottom,
            Height = Theme.ButtonHeight,
            Margin = new Padding(0, Theme.Space2, 0, 0),
        };
        settings.Click += (_, _) => ShowSettings();
        var community = new UiButton("ชุมชน", ButtonKind.Ghost, UiIcon.Chat)
        {
            Dock = DockStyle.Bottom,
            Height = Theme.ButtonHeight,
            Margin = new Padding(0, Theme.Space2, 0, 0),
        };
        community.Click += (_, _) => OpenCommunity();

        sidebar.Controls.Add(community);
        sidebar.Controls.Add(settings);
        sidebar.Controls.Add(_count);
        sidebar.Controls.Add(filter);
        sidebar.Controls.Add(search);
        sidebar.Controls.Add(header);

        // เส้นแบ่งขวาของแถบซ้าย 1px
        var divider = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.Divider };
        sidebar.Controls.Add(divider);
        divider.BringToFront();

        return sidebar;
    }

    // ===================== พื้นที่เนื้อหา (Content) =====================

    private Control BuildContent()
    {
        var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };

        // ---------- แถบเครื่องมือ: แถวเดียว 48px พื้นขาว เส้นล่าง 1px ----------
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.ToolbarHeight,
            Padding = new Padding(Theme.Space1),
            BackColor = Theme.ZoneHeader,
            WrapContents = false,
            AutoScroll = false,
        };

        void Tool(UiIcon icon, string text, string tip, Action act, ButtonKind kind = ButtonKind.Ghost)
        {
            var b = new UiButton(text, kind, icon)
            {
                Height = Theme.ButtonHeight,
                AutoSize = true,        // ให้ปุ่มขยายตามข้อความไทย ไม่ตัดข้อความ
                // ระยะรายปุ่มแบบ Space3 รวมทั้งแถวไม่พอ (11 ปุ่ม + 4 เส้นแบ่ง ≈ 1,590px > พื้นที่ ~1,119px)
                // จึงใช้ padding/margin 8/6px เพื่อให้ปุ่มแถวท้าย ("เพิ่มเติม"/"พลังงาน") แสดงครบไม่โดนตัดขอบขวา
                Padding = new Padding(Theme.Space1, 0, Theme.Space1, 0),
                Margin = new Padding(0, 0, 6, 0),
                UseMnemonic = false,
            };
            Theme.SetToolTip(b, tip);
            b.Click += (_, _) => act();
            toolbar.Controls.Add(b);
        }

        void Sep() => toolbar.Controls.Add(new Panel
        {
            Width = 1,
            Height = Theme.ButtonHeight,
            BackColor = Theme.Divider,
            Margin = new Padding(Theme.Space1),
        });

        Tool(UiIcon.Refresh, "ค้นหา", "ค้นหาเครื่องในเครือข่าย", () => _ = DiscoverAsync(userInitiated: true));
        Tool(UiIcon.Plus, "เพิ่ม", "เพิ่มเครื่องเองด้วย IP", AddHost);
        Sep();

        Tool(UiIcon.Monitor, "ควบคุม", "ควบคุมระยะไกลเครื่องที่เลือก", OpenRemoteForSelected);
        Tool(UiIcon.Chat, "แชท", "แชทกับเครื่องที่เลือก", OpenChatForSelected);
        Tool(UiIcon.Message, "ข้อความ", "ส่งข้อความไปยังเครื่องที่เลือก", SendMessageToTargets);
        Sep();

        Tool(UiIcon.Lock, "ล็อก", "ล็อกหน้าจอเครื่องที่เลือก", LockTargets);
        Tool(UiIcon.Unlock, "ปลดล็อก", "ปลดล็อกเครื่องที่เลือก", () => Broadcast(c => c.Send(MsgType.Unlock)));
        Sep();

        Tool(UiIcon.Activity, "กิจกรรม", "ดูกิจกรรมเครื่องที่เลือก", OpenActivityForSelected);
        Tool(UiIcon.Block, "บล็อก", "ตั้งค่าบล็อกโปรแกรม/เว็บ", OpenBlockSettings);
        Tool(UiIcon.More, "เพิ่มเติม", "เมนูเพิ่มเติม", ShowMoreMenu);
        Sep();

        // พลังงาน: outline สีดำ ไม่ใช่บล็อกสีแดง/ชมพู มีไอคอนและข้อความ
        var shutdown = new UiButton("พลังงาน", ButtonKind.Outline, UiIcon.Power)
        {
            Height = Theme.ButtonHeight,
            AutoSize = true,
            Padding = new Padding(Theme.Space1, 0, Theme.Space1, 0),
            Margin = new Padding(0, 0, 6, 0),
            UseMnemonic = false,
        };
        Theme.SetToolTip(shutdown, "ปิดเครื่อง / รีสตาร์ท / ออกจากระบบ");
        shutdown.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var menu = new ContextMenuStrip();
            Theme.StyleMenu(menu);
            menu.Items.Add("ปิดเครื่อง", null, (_, _) => Power(PowerAction.Shutdown, "ปิดเครื่อง"));
            menu.Items.Add("รีสตาร์ท", null, (_, _) => Power(PowerAction.Restart, "รีสตาร์ท"));
            menu.Items.Add("ออกจากระบบ", null, (_, _) => Power(PowerAction.Logoff, "ออกจากระบบ"));
            menu.Closed += (_, _) => menu.Dispose();
            menu.Show(shutdown, new Point(0, shutdown.Height));
        };
        toolbar.Controls.Add(shutdown);

        // ---------- พื้นที่แสดงการ์ดเครื่อง ----------
        _panel.Padding = new Padding(Theme.Space3);

        // ---------- Empty state: หัวเรื่อง Light ขนาดใหญ่ จัดกึ่งกลางพื้นที่แสดงผล ----------
        _empty = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };
        // เดิมเป็น Panel ธรรมดา ทำให้หัวเรื่อง/คำอธิบาย/ปุ่มวางซ้อนกันที่ (0,0) เดียวกัน
        // ใช้ FlowLayoutPanel เรียงลงล่างแทน เพื่อให้แต่ละชิ้นอยู่แถวของตัวเอง
        var emptyContainer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };
        var emptyHeading = new Label
        {
            Text = "ยังไม่มีเครื่อง",
            Font = Theme.FontHeading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, Theme.Space2),
            UseMnemonic = false,
        };
        var emptyDesc = new Label
        {
            Text = "กด \"ค้นหา\" เพื่อหาเครื่องในเครือข่าย หรือกด \"เพิ่ม\" เพื่อเพิ่มเครื่องด้วย IP",
            Font = Theme.FontBody,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            MaximumSize = new Size(Theme.SidebarWidth * 3, 0),
            Margin = new Padding(0, 0, 0, Theme.Space4),
            UseMnemonic = false,
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
            UseMnemonic = false,
        };
        btnSearch.Click += (_, _) => _ = DiscoverAsync(userInitiated: true);
        var btnAdd = new UiButton("เพิ่มด้วย IP", ButtonKind.Outline, UiIcon.Plus)
        {
            Height = Theme.ButtonHeightLarge,
            Width = Theme.ButtonWidthWide,
            UseMnemonic = false,
        };
        btnAdd.Click += (_, _) => AddHost();
        emptyButtons.Controls.Add(btnSearch);
        emptyButtons.Controls.Add(btnAdd);

        emptyContainer.Controls.Add(emptyHeading);
        emptyContainer.Controls.Add(emptyDesc);
        emptyContainer.Controls.Add(emptyButtons);

        var emptyCenter = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        emptyCenter.Controls.Add(emptyContainer);
        void CenterEmptyContainer()
        {
            emptyContainer.Location = new Point(
                Math.Max(Theme.Space3, (emptyCenter.Width - emptyContainer.Width) / 2),
                Math.Max(Theme.Space3, (emptyCenter.Height - emptyContainer.Height) / 2));
        }
        emptyCenter.Resize += (_, _) => CenterEmptyContainer();
        emptyContainer.SizeChanged += (_, _) => CenterEmptyContainer();
        _empty.Controls.Add(emptyCenter);

        // ---------- แถบสถานะ: ข้อความ 9.5pt สีรอง เส้นบน 1px ----------
        var statusDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };
        _status.Dock = DockStyle.Bottom;
        _status.Height = Theme.StatusHeight;
        _status.BackColor = Theme.ZoneContent;
        _status.ForeColor = Theme.TextSecondary;
        _status.Font = Theme.FontCaption;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(Theme.Space3, 0, 0, 0);
        _status.Text = "เลือกเครื่องเพื่อสั่งงานรายเครื่อง ถ้าไม่เลือกจะส่งไปทุกเครื่อง";
        _status.UseMnemonic = false;

        center.Controls.Add(statusDivider);
        center.Controls.Add(_scrollHost);
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

    // ===================== ตัวนับและตัวกรอง =====================

    private string _query = "";
    private MachineStatusFilter _statusFilter = MachineStatusFilter.All;
    private Panel _empty = new() { Dock = DockStyle.Fill, BackColor = Theme.ZoneContent };

    private void RefreshSidebars()
    {
        _count.Text = UiFilter.CountText(_tiles.Count(t => t.Visible), _tiles.Count);
    }

    private void ApplyFilter()
    {
        foreach (var tile in _tiles)
            tile.Visible = UiFilter.Matches(_query, _statusFilter, tile.IsOnline, tile.Host.Name, tile.Host.Address);
        _empty.Visible = !_tiles.Any(t => t.Visible);
        if (_empty.Visible) _empty.BringToFront();
        RefreshSidebars();
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
        _panel.Controls.Add((Control)tile);
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
            "กุญแจลับ CA/Master เข้ารหัส DPAPI CurrentUser ในโฟลเดอร์นี้ ห้ามลบหรือย้าย\n\n" +
            "เปิดโฟลเดอร์เลยไหม?",
            "เปิดโฟลเดอร์");
        if (answer)
            Process.Start("explorer.exe", $"/select,\"{MasterPki.CaPublicPath}\"");
    }

    // ---------- C2b: เพิ่มเครื่องลูกใหม่ด้วย pairing code ----------

    private async System.Threading.Tasks.Task AddDeviceClicked()
    {
        if (_uiPreview)
        {
            UiDialog.Show(this, "โหมดตัวอย่าง", "ไม่สามารถสร้าง pairing code ในโหมดตัวอย่างได้");
            return;
        }

        // สร้างรหัสใหม่
        string code;
        try { code = _codeStore.CreateCode(); }
        catch (InvalidOperationException ex)
        {
            UiDialog.Show(this, "ไม่สามารถสร้างรหัส", ex.Message, DialogKind.Warning);
            return;
        }
        _pki.Audit.Append(new AuditEvent("enroll", "-", "create-code", code.Length.ToString()));

        // เริ่ม enrollment listener ถ้ายังไม่รัน
        EnsureEnrollmentListener();

        // แสดงรหัสให้ผู้ดูแล (ไม่ log รหัสไว้ในที่ใด)
        UiDialog.Show(this, "รหัสจับคู่ (pairing code)",
            $"รหัส: {code}\n" +
            "รหัสใช้ได้ครั้งเดียว หมดอายุใน 10 นาที\n" +
            "นำรหัสนี้ไปใส่ในตัวติดตั้ง Agent ของเครื่องลูก\n\n" +
            "ห้ามบอกรหัสแก่บุคคลอื่น และห้ามบันทึกรหัสไว้ในเอกสารใดๆ");


        // นับถอยหลังหมดอายุ เตือนเมื่อเหลือ 1 นาที
        _ = System.Threading.Tasks.Task.Delay(9 * 60 * 1000).ContinueWith(_ =>
        {
            if (_codeStore.ActiveCount > 0)
                BeginInvoke(new Action(() => ShowToast("หมดอายุเร็วๆ นี้", "pairing code จะหมดอายุภายใน 1 นาที", "warning")));
        });

        // ตรวจว่าหมดรหัสทั้งหมดเมื่อใด จะปิด listener
        _ = System.Threading.Tasks.Task.Delay(10 * 60 * 1000).ContinueWith(_ =>
        {
            if (_codeStore.ActiveCount == 0 && _enrollmentListener is not null)
            {
                BeginInvoke(new Action(() =>
                {
                    _enrollmentListener.Stop();
                    _enrollmentListener = null;
                }));
            }
        });
    }

    private void EnsureEnrollmentListener()
    {
        if (_enrollmentListener is null)
        {
            _enrollmentListener = new CsrListener(_codeStore, _pki, _pki.Audit,
                System.Net.IPAddress.Any, RepoCertificates.EnrollmentPort);
            _enrollmentListener.Start();
        }
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
        _enrollmentListener?.Stop();
        _codeStore.Dispose();
        base.OnFormClosed(e);
    }
}
