using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// Repository CA and issued identities. All private material is DPAPI CurrentUser under
/// %AppData%\RainDrop, exactly like the C1 challenge key. The endpoint enrollment bundle
/// is the only export containing an Agent private key, and it is password-encrypted PKCS#12.
/// </summary>
internal sealed class MasterPki : IDisposable
{
    private readonly ECDsa _caKey;
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _client;
    private readonly object _gate = new();
    private List<IssuedAgent> _issued;
    private RevocationState _revoked;

    internal sealed record IssuedAgent(string MachineId, string MachineName, string Serial, DateTime NotAfterUtc);
    private sealed record ProtectedState(List<IssuedAgent> Issued, RevocationList Revocation);

    public static string CaPublicPath => Path.Combine(DataFolder.Path, "ca-public.pem");
    public static string ClientPublicPath => Path.Combine(DataFolder.Path, "master-client-public.pem");
    public static string AuditFolder => Path.Combine(DataFolder.Path, "audit");
    private static string CaKeyPath => Path.Combine(DataFolder.Path, "ca-private.dpapi");
    private static string ClientKeyPath => Path.Combine(DataFolder.Path, "master-client-private.dpapi");
    private static string StatePath => Path.Combine(DataFolder.Path, "pki-state.dpapi");

    public X509Certificate2 CaCertificate => _ca;
    public X509Certificate2 ClientCertificate => _client;
    public AuditLog Audit { get; }

    private MasterPki(ECDsa caKey, X509Certificate2 ca, X509Certificate2 client, ProtectedState state)
    {
        _caKey = caKey;
        _ca = ca;
        _client = client;
        _issued = state.Issued;
        _revoked = new RevocationState(state.Revocation);
        Audit = new AuditLog(AuditFolder);
    }

    /// <summary>สร้าง instance สำหรับโหมดตัวอย่าง UI (ไม่เขียนที่เก็บ ไม่เชื่อมต่อเครือข่าย)</summary>
    internal static MasterPki CreateForPreview(ECDsa caKey, X509Certificate2 ca, X509Certificate2 client)
    {
        var state = new ProtectedState(new List<IssuedAgent>(), new RevocationList(1, new List<string>()));
        return new MasterPki(caKey, ca, client, state);
    }

    public static MasterPki LoadOrCreate()
    {
        ECDsa? caKey = null;
        X509Certificate2? ca = null, client = null;
        try
        {
            if (File.Exists(CaKeyPath) && File.Exists(CaPublicPath))
            {
                try
                {
                    caKey = ECDsa.Create();
                    caKey.ImportPkcs8PrivateKey(KeyVault.LoadEncrypted(CaKeyPath), out _);
                }
                catch (CryptographicException)
                {
                    throw new InvalidDataException("อ่านกุญแจลับของ CA ไม่สำเร็จ: ไฟล์ .dpapi ถอดรหัสไม่ได้ (อาจเกิดจากบัญชีผู้ใช้หรือเครื่องคอมพิวเตอร์เปลี่ยนไป) ถ้ายังสร้างกุญแจใหม่ทับใบรับรองเดิมทั้งหมดจะใช้ไม่ได้ กู้คืนไฟล์สำรองหรือติดต่อทีมสนับสนุนก่อนดำเนินการต่อ");
                }
                ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaPublicPath));
            }
            else
            {
                if (File.Exists(CaKeyPath) || File.Exists(CaPublicPath))
                    throw new InvalidDataException("CA key/certificate pair is incomplete; restore both from backup");
                using var created = RepoCertificates.CreateCa();
                caKey = created.GetECDsaPrivateKey() ?? throw new CryptographicException("CA key missing");
                // Copy key so disposing the created certificate does not dispose our long-lived key.
                var exported = caKey.ExportPkcs8PrivateKey();
                caKey.Dispose();
                caKey = ECDsa.Create();
                caKey.ImportPkcs8PrivateKey(exported, out _);
                CryptographicOperations.ZeroMemory(exported);
                ca = new X509Certificate2(created.RawData);
                KeyVault.SaveEncrypted(CaKeyPath, caKey.ExportPkcs8PrivateKey());
                File.WriteAllText(CaPublicPath, ca.ExportCertificatePem());
            }

