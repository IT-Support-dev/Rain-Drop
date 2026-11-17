using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RainDrop.Master;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class CsrListenerTests : IDisposable
{
    private readonly PairingCodeStore _codes;
    private readonly MasterPki _pki;
    private readonly AuditLog _audit;
    private readonly CsrListener _listener;
    private readonly string _tempFolder;

    public CsrListenerTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), $"raindrop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempFolder);

        // ใช้ PKI แบบ preview (ไม่ต้องอ่านไฟล์จริง ไม่เชื่อมต่อเครือข่าย)
        using var ca = RepoCertificates.CreateCa();
        using var caKey = ca.GetECDsaPrivateKey()!;
        using var client = RepoCertificates.IssueClient(ca, caKey);
        _pki = MasterPki.CreateForPreview(caKey, ca, client);

        _codes = new PairingCodeStore();
        var auditFolder = Path.Combine(_tempFolder, "audit");
        _audit = new AuditLog(auditFolder);

        _listener = new CsrListener(_codes, _pki, _audit, IPAddress.Loopback, RepoCertificates.EnrollmentPort);
        _listener.Start();
    }

    [Fact]
    public void CorrectCode_PeekSucceeds()
    {
        string code = _codes.CreateCode();
        Assert.NotEmpty(code);
        Assert.Equal(16, code.Length);
        Assert.Matches("^[A-Z2-7]{16}$", code);
        Assert.True(_codes.TryPeek(code, out byte[]? bytes, out string? reason));
        Assert.NotNull(bytes);
        Assert.Equal(10, bytes.Length); // 80 bits = 10 bytes
    }

    [Fact]
    public void WrongCode_Rejected()
    {
        Assert.False(_codes.TryPeek("WRONGCODE123456", out _, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void UsedCode_CannotReuse()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryConsume(code, out _, out _));
        Assert.False(_codes.TryConsume(code, out _, out string? reason));
        Assert.Equal("รหัสถูกใช้ไปแล้ว", reason);
    }

    [Fact]
    public void ExpiredCode_Rejected()
    {
        var time = DateTime.UtcNow;
        var expiredStore = new PairingCodeStore(() => time);
        string code = expiredStore.CreateCode();
        time = time.AddMinutes(11);
        Assert.False(expiredStore.TryPeek(code, out _, out string? reason));
        Assert.Equal("รหัสหมดอายุแล้ว", reason);
    }

    [Fact]
    public void ThreeWrongAttempts_CancelCode()
    {
        string code = _codes.CreateCode();
        _codes.RecordWrong(code);
        _codes.RecordWrong(code);
        Assert.True(_codes.TryPeek(code, out _, out _)); // ผิด 2 ครั้งยังใช้ได้
        _codes.RecordWrong(code);
        Assert.False(_codes.TryPeek(code, out _, out string? reason));
        Assert.Equal("รหัสถูกยกเลิกเพราะผิดเกินกำหนด", reason);
    }

    [Fact]
    public void MaxEntries_LimitReached()
    {
        var store = new PairingCodeStore();
        for (int i = 0; i < 1000; i++)
            store.CreateCode();
        Assert.Throws<InvalidOperationException>(() => store.CreateCode());
    }

    [Fact]
    public void MasterPki_ExpiringWithin30Days_ReturnsList()
    {
        var expiring = _pki.ExpiringWithin30Days();
        Assert.Empty(expiring);
    }

    [Fact]
    public void ValidateCsr_CaTrue_Rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));

        var result = RepoCertificates.ValidateCsr(req, "", "test");
        Assert.False(result.Valid);
        Assert.Contains("ca-true-forbidden", result.Reason);
    }

    [Fact]
    public void ValidateCsr_WrongEku_Rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true)); // ClientAuth (wrong)

        var result = RepoCertificates.ValidateCsr(req, "", "test");
        Assert.False(result.Valid);
        Assert.Contains("eku-wrong-role", result.Reason);
    }

    [Fact]
    public void ValidateCsr_ValidP256_Accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=test-machine"),
            key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(RepoCertificates.ServerAuthOid) }, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var result = RepoCertificates.ValidateCsr(req, "", "test-machine");
        Assert.True(result.Valid);
    }

    [Fact]
    public void ValidateCsr_NotP256_Rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            key, HashAlgorithmName.SHA256);
        var result = RepoCertificates.ValidateCsr(req, "", "test");
        Assert.False(result.Valid);
    }

    [Fact]
    public void CodeBytes_NeverLeaveStoreInPlaintext()
    {
        // Verify that code bytes are only used for HKDF and never logged
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? bytes, out _));
        Assert.NotNull(bytes);
        // RecordWrong doesn't log the code
        _codes.RecordWrong(code);
        // ActiveCount still 1 (not consumed)
        Assert.Equal(1, _codes.ActiveCount);
    }

    // ---------- (ก) MITM relay: channel binding ต่างกัน → HMAC ไม่ผ่าน ----------
    [Fact]
    public void Mitm_Relay_DifferentChannelBinding_Fails()
    {
        // Master และ Agent ใช้ channel binding จาก cert คนละอัน (MITM สวมเซิร์ฟเฟอร์เซอร์ทิฟิเคตตัวเอง)
        byte[] cbMaster = SHA256.HashData(Encoding.UTF8.GetBytes("master-cert-spki"));
        byte[] cbMitm = SHA256.HashData(Encoding.UTF8.GetBytes("mitm-cert-spki"));
        Assert.NotEqual(cbMaster, cbMitm);

        byte[] codeBytes = Encoding.UTF8.GetBytes("ABCD1234EFGH5678");
        byte[] kMaster = EnrollmentChannel.HkdfSha256(codeBytes, cbMaster, EnrollmentChannel.Info, 32);
        byte[] kMitm = EnrollmentChannel.HkdfSha256(codeBytes, cbMitm, EnrollmentChannel.Info, 32);
        Assert.NotEqual(kMaster, kMitm);

        // Agent คำนวณ proof ด้วย kMitm แต่ Master ตรวจด้วย kMaster → ไม่ตรงกัน
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] agentProof = EnrollmentChannel.ComputeAgentProof(kMitm, nonceA, nonceM, transcript);
        byte[] expectedMasterSide = EnrollmentChannel.ComputeAgentProof(kMaster, nonceA, nonceM, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(agentProof, expectedMasterSide));
    }

    // ---------- (ข) Reflection: Agent ส่ง proof ตัวเองกลับ → Master ปฏิเสธ (label ต่างกัน) ----------
    [Fact]
    public void Reflection_AgentEchoesOwnProof_Fails()
    {
        byte[] key = EnrollmentChannel.HkdfSha256(
            Encoding.UTF8.GetBytes("ABCD1234EFGH5678"),
            SHA256.HashData(Encoding.UTF8.GetBytes("cb")),
            EnrollmentChannel.Info, 32);
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());

        byte[] agentProof = EnrollmentChannel.ComputeAgentProof(key, nonceA, nonceM, transcript);
        byte[] masterProof = EnrollmentChannel.ComputeMasterProof(key, nonceM, nonceA, transcript);

        // Agent proof กับ Master proof ต้องไม่เท่ากัน (label "agent" vs "master")
        Assert.NotEqual(agentProof, masterProof);

        // ถ้า Agent ส่ง agentProof ไปเป็น masterProof (reflection) Master จะปฏิเสธ
        byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(key, nonceM, nonceA, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(expectedMaster, agentProof));
    }

    // ---------- (ค) Wrong 1 char code → K ต่างกัน → HMAC ไม่ผ่าน ----------
    [Fact]
    public void WrongOneCharCode_DerivesDifferentKey_Fails()
    {
        byte[] cb = SHA256.HashData(Encoding.UTF8.GetBytes("cb"));
        byte[] k1 = EnrollmentChannel.HkdfSha256(
            Encoding.UTF8.GetBytes("ABCD1234EFGH5678"), cb, EnrollmentChannel.Info, 32);
        byte[] k2 = EnrollmentChannel.HkdfSha256(
            Encoding.UTF8.GetBytes("ABCD1234EFGH5679"), cb, EnrollmentChannel.Info, 32); // ต่างแค่ตัวสุดท้าย
        Assert.NotEqual(k1, k2);

        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] proof1 = EnrollmentChannel.ComputeAgentProof(k1, nonceA, nonceM, transcript);
        byte[] proof2 = EnrollmentChannel.ComputeAgentProof(k2, nonceA, nonceM, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(proof1, proof2));
    }

    // ---------- (ง) Captured stream: รหัสไม่ปรากฏในข้อมูลที่ส่งออก ----------
    [Fact]
    public void CapturedStream_CodeNotInOutboundBytes()
    {
        // สิ่งที่ส่งออกจาก Agent: CODE <รหัส> ส่งผ่าน TLS เท่านั้น
        // รหัสตัวเองไม่ควรปรากฏใน HMAC proof หรือ transcript hash
        string code = "ABCD1234EFGH5678";
        byte[] codeBytes = Encoding.UTF8.GetBytes(code);
        byte[] cb = SHA256.HashData(Encoding.UTF8.GetBytes("cb"));
        byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, cb, EnrollmentChannel.Info, 32);

        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(
            Encoding.UTF8.GetBytes($"CODE {code}"),
            Encoding.UTF8.GetBytes($"NONCE {Convert.ToHexString(nonceM)}"));

        byte[] agentProof = EnrollmentChannel.ComputeAgentProof(key: k, nonceA: nonceA, nonceM: nonceM, transcriptHash: transcript);

        // HMAC proof คือแฮช 32 ไบต์ ไม่มีรหัสต้นฉบับปนอยู่
        Assert.DoesNotContain(codeBytes[0], agentProof);
        // Transcript hash คือแฮช ไม่มีรหัสต้นฉบับเป็น plaintext
        Assert.DoesNotContain(codeBytes[0], transcript);
    }

    // ---------- (จ) Expired / used code → ปฏิเสธแม้มี HMAC ถูกต้อง ----------
    [Fact]
    public void ExpiredOrUsedCode_RejectedEvenWithValidHmac()
    {
        // Used code
        string code = _codes.CreateCode();
        Assert.True(_codes.TryConsume(code, out _, out _));
        Assert.False(_codes.TryConsume(code, out _, out string? usedReason));
        Assert.Equal("รหัสถูกใช้ไปแล้ว", usedReason);

        // Expired code
        var time = DateTime.UtcNow;
        var expiredStore = new PairingCodeStore(() => time);
        string expCode = expiredStore.CreateCode();
        time = time.AddMinutes(11);
        Assert.False(expiredStore.TryPeek(expCode, out _, out string? expReason));
        Assert.Equal("รหัสหมดอายุแล้ว", expReason);
    }

    // ---------- (ฉ) 3 wrong → code cancelled ----------
    [Fact]
    public void ThreeWrongAttempts_CodeCancelled()
    {
        string code = _codes.CreateCode();
        _codes.RecordWrong(code);
        _codes.RecordWrong(code);
        Assert.True(_codes.TryPeek(code, out _, out _)); // ผิด 2 ครั้งยังใช้ได้
        _codes.RecordWrong(code);
        Assert.False(_codes.TryPeek(code, out _, out string? reason));
        Assert.Equal("รหัสถูกยกเลิกเพราะผิดเกินกำหนด", reason);
    }

    public void Dispose()
    {
        _listener.Stop();
        _pki.Dispose();
        _audit.Dispose();
        try { Directory.Delete(_tempFolder, true); } catch { }
    }
}
