using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Master;

internal static class Program
{
    private static int ExportEnrollmentCli(string machine, string addresses, string output)
    {
        Console.Error.Write("Enrollment password (>=12 chars): ");
        string password = ReadPassword();
        try
        {
            using var key = MasterKey.LoadOrCreate();
            using var pki = MasterPki.LoadOrCreate();
            File.WriteAllBytes(output, pki.ExportEnrollment(machine,
                addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                password, File.ReadAllText(MasterKey.PublicPath)));
            Console.WriteLine("enrolled: " + output);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static string ReadPassword()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    /// <summary>Render หน้าต่างหลักและ dialog สำคัญเป็น PNG (Debug/env เท่านั้น)</summary>
    private static int DumpPng(string firstArg, string secondArg, string[] args)
    {
        // args: --dump-png <mode> <folder>
        // mode = "main" (หน้าต่างหลัก) หรือ "all" (หลัก + dialog สำคัญ)
        string mode = firstArg;
        string folder = secondArg;
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        bool envPreview = string.Equals(Environment.GetEnvironmentVariable("RAINDROP_UI_PREVIEW"), "1", StringComparison.OrdinalIgnoreCase);
        if (!envPreview && !Debugger.IsAttached) return 1;

        var scales = new[] { 1.0f, 1.25f, 1.5f };
        foreach (var scale in scales)
        {
            using var form = new MainForm(new[] { "--ui-preview" });
            form.Size = new Size((int)(1400 * scale), (int)(860 * scale));
            form.Show();
            var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(folder, $"main_{scale:0.00}.png"), ImageFormat.Png);
            form.Close();
        }

        if (mode == "all")
        {
            // Dialog ตัวอย่าง (settings, confirm, alert)
            using var dlg = new Form();
            Theme.ConfigureForm(dlg, new Size(Theme.DialogMinWidth, Theme.DialogMinHeight));
            dlg.Text = "ตัวอย่าง Dialog";
            dlg.Size = new Size((int)(500 * 1.0f), (int)(300 * 1.0f));
            foreach (var scale in scales)
            {
                var bmp = new Bitmap((int)(500 * scale), (int)(300 * scale));
                dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(folder, $"dialog_{scale:0.00}.png"), ImageFormat.Png);
            }
        }

        Console.WriteLine($"ส่งออก PNG แล้วที่ {folder}");
        return 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // ส่งออกเฉพาะกุญแจสาธารณะให้ผู้ติดตั้งเครื่องลูก: --export-public-key <โฟลเดอร์>
        if (args.Length == 2 && args[0] == "--export-public-key")
            return MasterKey.ExportPublicKey(args[1]);
        if (args.Length == 1 && args[0] == "--verify-audit")
        {
            var result = AuditLog.Verify(MasterPki.AuditFolder);
            Console.WriteLine(result.Valid ? $"audit valid ({result.Records} records)" : "audit invalid: " + result.Error);
            return result.Valid ? 0 : 1;
        }
        if (args.Length == 4 && args[0] == "--export-enrollment")
            return ExportEnrollmentCli(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "--revoke")
        {
            try
            {
                using var pki = MasterPki.LoadOrCreate();
                pki.Revoke(args[1]);
                Console.WriteLine("revoked: " + args[1]);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        // --dump-png <mode> <folder>: render หน้าต่างเป็น PNG (Debug/env เท่านั้น)
        if (args.Length == 3 && args[0] == "--dump-png" &&
            (Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("RAINDROP_UI_PREVIEW"), "1", StringComparison.OrdinalIgnoreCase)))
        {
            return DumpPng(args[1], args[2], args);
        }

        // ถ้าเกิด exception ที่ไม่ได้จับในฟอร์มหรือเธรดใด ๆ ให้บันทึก log และแจ้งผู้ใช้เป็นภาษาไทย
        Application.ThreadException += (_, e) =>
        {
            Diag.Error("UnhandledUI", "เกิดข้อผิดพลาดที่ไม่ได้จับในเธรดส่วนติดต่อผู้ใช้", e.Exception);
            ShowFatalDialog(e.Exception);
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Unknown exception");
            Diag.Error("UnhandledDomain", "เกิดข้อผิดพลาดที่ไม่ได้จับในโดเมนแอปพลิเคชัน", ex);
            ShowFatalDialog(ex);
            Environment.Exit(1);
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args));
        return 0;
    }