            if (File.Exists(ClientKeyPath) && File.Exists(ClientPublicPath))
            {
                try
                {
                    using var pub = X509Certificate2.CreateFromPem(File.ReadAllText(ClientPublicPath));
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(KeyVault.LoadEncrypted(ClientKeyPath), out _);
                }
                catch (CryptographicException)
                {
                    throw new InvalidDataException("อ่านกุญแจลับของ Master client ไม่สำเร็จ: ไฟล์ .dpapi ถอดรหัสไม่ได้ (อาจเกิดจากบัญชีผู้ใช้หรือเครื่องคอมพิวเตอร์เปลี่ยนไป) ถ้ายังสร้างกุญแจใหม่ทับใบรับรองเดิมทั้งหมดจะใช้ไม่ได้ กู้คืนไฟล์สำรองหรือติดต่อทีมสนับสนุนก่อนดำเนินการต่อ");
                }
                client = pub.CopyWithPrivateKey(key);
            }
            else
            {
                if (File.Exists(ClientKeyPath) || File.Exists(ClientPublicPath))
                    throw new InvalidDataException("Master client key/certificate pair is incomplete");
                using var issued = RepoCertificates.IssueClient(ca, caKey);
                var detached = ECDsa.Create();
                detached.ImportPkcs8PrivateKey(issued.GetECDsaPrivateKey()!.ExportPkcs8PrivateKey(), out _);
                client = new X509Certificate2(issued.RawData).CopyWithPrivateKey(detached);
                KeyVault.SaveEncrypted(ClientKeyPath, detached.ExportPkcs8PrivateKey());
                File.WriteAllText(ClientPublicPath, client.ExportCertificatePem());
            }

