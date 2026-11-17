using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Agent;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        // C2b enrollment: pairing code อ่านจาก stdin (ไม่ส่งผ่าน command line กันหลุดลง log)
        //   RainDrop.Agent.exe --enroll-pairing <folder> <master-host:port>
        if (args.Length >= 3 && args[0] == "--enroll-pairing")
        {
            if (await EnrollmentClient.Run(args[1], args[2], out string enrollError)) return 0;
            Console.Error.WriteLine("enroll-pairing ไม่สำเร็จ: " + enrollError);
            return 1;
        }

        // โหมดใช้ตอนติดตั้ง (สิทธิ์ผู้ดูแล): เขียน agent.json (ไม่มีรหัส) + private\credentials.json (PBKDF2 hash)
        //   RainDrop.Agent.exe --init-config "<โฟลเดอร์>" "<รหัสผ่าน>" <พอร์ต> [<Hub>]
        if (args.Length >= 3 && args[0] == "--init-config")
        {
            int port = args.Length >= 4 && int.TryParse(args[3], out int p) ? p : Protocol.DefaultPort;
            string hub = args.Length >= 5 ? args[4] : "";
            if (AgentConfig.WriteInitial(args[1], args[2], port, hub, out string initError)) return 0;
            Console.Error.WriteLine("ตั้งรหัสผ่านไม่สำเร็จ: " + initError);
            return 1;
        }

        // C2a enrollment: ถอด PKCS#12 ด้วยรหัส แล้วเก็บ key/cert ใน private ที่ Users เข้าไม่ได้
        if (args.Length >= 4 && args[0] == "--enroll")
        {
            int port = args.Length >= 6 && int.TryParse(args[5], out int ep) ? ep : Protocol.DefaultPort;
            string hub = args.Length >= 7 ? args[6] : "";
            if (args.Length < 5) { Console.Error.WriteLine("usage: --enroll <folder> <bundle> <bundle-password> <admin-password> [port] [hub]"); return 1; }
            if (AgentConfig.Enroll(args[1], args[2], args[3], args[4], port, hub, out string enrollError)) return 0;
            Console.Error.WriteLine("enroll ไม่สำเร็จ: " + enrollError);
            return 1;
        }
        if (args.Length == 1 && args[0] == "--verify-audit")
        {
            var result = ServiceClient.VerifyAudit(out string? auditError);
            if (result is null) { Console.Error.WriteLine(auditError); return 1; }
            Console.WriteLine(result.Ok ? "audit valid" : "audit invalid: " + result.AuditError);
            return result.Ok ? 0 : 1;
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

    /// <summary>ที่อยู่เซิร์ฟเวอร์กลาง (Hub) สำหรับห้องชุมชน ตั้งค่าตอนติดตั้งด้วย /HUB=</summary>
    public string HubAddress { get; set; } = "";

    /// <summary>
    /// โฟลเดอร์ค่าตั้งกลางที่ผู้ใช้ทั่วไป "เขียนไม่ได้" (Service เป็นคนตั้ง ACL ตอนเริ่มทำงาน)
    /// เดิมอยู่ในโฟลเดอร์โปรแกรมและใช้ส่งสัญญาณหยุดซึ่งผู้ใช้ปลอมได้ จึงย้ายมาที่นี่
    /// </summary>
    public static string SharedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RainDrop");

    public static string ConfigPath => Path.Combine(SharedFolder, "agent.json");

    /// <summary>กุญแจสาธารณะอยู่ข้างไฟล์โปรแกรม (Program Files) ซึ่งผู้ใช้ทั่วไปเขียนทับไม่ได้</summary>
    public static string PublicKeyPath => Path.Combine(SharedFolder, "challenge-public.pem");
    public static string LegacyPublicKeyPath => Path.Combine(AppContext.BaseDirectory, "agent-public.pem");

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath)) ?? new AgentConfig();
        }
        catch (Exception ex)
        {
            // อ่าน agent.json ไม่ได้ = พอร์ต/ที่อยู่ Hub ที่ตั้งตอนติดตั้งหายไป
            // ยังต้องเปิดโปรแกรมต่อได้ แต่ต้องมีร่องรอยให้ตรวจสอบว่าทำไมค่าตั้งไม่ตรง
            Diag.Error("AgentConfig.Load", $"อ่านค่าตั้งที่ '{ConfigPath}' ไม่สำเร็จ จึงใช้ค่าเริ่มต้น", ex);
        }
        return new AgentConfig();
    }

    /// <summary>โหลด public key ของ Master ถ้าไม่มีไฟล์จะคืน null และจะไม่มีใครเชื่อมต่อได้</summary>
    public static ECDsa? LoadPublicKey()
    {
        string path = File.Exists(PublicKeyPath) ? PublicKeyPath : LegacyPublicKeyPath;
        if (!File.Exists(path)) return null;
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path));
        return key;
    }

    // ---------- เขียนค่าตั้งต้นตอนติดตั้ง (ตัวติดตั้งรันด้วยสิทธิ์ผู้ดูแล) ----------

    /// <summary>รูปไฟล์ที่ Service อ่าน (อยู่ใน private\credentials.json ซึ่ง Users ไม่มีสิทธิ์อ่าน)</summary>
    internal sealed class StoredCredentials
    {
        public string? PasswordSalt { get; set; }
        public string? PasswordHash { get; set; }
    }

    /// <summary>
    /// เขียน agent.json (ไม่มีรหัสผ่านอีกต่อไป) และ private\credentials.json (PBKDF2 hash)
    /// ปฏิเสธรหัสผ่านว่างหรือสั้นกว่า 8 ตัวอักษร — ไม่มีรหัสเริ่มต้น "raindrop" อีกแล้ว
    /// การตั้ง ACL ปิดสิทธิ์เขียนของ Users ทำโดยตัวติดตั้ง (icacls) และ Service ย้ำอีกครั้งตอนเริ่ม
    /// </summary>
    public static bool WriteInitial(string folder, string password, int port, string hubAddress, out string error)
    {
        error = "";
        if (!PasswordHashing.IsAcceptable(password))
        {
            error = $"รหัสผ่านต้องยาวอย่างน้อย {PasswordHashing.MinPasswordLength} ตัวอักษร " +
                    "ห้ามว่าง และห้ามมีอักขระควบคุม";
            return false;
        }

        try
        {
            byte[] salt = PasswordHashing.NewSalt();
            byte[] hash = PasswordHashing.Hash(password, salt);
            var options = new JsonSerializerOptions { WriteIndented = true };

            Directory.CreateDirectory(folder);
            string privateDir = Path.Combine(folder, "private");
            Directory.CreateDirectory(privateDir);

            // ตั้ง ACL ทันทีที่สร้างโฟลเดอร์ (รันด้วยสิทธิ์ผู้ดูแลตอนติดตั้ง) กันช่วงเวลาที่ไฟล์ hash ยังอ่านได้
            // Service จะย้ำ ACL ซ้ำอีกครั้งตอนเริ่มทำงาน (ServiceStore.EnsureLayout)
            HardenFolder(folder, usersRead: true);
            HardenFolder(privateDir, usersRead: false);

            File.WriteAllText(Path.Combine(folder, "agent.json"),
                JsonSerializer.Serialize(new AgentConfig { Port = port, HubAddress = hubAddress }, options));

            File.WriteAllText(Path.Combine(privateDir, "credentials.json"),
                JsonSerializer.Serialize(new StoredCredentials
                {
                    PasswordSalt = Convert.ToBase64String(salt),
                    PasswordHash = Convert.ToBase64String(hash),
                }, options));

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Diag.Error("AgentConfig.WriteInitial", $"เขียนค่าตั้งต้นที่ '{folder}' ไม่สำเร็จ", ex);
            return false;
        }
    }

    /// <summary>C2a enrollment: validate the password-protected bundle before writing any endpoint secret.</summary>
    public static bool Enroll(string folder, string bundlePath, string bundlePassword, string adminPassword,
        int port, string hubAddress, out string error)
    {
        error = "";
        if (!PasswordHashing.IsAcceptable(adminPassword)) { error = "admin password rejected"; return false; }
        try
        {
            var (manifest, pfx) = EnrollmentFile.Decode(File.ReadAllBytes(bundlePath));
            var (agent, ca) = RepoCertificates.ReadEnrollment(pfx, bundlePassword);
            using (agent) using (ca)
            {
                if (!agent.Subject.EndsWith(manifest.MachineId, StringComparison.Ordinal))
                    throw new InvalidDataException("Bundle machine identity mismatch");
                using var challenge = ECDsa.Create();
                challenge.ImportFromPem(manifest.ChallengePublicKeyPem);
                if (Protocol.Fingerprint(challenge) != manifest.ChallengeFingerprint)
                    throw new InvalidDataException("Bundle challenge fingerprint mismatch");
                var policy = new ControlTlsPolicy(ca, new RevocationState(), ControlRole.AgentServer);
                if (!policy.Validate(agent, null, System.Net.Security.SslPolicyErrors.None, out string reason))
                    throw new InvalidDataException("Bundle certificate rejected: " + reason);

                Directory.CreateDirectory(folder);
                string privateDir = Path.Combine(folder, "private");
                Directory.CreateDirectory(privateDir);
                Directory.CreateDirectory(Path.Combine(privateDir, "audit"));
                HardenFolder(folder, usersRead: true);
                HardenFolder(privateDir, usersRead: false);
                HardenFolder(Path.Combine(privateDir, "audit"), usersRead: false);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(Path.Combine(folder, "agent.json"), JsonSerializer.Serialize(new AgentConfig { Port = port, HubAddress = hubAddress }, options));
                File.WriteAllText(Path.Combine(folder, "challenge-public.pem"), manifest.ChallengePublicKeyPem);
                byte[] salt = PasswordHashing.NewSalt();
                File.WriteAllText(Path.Combine(privateDir, "credentials.json"), JsonSerializer.Serialize(new StoredCredentials
                {
                    PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(PasswordHashing.Hash(adminPassword, salt)),
                }, options));
                File.WriteAllBytes(Path.Combine(privateDir, "agent-cert.der"), agent.RawData);
                File.WriteAllBytes(Path.Combine(privateDir, "agent-key.pkcs8"), agent.GetECDsaPrivateKey()!.ExportPkcs8PrivateKey());
                File.WriteAllBytes(Path.Combine(privateDir, "ca-cert.der"), ca.RawData);
                if (!File.Exists(Path.Combine(privateDir, "revocation.json")))
                    File.WriteAllText(Path.Combine(privateDir, "revocation.json"), JsonSerializer.Serialize(new RevocationList(1, new List<string>())));
                if (!File.Exists(Path.Combine(privateDir, "run-allowlist.json")))
                    File.WriteAllText(Path.Combine(privateDir, "run-allowlist.json"), JsonSerializer.Serialize(new RunAllowlist(new List<string>()), options));
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            Diag.Error("AgentConfig.Enroll", "enrollment failed; no partial identity was activated");
            return false;
        }
    }

    /// <summary>
    /// ตั้ง ACL: SYSTEM + Administrators เต็มสิทธิ์เสมอ, Users อ่านได้อย่างเดียวเมื่อ usersRead=true
    /// ตัด inheritance ออกเพื่อไม่ให้สิทธิ์กว้างจากโฟลเดอร์แม่ (ProgramData) ติดลงมา
    /// ใช้ SID มาตรฐานเพื่อไม่ขึ้นกับภาษาของ Windows
    /// </summary>
    internal static void HardenFolder(string path, bool usersRead)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var sid in new[] { system, admins })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        if (usersRead)
        {
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

/// <summary>
/// ที่อยู่เซิร์ฟเวอร์ชุมชนที่ผู้ใช้ตั้งเองได้ เก็บในโปรไฟล์ผู้ใช้ (%LocalAppData%\RainDrop) ซึ่งผู้ใช้เขียนได้
/// ถ้ายังไม่เคยตั้ง จะใช้ค่าเริ่มต้นที่ผู้ดูแลกำหนดตอนติดตั้ง (จาก agent.json)
/// </summary>
internal sealed class CommunitySettings
{
    public string HubAddress { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // ค่าที่ผู้ใช้ตั้งเองเป็นรสนิยมส่วนตัว ไม่ใช่ความลับ จึงเก็บในโปรไฟล์ผู้ใช้ที่เขียนได้
    private static string PathOnDisk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainDrop", "community.json");

    public static CommunitySettings Load(string defaultHub)
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<CommunitySettings>(File.ReadAllText(PathOnDisk)) ?? new CommunitySettings { HubAddress = defaultHub };
        }
        catch (Exception ex)
        {
            // ค่าที่ผู้ใช้ตั้งเองหาย จึงถอยไปใช้ค่าที่ผู้ดูแลกำหนดตอนติดตั้ง (พฤติกรรมเดิม) แต่บันทึกไว้
            Diag.Warn("CommunitySettings.Load", "อ่านค่าตั้งห้องชุมชนไม่สำเร็จ จึงใช้ค่าเริ่มต้น", ex);
        }
        return new CommunitySettings { HubAddress = defaultHub };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // บันทึกไม่ได้ = ค่าที่ผู้ใช้เพิ่งตั้งจะหายเมื่อเปิดใหม่ ไม่ควรล้มทั้งหน้าต่าง จึงบันทึกแทน
            Diag.Warn("CommunitySettings.Save", "บันทึกค่าตั้งห้องชุมชนไม่สำเร็จ", ex);
        }
    }
}

