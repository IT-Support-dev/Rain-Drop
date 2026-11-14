using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // โหมดใช้ตอนติดตั้ง: เขียน agent.json พร้อมรหัสผ่านที่ hash แล้ว
        //   RainDrop.Agent.exe --init-config "<โฟลเดอร์>" "<รหัสผ่าน>" <พอร์ต>
        if (args.Length >= 3 && args[0] == "--init-config")
        {
            int port = args.Length >= 4 && int.TryParse(args[3], out int p) ? p : Protocol.DefaultPort;
            string hub = args.Length >= 5 ? args[4] : "";
            return AgentConfig.WriteInitial(args[1], args[2], port, hub) ? 0 : 1;
        }

        // ให้รันได้แค่ 1 ตัวต่อ session ของผู้ใช้ (ถ้า Service ปลุกซ้ำจะไม่เปิดซ้อน)
        using var mutex = new Mutex(true, @"Local\RainDropAgent", out bool createdNew);
        if (!createdNew) return 0;

        // ต้องเป็น PerMonitorV2 เพื่อให้จับภาพหน้าจอและพิกัดเมาส์ได้ถูกต้องเมื่อตั้ง Scale 125%/150%
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new AgentContext(AgentConfig.Load()));
        return 0;
    }
}

internal sealed class AgentConfig
{
    public int Port { get; set; } = Protocol.DefaultPort;

    /// <summary>รหัสผ่านสำหรับ "ออกจากโปรแกรม" เก็บเป็น PBKDF2 (ไม่เก็บรหัสจริง)</summary>
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }

    /// <summary>ที่อยู่เซิร์ฟเวอร์กลาง (Hub) สำหรับห้องชุมชน ตั้งค่าตอนติดตั้งด้วย /HUB=</summary>
    public string HubAddress { get; set; } = "";

    public static string Folder => AppContext.BaseDirectory;
    public static string ConfigPath => Path.Combine(Folder, "agent.json");
    public static string PublicKeyPath => Path.Combine(Folder, "agent-public.pem");

    /// <summary>โฟลเดอร์ที่ทั้ง Service (SYSTEM) และ Agent (ผู้ใช้) เขียนได้ ใช้ส่งสัญญาณกัน</summary>
    public static string SharedFolder
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RainDrop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>ไฟล์สัญญาณให้ Service หยุดปลุก Agent (ใส่ token ที่ Service ส่งมาให้)</summary>
    public static string StopSignalPath => Path.Combine(SharedFolder, "agent-stop.signal");

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath)) ?? new AgentConfig();
        }
        catch { /* ใช้ค่าเริ่มต้น */ }
        return new AgentConfig();
    }

    /// <summary>โหลด public key ของ Master ถ้าไม่มีไฟล์จะคืน null และจะไม่มีใครเชื่อมต่อได้</summary>
    public static ECDsa? LoadPublicKey()
    {
        if (!File.Exists(PublicKeyPath)) return null;
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(PublicKeyPath));
        return key;
    }

    // ---------- รหัสผ่าน ----------

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    public bool VerifyPassword(string input)
    {
        if (!HasPassword) return true; // ไม่ได้ตั้งรหัส = ให้ออกได้เลย
        try
        {
            byte[] salt = Convert.FromBase64String(PasswordSalt!);
            byte[] expected = Convert.FromBase64String(PasswordHash!);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(input, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static bool WriteInitial(string folder, string password, int port, string hubAddress = "")
    {
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            var config = new AgentConfig
            {
                Port = port,
                PasswordSalt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                HubAddress = hubAddress,
            };
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "agent.json"),
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// ที่อยู่เซิร์ฟเวอร์ชุมชนที่ผู้ใช้ตั้งเองได้ เก็บในโฟลเดอร์ที่ผู้ใช้เขียนได้ (ProgramData\RainDrop)
/// ถ้ายังไม่เคยตั้ง จะใช้ค่าเริ่มต้นที่ผู้ดูแลกำหนดตอนติดตั้ง (จาก agent.json)
/// </summary>
internal sealed class CommunitySettings
{
    public string HubAddress { get; set; } = "";
    public string DisplayName { get; set; } = "";

    private static string PathOnDisk => Path.Combine(AgentConfig.SharedFolder, "community.json");

    public static CommunitySettings Load(string defaultHub)
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<CommunitySettings>(File.ReadAllText(PathOnDisk)) ?? new CommunitySettings { HubAddress = defaultHub };
        }
        catch { /* ใช้ค่าเริ่มต้น */ }
        return new CommunitySettings { HubAddress = defaultHub };
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* เขียนไม่ได้ */ }
    }
}