            var state = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<ProtectedState>(KeyVault.LoadEncrypted(StatePath))
                    ?? throw new InvalidDataException("Empty PKI state")
                : new ProtectedState(new List<IssuedAgent>(), new RevocationList(1, new List<string>()));
            var pki = new MasterPki(caKey, ca, client, state);
            caKey = null; ca = null; client = null; // ownership transferred
            return pki;
        }
        finally
        {
            caKey?.Dispose(); ca?.Dispose(); client?.Dispose();
        }
    }

    public RevocationList RevocationSnapshot() { lock (_gate) return _revoked.Snapshot(); }

    public string? ExpectedSerial(string machineName)
    {
        lock (_gate)
            return _issued.LastOrDefault(a => a.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase))?.Serial;
    }

    public bool IsRevoked(string serial) { lock (_gate) return _revoked.IsRevoked(serial); }

    public byte[] ExportEnrollment(string machineName, IEnumerable<string> addresses, string password, string challengePublicKeyPem)
    {
        RepoCertificates.ValidateBundlePassword(password);
        lock (_gate)
        {
            if (_issued.Any(a => a.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase) && !_revoked.IsRevoked(a.Serial)))
                throw new InvalidDataException("Machine already has an active certificate; revoke it before re-enrollment");
            string id = Guid.NewGuid().ToString("N");
            using var agent = RepoCertificates.IssueAgent(_ca, _caKey, id, machineName, addresses);
            string serial = RepoCertificates.Serial(agent);
            byte[] pfx = RepoCertificates.ExportEnrollment(agent, _ca, password);
            _issued.Add(new IssuedAgent(id, machineName, serial, agent.NotAfter.ToUniversalTime()));
            SaveState();
            using var challengeKey = ReadChallengeKey(challengePublicKeyPem);
            var manifest = new EnrollmentManifest(EnrollmentFile.CurrentFormat, id, machineName,
                Protocol.Fingerprint(challengeKey), Convert.ToBase64String(pfx))
            { ChallengePublicKeyPem = challengePublicKeyPem };
            Audit.Append(new AuditEvent("enroll", "-", "ok", serial));
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
    }

    private static ECDsa ReadChallengeKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    /// <summary>ออก client certificate ของ Master ด้วยคู่กุญแจ CA ที่โหลดไว้ (ใช้สำหรับเทสต์เส้นทางจริง)</summary>
    internal X509Certificate2 IssueClientCertificate() => RepoCertificates.IssueClient(_ca, _caKey);

    public RevocationList Revoke(string machineName)
    {
        lock (_gate)
        {
            var agent = _issued.LastOrDefault(a => a.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("No enrolled certificate for " + machineName);
            var snapshot = _revoked.Revoke(agent.Serial);
            SaveState();
            Audit.Append(new AuditEvent("revoke", "-", "ok", agent.Serial));
            return snapshot;
        }
    }

    private void SaveState()
    {
        var state = new ProtectedState(_issued.ToList(), _revoked.Snapshot());
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state);
        try { KeyVault.SaveEncrypted(StatePath, json); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    public void Dispose()
    {
        Audit.Dispose();
        _client.Dispose();
        _ca.Dispose();
        _caKey.Dispose();
    }
    /// <summary>เซ็น CSR ที่ Agent ส่งมา ผลัดใบรับรองใหม่ให้เครื่องลูก (ไม่มี private key ของ Agent หลุดออกจากเครื่องมัน)</summary>
    public X509Certificate2 SignAgentCsr(CertificateRequest csr, string machineId, string machineName,
        IEnumerable<string> addresses, out string serial, out DateTime notAfterUtc)
    {
        lock (_gate)
        {
            // NotBefore ย้อนหลัง 1 วัน กันปัญหานาฬิกาเครื่องลูกไม่ตรงกัน
            var notBefore = DateTime.UtcNow.AddDays(-1);
            notAfterUtc = DateTime.UtcNow.AddYears(1);

            var issued = RepoCertificates.SignCsr(csr, _ca, _caKey, machineId, machineName, addresses, notBefore, notAfterUtc);
            serial = RepoCertificates.Serial(issued);
            _issued.Add(new IssuedAgent(machineId, machineName, serial, notAfterUtc));
            SaveState();
            Audit.Append(new AuditEvent("enroll-csr", machineName, "ok", serial));
            return issued;
        }
    }

    /// <summary>คืนข้อมูลวันหมดอายุของใบรับรองเครื่อง (สำหรับแสดงใน UI และเตือนเมื่อเหลือ < 30 วัน)</summary>
    public (string Serial, DateTime NotAfterUtc, bool IsRevoked)? GetAgentExpiry(string machineName)
    {
        lock (_gate)
        {
            var agent = _issued.LastOrDefault(a => a.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase));
            if (agent is null) return null;
            return (agent.Serial, agent.NotAfterUtc, _revoked.IsRevoked(agent.Serial));
        }
    }

    /// <summary>ยกเลิกใบรับรองเก่าและออกให้ใหม่ (re-enroll)</summary>
    public X509Certificate2 ReEnrollAgent(CertificateRequest csr, string machineId, string machineName,
        IEnumerable<string> addresses, out string serial, out DateTime notAfterUtc)
    {
        lock (_gate)
        {
            var existing = _issued.LastOrDefault(a => a.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("ไม่พบใบรับรองเดิมของเครื่องนี้");
            var snapshot = _revoked.Revoke(existing.Serial);
            SaveState();
            Audit.Append(new AuditEvent("revoke", machineName, "ok", existing.Serial));
            return SignAgentCsr(csr, machineId, machineName, addresses, out serial, out notAfterUtc);
        }
    }

    /// <summary>ตรวจว่าเครื่องใดบ้างที่ใบรับรองจะหมดอายุภายใน 30 วัน</summary>
    public List<(string MachineName, string Serial, DateTime NotAfterUtc)> ExpiringWithin30Days()
    {
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow.AddDays(30);
            return _issued
                .Where(a => !_revoked.IsRevoked(a.Serial) && a.NotAfterUtc <= cutoff)
                .Select(a => (a.MachineName, a.Serial, a.NotAfterUtc))
                .ToList();
        }
    }
}
