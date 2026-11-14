using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RainDrop.Shared;

namespace RainDrop.AgentService;

/// <summary>
/// ที่เก็บไฟล์ตั้งค่าฝั่ง Service ภายใต้ %ProgramData%\RainDrop
///
/// สิทธิ์ที่ตั้ง:
///   ราก  : SYSTEM + Administrators เต็มสิทธิ์, Users อ่านได้อย่างเดียว (เขียนไม่ได้)
///   private\ : SYSTEM + Administrators เท่านั้น Users ไม่มีสิทธิ์แม้แต่เปิดอ่าน (เก็บ hash รหัสผ่าน)
///
/// ต้องเรียก EnsureLayout ตอน Service เริ่ม และตอนตัวติดตั้งเขียนค่าตั้งต้น (ทั้งสองรันสิทธิ์สูง)
/// </summary>
internal static class ServiceStore
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RainDrop");

    public static string PrivateFolder => Path.Combine(Root, "private");
    public static string CredentialsPath => Path.Combine(PrivateFolder, "credentials.json");
    public static string BlocklistPath => Path.Combine(Root, "blocklist.json");
    public static string AgentConfigPath => Path.Combine(Root, "agent.json");
    public static string CertificatePath => Path.Combine(PrivateFolder, "agent-cert.der");
    public static string PrivateKeyPath => Path.Combine(PrivateFolder, "agent-key.pkcs8");
    public static string CaPath => Path.Combine(PrivateFolder, "ca-cert.der");
    public static string RevocationPath => Path.Combine(PrivateFolder, "revocation.json");
    public static string AllowlistPath => Path.Combine(PrivateFolder, "run-allowlist.json");
    public static string AuditFolder => Path.Combine(PrivateFolder, "audit");

    internal sealed class Credentials
    {
        public string? PasswordSalt { get; set; }
        public string? PasswordHash { get; set; }
    }

    /// <summary>สร้างโฟลเดอร์และตั้ง ACL ให้ถูกต้องทุกครั้ง (idempotent)</summary>
    public static void EnsureLayout(Action<string> log)
    {
        Directory.CreateDirectory(Root);
        ApplyRootAcl(Root);

        Directory.CreateDirectory(PrivateFolder);
        ApplyPrivateAcl(PrivateFolder);
        Directory.CreateDirectory(AuditFolder);
        ApplyPrivateAcl(AuditFolder);
        if (!File.Exists(AllowlistPath))
            File.WriteAllText(AllowlistPath, JsonSerializer.Serialize(new RunAllowlist(new List<string>())));

        log($"ตรวจสิทธิ์โฟลเดอร์ค่าตั้งแล้ว: {Root}");
    }

    private static void ApplyRootAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFull(security, SystemSid);
        AddFull(security, AdminsSid);
        security.AddAccessRule(new FileSystemAccessRule(UsersSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ApplyPrivateAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFull(security, SystemSid);
        AddFull(security, AdminsSid);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void AddFull(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

    /// <summary>อ่าน hash รหัสผ่าน คืน null เมื่อไม่มี (PasswordGate จะปฏิเสธการหยุดเอง)</summary>
    public static (byte[]? Salt, byte[]? Hash) LoadCredentials(Action<string> log)
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return (null, null);
            var creds = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(CredentialsPath));
            if (creds?.PasswordSalt is null || creds.PasswordHash is null) return (null, null);
            return (Convert.FromBase64String(creds.PasswordSalt), Convert.FromBase64String(creds.PasswordHash));
        }
        catch (Exception ex)
        {
            // อ่าน hash ไม่ได้ = ไม่มีใครหยุด Agent ได้ ต้องแจ้งผู้ดูแลผ่าน Event Log ไม่ใช่เงียบไว้
            log("อ่านไฟล์รหัสผ่านผู้ดูแลไม่สำเร็จ การขออนุญาตหยุด Agent จะถูกปฏิเสธ: " + ex.Message);
            return (null, null);
        }
    }

    public static BlockRules LoadBlocklist(Action<string> log)
    {
        try
        {
            if (!File.Exists(BlocklistPath)) return new BlockRules(new List<string>(), new List<string>());
            var rules = JsonSerializer.Deserialize<BlockRules>(File.ReadAllText(BlocklistPath));
            return rules ?? new BlockRules(new List<string>(), new List<string>());
        }
        catch (Exception ex)
        {
            log("อ่านรายการบล็อกไม่สำเร็จ ใช้รายการว่างสำหรับรอบนี้: " + ex.Message);
            return new BlockRules(new List<string>(), new List<string>());
        }
    }

    /// <summary>เขียนรายการที่ตรวจรูปแบบแล้วเท่านั้น ผู้เรียกต้องผ่าน BlocklistValidator มาก่อน</summary>
    public static void SaveBlocklist(BlockRules normalized) =>
        File.WriteAllText(BlocklistPath,
            JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Load the endpoint identity. The private key is released only over the PID-checked local pipe.</summary>
    public static (X509Certificate2 Certificate, X509Certificate2 Ca, RevocationList Revocation, RunAllowlist Allowlist)? LoadIdentity(Action<string> log)
    {
        try
        {
            if (!File.Exists(CertificatePath) || !File.Exists(PrivateKeyPath) || !File.Exists(CaPath)) return null;
            using var publicCert = new X509Certificate2(File.ReadAllBytes(CertificatePath));
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(PrivateKeyPath), out _);
            var certificate = publicCert.CopyWithPrivateKey(key);
            var ca = new X509Certificate2(File.ReadAllBytes(CaPath));
            var revocation = File.Exists(RevocationPath)
                ? JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(RevocationPath)) ?? new RevocationList(1, new List<string>())
                : new RevocationList(1, new List<string>());
            var allowlist = RunCommandPolicy.Load(AllowlistPath);
            return (certificate, ca, revocation, allowlist);
        }
        catch (Exception ex)
        {
            log("อ่าน identity ของ Agent ไม่สำเร็จ การเชื่อมต่อ TLS จะถูกปฏิเสธ: " + ex.GetType().Name);
            return null;
        }
    }

    public static void SaveRevocation(RevocationList incoming)
    {
        var current = File.Exists(RevocationPath)
            ? JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(RevocationPath)) ?? new RevocationList(1, new List<string>())
            : new RevocationList(1, new List<string>());
        var state = new RevocationState(current);
        state.Apply(incoming); // rejects rollback before touching the protected file
        File.WriteAllText(RevocationPath, JsonSerializer.Serialize(state.Snapshot()));
    }
}