/// <summary>
/// ตัวแอปหลักของ Agent: ไม่มีหน้าต่าง มีแค่ไอคอนใน System Tray
/// งานที่ต้องแตะ UI (ล็อกจอ, กล่องข้อความ) ถูกส่งกลับมาทำบน UI thread ผ่าน RunOnUi
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly Control _ui;
    private readonly NotifyIcon _tray;
    private readonly AgentServer _server;
    private LockForm? _lockForm;
    private ChatWindow? _chat;
    private CommunityForm? _community;
    private readonly ActivityTracker _activity = new();
    private readonly BlockEnforcer _block = new();
    private readonly AgentConfig _config;
    private readonly string? _communityFingerprint;
    private readonly string _myName = $"{Environment.UserName} @ {Environment.MachineName}";

    public AgentContext(AgentConfig config)
    {
        _config = config;
        _ui = new Control();
        _ui.CreateControl();

        // ไอคอนหยดน้ำ ให้ผู้ใช้รู้ว่าเครื่องนี้อยู่ภายใต้การดูแล (โปร่งใสต่อผู้ใช้)
        _tray = new NotifyIcon
        {
            Icon = AppIcon.LoadTray(),
            Text = "Rain Drop Agent - เครื่องนี้อยู่ภายใต้การดูแลของผู้ดูแลระบบ",
            Visible = true,
        };

        // ผู้ใช้เครื่องลูกเริ่มแชทหาผู้ดูแลได้เอง: ดับเบิลคลิกไอคอน หรือคลิกขวาแล้วเลือกเมนู
        var menu = new ContextMenuStrip();
        menu.Items.Add("แชทกับผู้ดูแลระบบ", null, (_, _) => OpenChat());
        menu.Items.Add("เปิดห้องชุมชน (แชท/คอลเสียง)", null, (_, _) => OpenCommunity());
        menu.Items.Add("สถานะการเชื่อมต่อ...", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ออกจากโปรแกรม (ต้องใช้รหัสผู้ดูแล)", null, (_, _) => RequestExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenChat();

        var publicKey = AgentConfig.LoadPublicKey();
        if (publicKey is null)
        {
            _tray.ShowBalloonTip(10000, "Rain Drop Agent",
                "ไม่พบไฟล์ agent-public.pem จึงยังไม่มี Master ใดเชื่อมต่อได้", ToolTipIcon.Warning);
        }
        _communityFingerprint = publicKey is null ? null : Protocol.Fingerprint(publicKey);

        _server = new AgentServer(config.Port, publicKey, this);

        // ส่งแจ้งเตือน (USB/ไฟล์/บล็อกโปรแกรม) ไปยัง Master ที่เชื่อมต่ออยู่
        _activity.Alert += a => _server.BroadcastAlert(a);
        _block.Alert += a => _server.BroadcastAlert(a);

        Task.Run(_server.RunAsync);
    }

    public void ReportError(string message) => RunOnUi(() =>
        _tray.ShowBalloonTip(15000, "Rain Drop Agent - เกิดปัญหา", message, ToolTipIcon.Error));

    /// <summary>สรุปกิจกรรมปัจจุบันของเครื่องนี้ (ให้ AgentServer ส่งต่อให้ Master)</summary>
    public ActivitySnapshot GetActivity() => _activity.Snapshot();

    /// <summary>ตั้งรายการบล็อกโปรแกรม/เว็บ (Master สั่งมา)</summary>
    public void SetBlocklist(BlockRules rules) => _block.SetRules(rules);

    /// <summary>เปิดห้องชุมชนแบบ Discord (แชทกลุ่ม/คอลเสียง/แชร์ไฟล์)</summary>
    private void OpenCommunity()
    {
        if (_community is { IsDisposed: false })
        {
            _community.WindowState = FormWindowState.Normal;
            _community.Activate();
            return;
        }
        if (_communityFingerprint is null)
        {
            MessageBox.Show("ยังไม่ได้ติดตั้งกุญแจของชุมชน (agent-public.pem) จึงเข้าห้องชุมชนไม่ได้",
                "Rain Drop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cs = CommunitySettings.Load(_config.HubAddress);
        if (string.IsNullOrWhiteSpace(cs.HubAddress))
        {
            var values = Prompt.Show(null, "เข้าห้องชุมชน Rain Drop",
                ("ที่อยู่เซิร์ฟเวอร์ (ถามผู้ดูแลระบบ):", "192.168.1."),
                ("ชื่อที่จะแสดง:", _myName));
            if (values is null || values[0].Length == 0) return;
            cs.HubAddress = values[0];
            cs.DisplayName = values[1];
            cs.Save();
        }

        string name = string.IsNullOrWhiteSpace(cs.DisplayName) ? _myName : cs.DisplayName;
        int idx = cs.HubAddress.LastIndexOf(':');
        string host = cs.HubAddress; int port = HubProtocol.DefaultPort;
        if (idx > 0 && int.TryParse(cs.HubAddress[(idx + 1)..], out int p)) { host = cs.HubAddress[..idx]; port = p; }

        var hub = new HubClient(host, port, name, MemberRole.Client, _communityFingerprint);
        _community = new CommunityForm(hub, name) { ShowInTaskbar = true };
        _community.FormClosed += (_, _) => { hub.Dispose(); _community = null; };
        _community.Show();
    }

    /// <summary>
    /// ออกจากโปรแกรม ต้องใส่รหัสของผู้ดูแลให้ถูกก่อน
    /// เมื่อถูกต้องจะเขียน "token หยุด" ที่ Service ส่งมาให้ลงไฟล์สัญญาณ
    /// Service เห็น token ตรงกันจึงจะไม่ปลุก Agent ขึ้นมาใหม่
    /// (ถ้าปิดด้วยวิธีอื่น เช่น End Task จะไม่มี token ตัวนี้ Service จึงปลุกกลับทันที)
    /// </summary>
    private void RequestExit()
    {
        if (_config.HasPassword)
        {
            var values = Prompt.Show(null, "ออกจากโปรแกรม Rain Drop Agent",
                ("ใส่รหัสผ่านของผู้ดูแลระบบ:", ""));
            if (values is null) return;
            if (!_config.VerifyPassword(values[0]))
            {
                MessageBox.Show("รหัสผ่านไม่ถูกต้อง", "Rain Drop Agent",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        else if (MessageBox.Show("ยังไม่ได้ตั้งรหัสผ่าน ต้องการออกจากโปรแกรมหรือไม่?",
                     "Rain Drop Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        // บอก Service ว่าให้หยุดปลุก: เขียน token ที่ได้รับตอนถูกเปิด
        string token = Environment.GetEnvironmentVariable("RAINDROP_STOP_TOKEN") ?? "";
        try { File.WriteAllText(AgentConfig.StopSignalPath, token); } catch { /* ไม่เป็นไร */ }

        _tray.Visible = false;
        ExitThread();
    }

    /// <summary>หน้าต่างข้อมูลสำหรับตรวจสอบปัญหาการเชื่อมต่อ</summary>
    private void ShowStatus()
    {
        var ips = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToList();
        var masters = _server.ConnectedMasters;

        string text =
            $"ชื่อเครื่อง:  {Environment.MachineName}\n" +
            $"IP ของเครื่องนี้:  {(ips.Count > 0 ? string.Join(", ", ips) : "ไม่พบ (ไม่ได้ต่อเครือข่าย)")}\n" +
            $"พอร์ต:  {_server.Port}  " + (_server.StartError is null ? "(รอการเชื่อมต่ออยู่)" : "\n   ✖ " + _server.StartError) + "\n\n" +
            (_server.HasPublicKey
                ? $"กุญแจของ Master:  ✔ พบ\nลายนิ้วมือกุญแจ:  {_server.KeyFingerprint}\n(ต้องตรงกับที่แสดงใน Master → ตั้งค่า / กุญแจ)\n\n"
                : $"กุญแจของ Master:  ✖ ไม่พบไฟล์\n{AgentConfig.PublicKeyPath}\n\n") +
            $"Master ที่เชื่อมต่ออยู่:  {(masters.Count > 0 ? string.Join(", ", masters) : "ยังไม่มี")}";

        MessageBox.Show(text, "Rain Drop Agent - สถานะการเชื่อมต่อ", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void RunOnUi(Action action)
    {
        if (_ui.IsHandleCreated) _ui.BeginInvoke(action);
    }

    public void Lock(string text) => RunOnUi(() =>
    {
        if (_lockForm is null || _lockForm.IsDisposed)
        {
            _lockForm = new LockForm(text);
            _lockForm.Show();
        }
        else
        {
            _lockForm.SetText(text);
        }
    });

    public void Unlock() => RunOnUi(() =>
    {
        _lockForm?.ForceClose();
        _lockForm = null;
    });

    private ChatWindow EnsureChat()
    {
        if (_chat is null || _chat.IsDisposed)
        {
            _chat = new ChatWindow("แชทกับผู้ดูแลระบบ", _myName,
                text => _server.BroadcastChat(new ChatMsg(_myName, text, DateTime.Now)) > 0,
                voice => _server.BroadcastVoice(voice) > 0)
            {
                TopMost = true,
                ShowInTaskbar = true,
            };
        }
        return _chat;
    }

    private void OpenChat()
    {
        var chat = EnsureChat();
        chat.ShowAndNotify(activate: true);
        if (!_server.HasMaster)
            chat.AppendSystem("ขณะนี้ยังไม่มีผู้ดูแลระบบเชื่อมต่ออยู่ จึงส่งข้อความไม่ได้");
    }

    /// <summary>มีข้อความจาก Master: เด้งหน้าต่างแชทขึ้นมาข้างหน้าเพื่อให้ผู้ใช้เห็นทันที</summary>
    public void ReceiveChat(ChatMsg msg) => RunOnUi(() =>
    {
        var chat = EnsureChat();
        chat.Append(msg, mine: false);
        chat.ShowAndNotify(activate: true);
    });

    /// <summary>มีข้อความเสียงจาก Master: เด้งหน้าต่างแชทขึ้นมา ผู้ใช้กด ▶ เพื่อฟัง</summary>
    public void ReceiveVoice(VoiceMsg msg) => RunOnUi(() =>
    {
        var chat = EnsureChat();
        chat.AppendVoice(msg, mine: false);
        chat.ShowAndNotify(activate: true);
    });

    public void ShowMessage(string title, string text) => RunOnUi(() =>
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Stop();
            _activity.Dispose();
            _block.Dispose();
            _chat?.ForceClose();
            _community?.Close();
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
