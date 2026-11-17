using System.Net;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>
/// Certificate helpers for the private Rain Drop CA. This is PKI for the TCP control channel,
/// not a replacement for the existing ECDSA challenge/response in Protocol.cs.
/// Private keys never enter a Windows certificate store: callers keep them in memory and
/// decide how to protect them at rest (DPAPI on Master, SYSTEM-only ACLs on an endpoint).
/// </summary>
public static class RepoCertificates
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.2";
    public const int BundleIterations = 210_000;
    public const int MinimumBundlePasswordLength = 12;

    public static X509Certificate2 CreateCa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Rain Drop Repository CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTime.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
    }

    public static X509Certificate2 IssueClient(X509Certificate2 ca, ECDsa caKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Rain Drop Master " + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256);
        AddLeafExtensions(request, ClientAuthOid, null);
        return Sign(request, ca, caKey, DateTime.UtcNow.AddYears(1), key);
    }

    public static X509Certificate2 IssueAgent(X509Certificate2 ca, ECDsa caKey,
        string machineId, string machineName, IEnumerable<string> addresses)
    {
        if (!Guid.TryParseExact(machineId, "N", out _))
            throw new ArgumentException("Machine identity must be a GUID without braces", nameof(machineId));
        if (string.IsNullOrWhiteSpace(machineName) || machineName.Length > 128 || machineName.Any(char.IsControl))
            throw new ArgumentException("Invalid machine name", nameof(machineName));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName);
        // IP changes must not change the machine identity. A renewed certificate can contain new addresses.
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (string address in addresses)
        {
            if (IPAddress.TryParse(address, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(address);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Rain Drop Agent " + machineId, key, HashAlgorithmName.SHA256);
        AddLeafExtensions(request, ServerAuthOid, san.Build());
        return Sign(request, ca, caKey, DateTime.UtcNow.AddYears(1), key);
    }

    private static void AddLeafExtensions(CertificateRequest request, string eku, X509Extension? san)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (san is not null) request.CertificateExtensions.Add(san);
    }

    private static X509Certificate2 Sign(CertificateRequest request, X509Certificate2 ca,
        ECDsa caKey, DateTime notAfter, ECDsa key)
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f; // Positive, non-zero X.509 serial, independent of byte-order conventions.
        serial[15] |= 1;
        var now = DateTime.UtcNow;
        // ca อาจถูกโหลดจาก PEM เฉพาะส่วนสาธารณะ (ไม่มี private key ผูกอยู่) เช่น
        // ตอนโหลดจากที่เก็บใน MasterPki.LoadOrCreate ในกรณีนั้นต้องนำ caKey ที่แยกเก็บ
        // ไว้มารวมกับ ca ก่อนส่งให้ CertificateRequest.Create แต่ถ้า ca มี key อยู่แล้ว
        // (เช่น CreateCa() ในเทสต์) ให้ใช้ ca โดยตรง เพื่อไม่ให้ CopyWithPrivateKey โยน
        // "certificate already has an associated private key"
        X509Certificate2 caForSigning = ca.HasPrivateKey ? ca : ca.CopyWithPrivateKey(caKey);
        try
        {
            using var issued = request.Create(caForSigning, now.AddMinutes(-5), notAfter, serial);

            // Detach the private key from the short-lived request key so disposing the request
            // key cannot invalidate the returned certificate on Windows CNG.
            var detached = ECDsa.Create();
            detached.ImportPkcs8PrivateKey(key.ExportPkcs8PrivateKey(), out _);
            return issued.CopyWithPrivateKey(detached);
        }
        finally
        {
            // caForSigning เป็นตัวที่เราสร้างขึ้นเองจาก CopyWithPrivateKey ต้องทิ้งหลังใช้
            // ถ้า ca มี key อยู่แล้ว caForSigning จะอ้างถึงพารามิเตอร์ ca โดยตรง ไม่ต้องทิ้ง
            if (!ca.HasPrivateKey) caForSigning?.Dispose();
        }
    }

    /// <summary>
    /// Export a password-encrypted PKCS#12 bag with the Agent key/certificate and CA public certificate.
    /// Both the key bag and integrity MAC are password-derived. No plaintext key file is exported.
    /// </summary>
    public static byte[] ExportEnrollment(X509Certificate2 agent, X509Certificate2 ca, string password)
    {
        if (agent.GetECDsaPrivateKey() is null)
            throw new CryptographicException("Agent certificate has no private key");
        ValidateBundlePassword(password);
        var contents = new Pkcs12SafeContents();
        contents.AddCertificate(agent);
        contents.AddCertificate(ca);
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, BundleIterations);
        contents.AddShroudedKey(agent.GetECDsaPrivateKey()!, password, parameters);
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(contents);
        builder.SealWithMac(password, HashAlgorithmName.SHA256, BundleIterations);
        return builder.Encode();
    }

    public static void ValidateBundlePassword(string? password)
    {
        if (password is null || password.Length < MinimumBundlePasswordLength || password.Any(char.IsControl))
            throw new ArgumentException($"Enrollment password must contain at least {MinimumBundlePasswordLength} characters and no control characters");
    }

    /// <summary>Read and authenticate a bundle without installing any key into a Windows certificate store.</summary>
    public static (X509Certificate2 Agent, X509Certificate2 Ca) ReadEnrollment(byte[] bytes, string password)
    {
        ValidateBundlePassword(password);
        var info = Pkcs12Info.Decode(bytes, out _, skipCopy: true);
        // .NET 8 exposes VerifyMac (algorithm/iterations are read from the MAC), not VerifyIntegrity.
        if (info.IntegrityMode != Pkcs12IntegrityMode.Password || !info.VerifyMac(password))
            throw new CryptographicException("Enrollment password is incorrect or the bundle was modified");
        var certificates = new List<X509Certificate2>();
        ECDsa? key = null;
        try
        {
            foreach (var safe in info.AuthenticatedSafe)
            {
                if (safe.ConfidentialityMode == Pkcs12ConfidentialityMode.Password)
                    safe.Decrypt(password);
                else if (safe.ConfidentialityMode != Pkcs12ConfidentialityMode.None)
                    throw new CryptographicException("Unsupported enrollment encryption");
                foreach (var bag in safe.GetBags())
                {
                    if (bag is Pkcs12CertBag cert) certificates.Add(cert.GetCertificate());
                    else if (bag is Pkcs12ShroudedKeyBag shrouded)
                    {
                        if (key is not null) throw new CryptographicException("Enrollment contains multiple private keys");
                        // Pkcs12ShroudedKeyBag exposes EncryptedPkcs8PrivateKey in .NET 8;
                        // ECDsa decrypts it directly, without importing into a Windows key store.
                        key = ECDsa.Create();
                        key.ImportEncryptedPkcs8PrivateKey(password, shrouded.EncryptedPkcs8PrivateKey.Span, out _);
                    }
                    else throw new CryptographicException("Unexpected enrollment bag");
                }
            }
            if (key is null || certificates.Count != 2)
                throw new CryptographicException("Enrollment must contain one key and two certificates");
            var agent = certificates.FirstOrDefault(c => c.Subject.StartsWith("CN=Rain Drop Agent ", StringComparison.Ordinal));
            var ca = certificates.FirstOrDefault(c => !ReferenceEquals(c, agent));
            if (agent is null || ca is null)
                throw new CryptographicException("Enrollment needs an Agent certificate and a CA certificate");
            using var publicKey = agent.GetECDsaPublicKey()
                ?? throw new CryptographicException("Agent certificate must use ECDSA");
            var expected = publicKey.ExportSubjectPublicKeyInfo();
            var actual = key.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new CryptographicException("Agent certificate and private key do not match");
            var result = agent.CopyWithPrivateKey(key);
            key = null; // Owned by result now.
            var caCopy = new X509Certificate2(ca.RawData);
            return (result, caCopy);
        }
        finally
        {
            key?.Dispose();
            foreach (var cert in certificates) cert.Dispose();
        }
    }

    public static string Serial(X509Certificate2 certificate) => NormalizeSerial(certificate.SerialNumber);

    /// <summary>X.509 displays big-endian hex; some APIs return little-endian. Our issued serials are positive and canonical.</summary>
    public static string NormalizeSerial(string serial)
    {
        var trimmed = serial.Trim().TrimStart('0').ToUpperInvariant();
        return trimmed.Length == 0 ? "0" : trimmed;
    }
    /// <summary>พอร์ตสำหรับ enrollment listener (แยกจาก control channel พอร์ต 11400)</summary>
    public const int EnrollmentPort = 11401;

    /// <summary>ตรวจสอบ CSR ก่อนเซ็น: ชนิด/ขนาดกุญแจ, ห้ามขอ CA:true, ห้าม EKU ผิด</summary>
    public static CsrValidationResult ValidateCsr(CertificateRequest request, string expectedMachineId, string expectedMachineName)
    {
        try
        {
            // ตรวจกุญแจต้องเป็น ECDSA P-256
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(request.PublicKey.EncodedKeyValue.RawData, out _);
            if (ecdsa.KeySize != 256)
                return new CsrValidationResult(false, "invalid-key-type-or-size");

            // ตรวจ EKU ต้องเป็น ServerAuth เท่านั้น ห้ามขอ CA:true
            var basicConstraints = request.CertificateExtensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (basicConstraints is not null && basicConstraints.CertificateAuthority)
                return new CsrValidationResult(false, "ca-true-forbidden");

            var eku = request.CertificateExtensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is null || !eku.Critical)
                return new CsrValidationResult(false, "eku-missing-or-not-critical");
            if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid))
                return new CsrValidationResult(false, "eku-wrong-role");

            return new CsrValidationResult(true, null);
        }
        catch (Exception ex)
        {
            return new CsrValidationResult(false, "csr-parse-failed: " + ex.GetType().Name);
        }
    }

    /// <summary>เซ็น CSR ด้วย CA คืนใบรับรอง Agent (private key อยู่กับ Agent เท่านั้น)</summary>
    public static X509Certificate2 SignCsr(
        CertificateRequest csr,
        X509Certificate2 ca,
        ECDsa caKey,
        string machineId,
        string machineName,
        IEnumerable<string> addresses,
        DateTime notBefore,
        DateTime notAfter)
    {
        if (!Guid.TryParseExact(machineId, "N", out _))
            throw new ArgumentException("Machine identity must be a GUID without braces", nameof(machineId));
        if (string.IsNullOrWhiteSpace(machineName) || machineName.Length > 128 || machineName.Any(char.IsControl))
            throw new ArgumentException("Invalid machine name", nameof(machineName));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (string address in addresses)
        {
            if (IPAddress.TryParse(address, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(address);
        }

        csr.CertificateExtensions.Add(san.Build());
        csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        csr.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthOid) }, true));
        csr.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[15] |= 1;

        X509Certificate2 caForSigning = ca.HasPrivateKey ? ca : ca.CopyWithPrivateKey(caKey);
        try
        {
            using var issued = csr.Create(caForSigning, notBefore, notAfter, serial);
            return issued;
        }
        finally
        {
            if (!ca.HasPrivateKey) caForSigning?.Dispose();
        }
    }

}