/// <summary>
/// ตัวแอปหลักของ Agent: ไม่มีหน้าต่าง มีแค่ไอคอนใน System Tray
/// งานที่ต้องแตะ UI (ล็อกจอ, กล่องข้อความ) ถูกส่งกลับมาทำบน UI thread ผ่าน RunOnUi
/// </summary>
internal sealed class AgentContext : ApplicationContext, IAgentHandler
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
    private readonly AgentSecurity? _security;
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
        Theme.StyleMenu(menu);
        menu.Items.Add("แชทกับผู้ดูแลระบบ", null, (_, _) => OpenChat());
        menu.Items.Add("เปิดห้องชุมชน (แชท/คอลเสียง)", null, (_, _) => OpenCommunity());
        menu.Items.Add("สถานะการเชื่อมต่อ...", null, (_, _) => ShowStatus());
        menu.Items.Add("ตรวจสอบความถูกต้องของ audit", null, (_, _) => VerifyAudit());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ออกจากโปรแกรม (ต้องใช้รหัสผู้ดูแล)", null, (_, _) => RequestExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenChat();

        var publicKey = AgentConfig.LoadPublicKey();
        if (publicKey is null)
        {
            _tray.ShowBalloonTip(10000, "Rain Drop Agent",
                "ไม่พบกุญแจ challenge ของ Master จึงยังยืนยันตัวตนชั้นที่สองไม่ได้", ToolTipIcon.Warning);
        }
        _communityFingerprint = publicKey is null ? null : Protocol.Fingerprint(publicKey);

        _security = AgentSecurity.LoadFromService(out string? identityError);
        if (_security is null) ReportError("ยังไม่มี Agent identity: " + (identityError ?? "ติดตั้ง enrollment bundle ก่อน"));
        _server = new AgentServer(config.Port, publicKey, this, _security);

        // ส่งแจ้งเตือน (USB/ไฟล์/บล็อกโปรแกรม) ไปยัง Master ที่เชื่อมต่ออยู่
        _activity.Alert += a => _server.BroadcastAlert(a);
        _block.Alert += a => _server.BroadcastAlert(a);

        Task.Run(_server.RunAsync);
    }

    private void VerifyAudit()
    {
        var result = ServiceClient.VerifyAudit(out string? error);
        UiDialog.Show(null, "Rain Drop Audit",
            result is null ? (error ?? "ติดต่อ Service ไม่ได้") : result.Ok ? "audit chain ถูกต้อง" : "audit chain ไม่ถูกต้อง: " + result.AuditError,
            result is { Ok: true } ? DialogKind.Info : DialogKind.Warning);
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
            UiDialog.Show(null, "Rain Drop", "ยังไม่ได้ติดตั้งกุญแจของชุมชน จึงเข้าห้องชุมชนไม่ได้", DialogKind.Warning);
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
        var values = Prompt.Show(null, "ออกจากโปรแกรม Rain Drop Agent",
            ("ใส่รหัสผ่านของผู้ดูแลระบบ:", ""));
        if (values is null) return;

        // Agent ไม่ตรวจรหัสเองแล้ว ส่งให้ Service (SYSTEM) ตรวจจาก hash ที่ผู้ใช่อ่านไม่ได้
        // Service เป็นคนนับครั้งลองผิดและล็อกชั่วคราว กันเดารหัสโดยไม่จำกัด
        ControlResponse? response = ServiceClient.RequestStop(values[0], out string? error);
        if (response is null)
        {
            UiDialog.Show(null, "Rain Drop Agent", error ?? "ติดต่อบริการไม่ได้", DialogKind.Warning);
            return;
        }
        if (!response.Ok)
        {
            UiDialog.Show(null, "Rain Drop Agent", response.Message, DialogKind.Error);
            return;
        }

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

        string keyState = _server.HasPublicKey
            ? $"พบ (ลายนิ้วมือ {_server.KeyFingerprint})"
            : $"ไม่พบไฟล์ {AgentConfig.PublicKeyPath}";
        using var form = new StatusForm(Environment.MachineName,
            ips.Count > 0 ? string.Join(", ", ips) : "ไม่พบ (ไม่ได้ต่อเครือข่าย)",
            _server.Port.ToString(), keyState,
            masters.Count > 0 ? string.Join(", ", masters) : "ยังไม่มี",
            _server.StartError);
        form.ShowDialog();
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
        UiDialog.Show(null, title, text));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Stop();
            _security?.Dispose();
            _activity.Dispose();
            _block.Dispose();
            _chat?.ForceClose();
            _community?.Close();
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
