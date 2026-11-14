using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Master;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ส่งออกเฉพาะกุญแจสาธารณะให้ผู้ติดตั้งเครื่องลูก: --export-public-key <โฟลเดอร์>
        // (เลิก --generate-keys เพราะไม่ควรสร้าง/แจกกุญแจลับตอน build อีกต่อไป)
        if (args.Length == 2 && args[0] == "--export-public-key")
            return MasterKey.ExportPublicKey(args[1]);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}

/// <summary>โฟลเดอร์เก็บข้อมูลของผู้ใช้ (%AppData%\RainDrop) เพราะเขียนลง Program Files ไม่ได้</summary>
internal static class DataFolder
{
    public static string Path
    {
        get
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainDrop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
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
            // ตั้งค่าหาย = รายชื่อเครื่อง/รายการบล็อกที่ผู้ดูแลเคยตั้งไว้จะไม่ถูกโหลด ต้องตรวจสอบได้
            Diag.Warn("MasterSettings.Load", "อ่านค่าตั้งของ Master ไม่สำเร็จ จึงใช้ค่าเริ่มต้น", ex);
        }
        return new MasterSettings();
    }

    public void Save() =>
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// คู่กุญแจของ Master สร้างและเก็บ "บนเครื่อง Master เท่านั้น"
///   %AppData%\RainDrop\master-private.dpapi = กุญแจลับ เข้ารหัสด้วย DPAPI (CurrentUser)
///   %AppData%\RainDrop\master-public.pem   = กุญแจสาธารณะ สำหรับส่งให้ผู้ติดตั้งเครื่องลูก
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
            // ไฟล์ PEM ถูกลบไปแล้ว อ่านจาก blob ที่เพิ่งเข้ารหัสแทน
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