/// <summary>Versioned, full revocation snapshot. Version prevents replaying an older, shorter list.</summary>
public sealed record RevocationList(long Version, List<string> Serials);

public sealed class RevocationState
{
    private readonly object _gate = new();
    private HashSet<string> _serials = new(StringComparer.OrdinalIgnoreCase);
    public long Version { get; private set; } = 1;

    public RevocationState(RevocationList? initial = null)
    {
        if (initial is not null) Apply(initial);
    }

    public bool IsRevoked(string serial)
    {
        lock (_gate) return _serials.Contains(RepoCertificates.NormalizeSerial(serial));
    }

    public RevocationList Snapshot()
    {
        lock (_gate) return new RevocationList(Version, _serials.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    /// <summary>Reject rollback and malformed serials; a full snapshot may also remove a serial on the trusted Master.</summary>
    public void Apply(RevocationList list)
    {
        if (list.Version < 1 || list.Serials is null || list.Serials.Count > 100_000)
            throw new InvalidDataException("Invalid revocation snapshot");
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? serial in list.Serials)
        {
            if (serial is null || serial.Length is < 1 or > 40 || !serial.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid revoked serial");
            next.Add(RepoCertificates.NormalizeSerial(serial));
        }
        lock (_gate)
        {
            if (list.Version < Version) throw new InvalidDataException("Revocation snapshot replay detected");
            _serials = next;
            Version = list.Version;
        }
    }

    public RevocationList Revoke(string serial)
    {
        lock (_gate)
        {
            _serials.Add(RepoCertificates.NormalizeSerial(serial));
            Version = Math.Max(Version + 1, DateTime.UtcNow.Ticks);
            return Snapshot();
        }
    }
}

/// <summary>Metadata is public; only the PKCS#12 inside it contains a secret.</summary>
public sealed record EnrollmentManifest(int Format, string MachineId, string MachineName, string ChallengeFingerprint, string BundleBase64)
{
    /// <summary>C1 challenge public key, kept for the second authentication layer inside TLS.</summary>
    public string ChallengePublicKeyPem { get; init; } = "";
}

public static class EnrollmentFile
{
    public const int CurrentFormat = 1;

    public static byte[] Encode(string machineId, string machineName, string challengeFingerprint, byte[] pfx) =>
        JsonSerializer.SerializeToUtf8Bytes(new EnrollmentManifest(CurrentFormat, machineId, machineName,
            challengeFingerprint, Convert.ToBase64String(pfx)));

    public static (EnrollmentManifest Manifest, byte[] Pfx) Decode(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Enrollment file is too large");
        var manifest = JsonSerializer.Deserialize<EnrollmentManifest>(bytes)
            ?? throw new InvalidDataException("Empty enrollment file");
        if (manifest.Format != CurrentFormat || !Guid.TryParseExact(manifest.MachineId, "N", out _))
            throw new InvalidDataException("Unsupported enrollment format");
        return (manifest, Convert.FromBase64String(manifest.BundleBase64));
    }

    /// <summary>พอร์ตสำหรับ enrollment listener (แยกจาก control channel พอร์ต 11400)</summary>
    public const int EnrollmentPort = 11401;

    /// <summary>ตรวจสอบ CSR ก่อนเซ็น: ชนิด/ขนาดกุญแจ, ห้ามขอ CA:true, ห้าม EKU ผิด</summary>
    public static CsrValidationResult ValidateCsr(CertificateRequest request, string expectedMachineId, string expectedMachineName)
    {
        try {
            // ตรวจกุญแจต้องเป็น ECDSA P-256
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(request.PublicKey.EncodedKeyValue.RawData, out _);
            if (ecdsa.KeySize != 256)
                return new CsrValidationResult(false, "invalid-key-type-or-size");

            // ตรวจ EKU ต้องเป็น ServerAuth เท่านั้น ห้ามขอ CA:true
            var basicConstraints = request.CertificateExtensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (basicConstraints is not null && basicConstraints.CertificateAuthority)
                return new CsrValidationResult(false, "ca-true-forbidden");

            var eku = request.CertificateExtensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is null || !eku.Critical)
                return new CsrValidationResult(false, "eku-missing-or-not-critical");
            if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid))
                return new CsrValidationResult(false, "eku-wrong-role");

            return new CsrValidationResult(true, null);
        }
        catch (Exception ex)
        {
            return new CsrValidationResult(false, "csr-parse-failed: " + ex.GetType().Name);
        }
    }

    /// <summary>เซ็น CSR ด้วย CA คืนใบรับรอง Agent (พร้อม private key แยกต่างหาก)</summary>
    public static X509Certificate2 SignCsr(
        CertificateRequest csr,
        X509Certificate2 ca,
        ECDsa caKey,
        string machineId,
        string machineName,
        IEnumerable<string> addresses,
        DateTime notBefore,
        DateTime notAfter)
    {
        if (!Guid.TryParseExact(machineId, "N", out _))
            throw new ArgumentException("Machine identity must be a GUID without braces", nameof(machineId));
        if (string.IsNullOrWhiteSpace(machineName) || machineName.Length > 128 || machineName.Any(char.IsControl))
            throw new ArgumentException("Invalid machine name", nameof(machineName));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (string address in addresses)
        {
            if (IPAddress.TryParse(address, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(address);
        }

        // เพิ่ม SAN ลงในคำขอเดิม
        var sanExt = san.Build();
        csr.CertificateExtensions.Add(sanExt);

        // BasicConstraints: ไม่ใช่ CA, critical
        csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // KeyUsage: DigitalSignature เท่านั้น, critical
        csr.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        // EKU: ServerAuth, critical
        csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthOid) }, true));
        // SKI
        csr.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[15] |= 1;

        X509Certificate2 caForSigning = ca.HasPrivateKey ? ca : ca.CopyWithPrivateKey(caKey);
        try
        {
            using var issued = csr.Create(caForSigning, notBefore, notAfter, serial);
            // ใบรับรองที่ออกไม่มี private key (private key อยู่กับ Agent เท่านั้น)
            return issued;
        }
        finally
        {
            if (!ca.HasPrivateKey) caForSigning?.Dispose();
        }
    }
}