    private static void ShowFatalDialog(Exception ex)
    {
        try
        {
            string logPath = Diag.LogPath;
            System.Windows.Forms.MessageBox.Show(
                $"โปรแกรม Rain Drop Master หยุดทำงานอย่างกะทันหัน\n\n" +
                $"สาเหตุ: {ex.GetType().Name}\n{ex.Message}\n\n" +
                $"บันทึกรายละเอียดไว้ที่ไฟล์: {logPath}\n" +
                $"โปรดส่งไฟล์นี้ให้ทีมสนับสนุนเพื่อตรวจสอบ",
                "Rain Drop Master – ข้อผิดพลาดร้ายแรง",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch
        {
            // ถ้าแสดงกล่องข้อความไม่ได้ ก็ปล่อยให้ปิดเงียบ ๆ ตามเดิม
        }
    }
}

/// <summary>โฟลเดอร์เก็บข้อมูลของผู้ใช้ (%AppData%\\RainDrop) เพราะเขียนลง Program Files ไม่ได้</summary>
internal static class DataFolder
{
    private static string? _overridePath;

    public static string Path
    {
        get
        {
            if (_overridePath is not null) return _overridePath;
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainDrop");
            Directory.CreateDirectory(dir);
            return dir;
        }
        internal set => _overridePath = value;
    }

    internal static void ResetOverride() => _overridePath = null;
}

internal sealed class HostEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>รองรับการระบุพอร์ตเอง เช่น "192.168.1.20:11500"</summary>
    public (string Host, int Port) Resolve(int defaultPort)
    {
        int idx = Address.LastIndexOf(':');
        if (idx > 0 && int.TryParse(Address[(idx + 1)..], out int port))
            return (Address[..idx], port);
        return (Address, defaultPort);
    }
}

internal sealed class MasterSettings
{
    public int Port { get; set; } = Protocol.DefaultPort;
    public List<HostEntry> Hosts { get; set; } = new();

    /// <summary>ค้นหาและเพิ่มเครื่องลูกในวงแลนอัตโนมัติ</summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>ชื่อเครื่องที่ผู้ดูแลลบออกเอง จะไม่ถูกเพิ่มกลับมาจากการค้นหาอัตโนมัติ</summary>
    public List<string> IgnoredMachines { get; set; } = new();

    /// <summary>ที่อยู่เซิร์ฟเวอร์กลาง (Hub) สำหรับห้องชุมชน เช่น "192.168.1.10" หรือ "192.168.1.10:11500"</summary>
    public string HubAddress { get; set; } = "";

    /// <summary>ชื่อที่จะแสดงในห้องชุมชน (เว้นว่าง = ใช้ชื่อผู้ใช้ Windows)</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>เปิด/ปิดการแจ้งเตือนจากเครื่องลูก (USB/ไฟล์/บล็อก)</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>รายการบล็อกโปรแกรม/เว็บ แยกตามชื่อเครื่อง</summary>
    public Dictionary<string, BlockRules> Blocklists { get; set; } = new();

    private static string PathOnDisk => Path.Combine(DataFolder.Path, "master.json");

    public static MasterSettings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<MasterSettings>(File.ReadAllText(PathOnDisk)) ?? new MasterSettings();
        }
        catch (Exception ex)
        {
            Diag.Warn("MasterSettings.Load", "อ่านค่าตั้งของ Master ไม่สำเร็จ จึงใช้ค่าเริ่มต้น", ex);
        }
        return new MasterSettings();
    }

    public void Save() =>
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// คู่กุญแจของ Master สร้างและเก็บ "บนเครื่อง Master เท่านั้น"
///   %AppData%\\RainDrop\\master-private.dpapi = กุญแจลับ เข้ารหัสด้วย DPAPI (CurrentUser)
///   %AppData%\\RainDrop\\master-public.pem   = กุญแจสาธารณะ สำหรับส่งให้ผู้ติดตั้งเครื่องลูก
///
/// ไม่อ่านกุญแจจากโฟลเดอร์ที่ติดตั้งโปรแกรมอีกต่อไป (เดิมตัวติดตั้งฝัง private key มาให้
/// ซึ่งทำให้ทุกคนที่ได้ตัวติดตั้ง Master ปลอมเป็น Master ได้)
/// ถ้าพบไฟล์ PEM เดิมในโปรไฟล์ผู้ใช้ จะอ่านครั้งเดียว เข้ารหัส แล้วลบไฟล์ plaintext ทิ้ง
/// </summary>
internal sealed class MasterKey : IDisposable
{
    private readonly ECDsa _key;

