using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Master;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ใช้ตอน build ตัวติดตั้ง: RainDrop.Master.exe --generate-keys <โฟลเดอร์>
        if (args.Length == 2 && args[0] == "--generate-keys")
            return MasterKey.GenerateKeyPair(args[1]);

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
        catch { /* ใช้ค่าเริ่มต้น */ }
        return new MasterSettings();
    }

    public void Save() =>
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// คู่กุญแจของ Master
/// master-private.pem = กุญแจลับ เก็บไว้ที่เครื่อง Master เท่านั้น
/// master-public.pem  = กุญแจสาธารณะ ใส่ไว้ในตัวติดตั้งของเครื่องลูก (agent-public.pem)
///
/// ลำดับการหากุญแจ:
///   1) โฟลเดอร์ที่ติดตั้งโปรแกรม (ตัวติดตั้งวางไว้ให้ ตรงกับกุญแจใน RainDrop-Agent-Setup.exe)
///   2) %AppData%\RainDrop\master-private.pem
///   3) ถ้าไม่พบทั้งสองที่ จะสร้างใหม่ใน %AppData%\RainDrop
/// </summary>
internal sealed class MasterKey
{
    private readonly ECDsa _key;

    /// <summary>ไฟล์กุญแจสาธารณะที่ใช้ติดตั้งเครื่องลูกเองแบบไม่ใช้ตัวติดตั้ง</summary>
    public static string PublicPath => Path.Combine(DataFolder.Path, "master-public.pem");

    private MasterKey(ECDsa key) => _key = key;

    public static MasterKey LoadOrCreate()
    {
        string userKey = Path.Combine(DataFolder.Path, "master-private.pem");
        string installedKey = Path.Combine(AppContext.BaseDirectory, "master-private.pem");

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(installedKey))
            key.ImportFromPem(File.ReadAllText(installedKey));
        else if (File.Exists(userKey))
            key.ImportFromPem(File.ReadAllText(userKey));
        else
            File.WriteAllText(userKey, key.ExportECPrivateKeyPem());

        File.WriteAllText(PublicPath, key.ExportSubjectPublicKeyInfoPem());
        return new MasterKey(key);
    }

    public string Fingerprint => Protocol.Fingerprint(_key);

    /// <summary>สร้างคู่กุญแจใหม่ลงโฟลเดอร์ที่กำหนด (ถ้ามีอยู่แล้วจะไม่เขียนทับ) คืนค่า 0 เมื่อสำเร็จ</summary>
    public static int GenerateKeyPair(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string privatePath = Path.Combine(folder, "master-private.pem");
            string publicPath = Path.Combine(folder, "master-public.pem");
            if (File.Exists(privatePath)) return 0; // ห้ามเขียนทับ: เครื่องลูกที่ติดตั้งไปแล้วจะเชื่อมต่อไม่ได้

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(privatePath, key.ExportECPrivateKeyPem());
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    public byte[] Sign(byte[] nonce)
    {
        lock (_key) return Protocol.Sign(_key, nonce);
    }
}