    /// <summary>ไฟล์กุญแจสาธารณะที่ใช้ติดตั้งเครื่องลูก (ส่งให้ผู้ติดตั้งทางช่องทางที่เชื่อถือได้)</summary>
    public static string PublicPath => Path.Combine(DataFolder.Path, "master-public.pem");

    public static string PrivateBlobPath => Path.Combine(DataFolder.Path, "master-private.dpapi");

    /// <summary>พบกุญแจลับเก่าที่ตัวติดตั้งรุ่นก่อนวางไว้ในโฟลเดอร์โปรแกรมหรือไม่ (ต้องแจ้งผู้ดูแลให้หมุนกุญแจ)</summary>
    public static bool LegacyKeyInInstallFolder { get; private set; }

    private MasterKey(ECDsa key) => _key = key;

    /// <summary>สร้าง instance สำหรับโหมดตัวอย่าง UI (ไม่เขียนที่เก็บ)</summary>
    internal static MasterKey CreateForPreview(ECDsa key) => new MasterKey(key);

    public static MasterKey LoadOrCreate()
    {
        string legacyPem = Path.Combine(DataFolder.Path, "master-private.pem");
        LegacyKeyInInstallFolder = File.Exists(Path.Combine(AppContext.BaseDirectory, "master-private.pem"));
        if (LegacyKeyInInstallFolder)
            Diag.Error("MasterKey",
                "พบ master-private.pem ในโฟลเดอร์โปรแกรม ซึ่งเป็นกุญแจที่ตัวติดตั้งรุ่นเก่าแจกไว้ " +
                "ถือว่าหลุดแล้ว ควรสร้างกุญแจใหม่และติดตั้งเครื่องลูกใหม่ทั้งหมด");

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(PrivateBlobPath))
        {
            key.ImportPkcs8PrivateKey(KeyVault.LoadEncrypted(PrivateBlobPath), out _);
        }
        else if (KeyVault.MigrateFromPem(legacyPem, PrivateBlobPath))
        {
            key.ImportPkcs8PrivateKey(KeyVault.LoadEncrypted(PrivateBlobPath), out _);
        }
        else
        {
            KeyVault.SaveEncrypted(PrivateBlobPath, key.ExportPkcs8PrivateKey());
        }

        File.WriteAllText(PublicPath, key.ExportSubjectPublicKeyInfoPem());
        return new MasterKey(key);
    }

    public string Fingerprint => Protocol.Fingerprint(_key);

    /// <summary>
    /// ส่งออก "เฉพาะกุญแจสาธารณะ" ไปยังโฟลเดอร์ที่กำหนด เพื่อให้ผู้ติดตั้งนำไปใช้กับตัวติดตั้งเครื่องลูก
    /// ไม่มีการส่งออกกุญแจลับออกจากเครื่อง Master อีกต่อไป
    /// </summary>
    public static int ExportPublicKey(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var loaded = LoadOrCreate();
            File.WriteAllText(Path.Combine(folder, "master-public.pem"), File.ReadAllText(PublicPath));
            Console.WriteLine("ส่งออกกุญแจสาธารณะแล้ว: " + Path.Combine(folder, "master-public.pem"));
            Console.WriteLine("ลายนิ้วมือ: " + loaded.Fingerprint);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ส่งออกกุญแจสาธารณะไม่สำเร็จ: " + ex.Message);
            return 1;
        }
    }

    public byte[] Sign(byte[] nonce)
    {
        lock (_key) return Protocol.Sign(_key, nonce);
    }

    public void Dispose() => _key.Dispose();
}
