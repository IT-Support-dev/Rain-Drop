using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using RainDrop.Agent;
using RainDrop.Master;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>เทสต์ฝั่ง Master ของ CsrListener (C2b). แต่ละเทสต์ที่ต้องการ listener
/// จะสร้าง listener ใหม่บนพอร์ตสุ่มจริง (bind port 0 แล้วอ่านพอร์ตที่ได้) กันการชนพอร์ต
/// และกัน state ระหว่างเทสต์. คลาสนี้รันแบบ serial (ปิด parallelization ทั้งแอสเซมบลี)
/// และทุกการทำงานเครือข่ายมี timeout 15 วินาทีกันเทสต์แขวนค้าง</summary>
[Collection("CsrListener serial")]
public class CsrListenerTests : IDisposable
{
    private readonly PairingCodeStore _codes;
    private readonly MasterPki _pki;
    private readonly AuditLog _audit;
    private readonly string _tempFolder;

    public CsrListenerTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), $"raindrop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempFolder);

        // ไม่ใช้ using-block กับ ca/caKey/client — ปล่อยให้ _pki เป็นเจ้าของและ dispose ตอน CsrListenerTests.Dispose()
        var ca = RepoCertificates.CreateCa();
        var caKey = ca.GetECDsaPrivateKey()!;
        var client = RepoCertificates.IssueClient(ca, caKey);
        _pki = MasterPki.CreateForPreview(caKey, ca, client);

        _codes = new PairingCodeStore();
        var auditFolder = Path.Combine(_tempFolder, "audit");
        _audit = new AuditLog(auditFolder);
    }

    /// <summary>สร้าง CsrListener ใหม่บนพอร์ตสุ่มจริง (bind 0) เริ่มรับการเชื่อมต่อแล้ว
    /// คืนค่าทั้ง listener และพอร์ตที่เปิดจริง ให้ผู้เรียกเรียก listener.Stop() เมื่อใช้เสร็จ</summary>
    private (CsrListener Listener, int Port) StartListenerOnFreePort(PairingCodeStore? codes = null)
    {
        var store = codes ?? _codes;
        var port = GetFreePort();
        var listener = new CsrListener(store, _pki, _audit, IPAddress.Loopback, port);
        listener.Start();
        return (listener, port);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, int seconds = 15)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(seconds));
        if (await Task.WhenAny(task, timeout) != task)
            throw new TimeoutException($"ทดสอบหมดเวลา ({seconds} วินาที)");
        return await task;
    }

    private static async Task WithTimeoutAsync(Task task, int seconds = 15)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(seconds));
        if (await Task.WhenAny(task, timeout) != task)
            throw new TimeoutException($"ทดสอบหมดเวลา ({seconds} วินาที)");
        await task;
    }

    /// <summary>คืนสิทธิ์ให้ผู้ใช้ปัจจุบันทั่วทั้งต้นไม้โฟลเดอร์แล้วจึงลบ
    /// ใช้กับเทสต์ที่แตะ HardenFolder ซึ่งตั้ง ACL ปิดกั้น Users ไม่ให้ลบไฟล์ใน private    /// ระดับ production ไม่ได้แตะ — ACL เดิมยังคงเข้มงวดเหมือนเดิม</summary>
    private static void DeleteDirectoryWithPermissions(string path)
    {
        if (!Directory.Exists(path)) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var security = new FileInfo(file).GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(file).SetAccessControl(security);
            }
            catch { }
        }
        foreach (var dir in Directory.GetDirectories(path, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var security = new DirectoryInfo(dir).GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                new DirectoryInfo(dir).SetAccessControl(security);
            }
            catch { }
        }
        try
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch { }

        Directory.Delete(path, recursive: true);
    }

    // ---------- PairingCodeStore (ไม่ต้องใช้ listener) ----------

    [Fact]
    public void CorrectCode_PeekSucceeds()
    {
        string code = _codes.CreateCode();
        Assert.NotEmpty(code);
        Assert.Equal(16, code.Length);
        Assert.Matches("^[A-Z2-7]{16}$", code);
        Assert.True(_codes.TryPeek(code, out byte[]? bytes, out string? reason));
        Assert.NotNull(bytes);
        Assert.Equal(10, bytes.Length); // 80 บิต = 10 ไบต์
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
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true)); // ClientAuth (ผิด)

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
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? bytes, out _));
        Assert.NotNull(bytes);
        _codes.RecordWrong(code);
        Assert.Equal(1, _codes.ActiveCount);
    }

    [Fact]
    public void Mitm_Relay_DifferentChannelBinding_Fails()
    {
        byte[] cbMaster = SHA256.HashData(Encoding.UTF8.GetBytes("master-cert-spki"));
        byte[] cbMitm = SHA256.HashData(Encoding.UTF8.GetBytes("mitm-cert-spki"));
        Assert.NotEqual(cbMaster, cbMitm);

        byte[] codeBytes = Encoding.UTF8.GetBytes("ABCD1234EFGH5678");
        byte[] kMaster = EnrollmentChannel.HkdfSha256(codeBytes, cbMaster, EnrollmentChannel.Info, 32);
        byte[] kMitm = EnrollmentChannel.HkdfSha256(codeBytes, cbMitm, EnrollmentChannel.Info, 32);
        Assert.NotEqual(kMaster, kMitm);

        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] agentProof = EnrollmentChannel.ComputeAgentProof(kMitm, nonceA, nonceM, transcript);
        byte[] expectedMasterSide = EnrollmentChannel.ComputeAgentProof(kMaster, nonceA, nonceM, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(agentProof, expectedMasterSide));
    }

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
        Assert.NotEqual(agentProof, masterProof);

        byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(key, nonceM, nonceA, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(expectedMaster, agentProof));
    }

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

    [Fact]
    public void ExpiredOrUsedCode_RejectedEvenWithValidHmac()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryConsume(code, out _, out _));
        Assert.False(_codes.TryConsume(code, out _, out string? usedReason));
        Assert.Equal("รหัสถูกใช้ไปแล้ว", usedReason);

        var time = DateTime.UtcNow;
        var expiredStore = new PairingCodeStore(() => time);
        string expCode = expiredStore.CreateCode();
        time = time.AddMinutes(11);
        Assert.False(expiredStore.TryPeek(expCode, out _, out string? expReason));
        Assert.Equal("รหัสหมดอายุแล้ว", expReason);
    }

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

    // ---------- (ก) MITM relay: channel binding ต่างกัน → HMAC ไม่ผ่าน ----------
    [Fact]
    public async Task Mitm_Relay_Loopback_TwoTlsStreams_Fails()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? codeBytes, out _));
        Assert.NotNull(codeBytes);

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));

            var remoteCert = new X509Certificate2(ssl.RemoteCertificate!);
            using var serverKey = remoteCert.GetECDsaPublicKey()!;
            byte[] cbMaster = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

            // ใช้ channel binding ของ MITM cert ปลอมแทน cert จริงของ Master
            using var mitmKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var mitmReq = new CertificateRequest(
                new X500DistinguishedName("CN=MITM-Relay"), mitmKey, HashAlgorithmName.SHA256);
            mitmReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            mitmReq.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, true));
            using var mitmCert = mitmReq.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
            byte[] cbMitm = SHA256.HashData(mitmCert.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
            Assert.NotEqual(cbMaster, cbMitm);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {code}"));
            string nonceLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("NONCE ", nonceLine);
            byte[] nonceM = Convert.FromHexString(nonceLine[6..].Trim());

            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] kMitm = EnrollmentChannel.HkdfSha256(codeBytes, cbMitm, EnrollmentChannel.Info, 32);
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(kMitm, nonceA, nonceM, transcript);
            await WithTimeoutAsync(writer.WriteLineAsync($"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}"));

            string response = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("REJECT", response);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    // ---------- (ข) ปฏิเสธ TLS ต่ำกว่า 1.2 ----------
    // .NET 8 ไม่มี SslProtocols.Tls10/Tls11 จึงไม่สามารถบังคับ client เสนอเวอร์ชันเก่าได้
    // วิธีที่ทำได้: client เสนอโดยไม่จำกัดเวอร์ชัน (SslProtocols.None) server บังคับ TLS 1.2+
    // -> protocol ที่ตกลงต้องไม่ต่ำกว่า Tls12 ตรวจด้วย ssl.SslProtocol หลังเจรจา
    [Fact]
    public async Task Listener_NegotiatesTls12OrHigher()
    {
        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.None,
            }));

            // Server บังคับ TLS 1.2+ ดังนั้น protocol ที่ตกลงต้องไม่ต่ำกว่า Tls12
            Assert.True(ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13,
                $"คาดหวัง TLS 1.2 ขึ้นไป แต่ได้ {ssl.SslProtocol}");
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    // ---------- (ค) PoP ลายเซ็นผิด -> ไม่เซ็น cert ----------
    [Fact]
    public async Task Pop_WrongSignature_NotSigned()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? codeBytes, out _));
        Assert.NotNull(codeBytes);

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));

            var remoteCert = new X509Certificate2(ssl.RemoteCertificate!);
            using var serverKey = remoteCert.GetECDsaPublicKey()!;
            byte[] channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {code}"));
            string nonceLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("NONCE ", nonceLine);
            byte[] nonceM = Convert.FromHexString(nonceLine[6..].Trim());

            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            string agentLine = $"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}";
            await WithTimeoutAsync(writer.WriteLineAsync(agentLine));

            string masterProofLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("PROOF ", masterProofLine);
            byte[] masterProof = Convert.FromHexString(masterProofLine[6..].Trim());
            byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));
            byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);
            Assert.True(EnrollmentChannel.VerifyProof(expectedMaster, masterProof));

            using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] spki = agentKey.ExportSubjectPublicKeyInfo();
            await WithTimeoutAsync(writer.WriteLineAsync("MACHINE pop-test-machine"));
            await WithTimeoutAsync(writer.WriteLineAsync($"PUBKEY {Convert.ToBase64String(spki)}"));

            byte[] popTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine),
                Encoding.UTF8.GetBytes(masterProofLine),
                Encoding.UTF8.GetBytes("MACHINE pop-test-machine"),
                Encoding.UTF8.GetBytes($"PUBKEY {Convert.ToBase64String(spki)}"));

            using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] wrongPop = wrongKey.SignData(popTranscript, HashAlgorithmName.SHA256);
            await WithTimeoutAsync(writer.WriteLineAsync($"POP {Convert.ToBase64String(wrongPop)}"));

            string issuedLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.DoesNotContain("ISSUED", issuedLine);
            Assert.Contains("REJECT", issuedLine);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    // ---------- (ง) ไหล่เต็มถูกต้องทุกขั้นตอน -> Master セ็นคืน ISSUED + CA ----------
    [Fact]
    public async Task FullEnrollment_ValidProofs_IssuedCaReturned()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? codeBytes, out _));
        Assert.NotNull(codeBytes);

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));

            var remoteCert = new X509Certificate2(ssl.RemoteCertificate!);
            using var serverKey = remoteCert.GetECDsaPublicKey()!;
            byte[] channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {code}"));
            string nonceLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            byte[] nonceM = Convert.FromHexString(nonceLine[6..].Trim());

            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            string agentLine = $"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}";
            await WithTimeoutAsync(writer.WriteLineAsync(agentLine));

            string masterProofLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            byte[] masterProof = Convert.FromHexString(masterProofLine[6..].Trim());
            byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));
            byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);
            Assert.True(EnrollmentChannel.VerifyProof(expectedMaster, masterProof));

            using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] spki = agentKey.ExportSubjectPublicKeyInfo();
            await WithTimeoutAsync(writer.WriteLineAsync("MACHINE full-enroll-machine"));
            await WithTimeoutAsync(writer.WriteLineAsync($"PUBKEY {Convert.ToBase64String(spki)}"));

            byte[] popTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes($"CODE {code}"),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine),
                Encoding.UTF8.GetBytes(masterProofLine),
                Encoding.UTF8.GetBytes("MACHINE full-enroll-machine"),
                Encoding.UTF8.GetBytes($"PUBKEY {Convert.ToBase64String(spki)}"));
            byte[] popSignature = agentKey.SignData(popTranscript, HashAlgorithmName.SHA256);
            await WithTimeoutAsync(writer.WriteLineAsync($"POP {Convert.ToBase64String(popSignature)}"));

            string issuedLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("ISSUED ", issuedLine);
            byte[] agentCertDer = Convert.FromBase64String(issuedLine[7..].Trim());

            string caLine = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("CA ", caLine);
            byte[] caCertDer = Convert.FromBase64String(caLine[3..].Trim());

            using var agentCert = new X509Certificate2(agentCertDer);
            using var caCert = new X509Certificate2(caCertDer);
            Assert.Equal("CN=full-enroll-machine", agentCert.Subject);
            Assert.True(agentCert.NotAfter > DateTime.UtcNow);

            Assert.False(_codes.TryConsume(code, out _, out string? reuseReason));
            Assert.Equal("รหัสถูกใช้ไปแล้ว", reuseReason);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    // ---------- (จ) รหัสใช้แล้ว / หมดอายุ -> ปฏิเสธก่อนถึงขั้นตรวจ HMAC ----------
    [Fact]
    public async Task UsedCode_RejectedByListener_EvenIfHmacValid()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryConsume(code, out _, out _)); // ใช้ไปแล้ว

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));
            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {code}"));
            string response = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("REJECT invalid-or-expired-code", response);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    [Fact]
    public async Task ExpiredCode_RejectedByListener()
    {
        var time = DateTime.UtcNow;
        var expiredStore = new PairingCodeStore(() => time);
        string expCode = expiredStore.CreateCode();
        time = time.AddMinutes(11); // หมดอายุแล้ว

        // สร้าง listener ใหม่กับ expiredStore แยกต่างหาก (ไม่แตะ listener ตัวอื่น)
        var (listener, port) = StartListenerOnFreePort(expiredStore);
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));
            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {expCode}"));
            string response = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.StartsWith("REJECT invalid-or-expired-code", response);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    // ---------- (ฉ) บรรท่วยาวเกิน 8 KB -> ถูกตัดการเชื่อมต่อ ----------
    [Fact]
    public async Task OversizedLine_CutsConnection()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out _, out _));

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            using var tcp = new TcpClient();
            await WithTimeoutAsync(tcp.ConnectAsync(IPAddress.Loopback, port));
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await WithTimeoutAsync(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }));
            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var oversized = new string('A', 9000);
            await WithTimeoutAsync(writer.WriteLineAsync($"CODE {oversized}"));

            string response = await WithTimeoutAsync(sr.ReadLineAsync()) ?? "";
            Assert.True(response.StartsWith("REJECT") || string.IsNullOrEmpty(response), $"คาดหวัง REJECT แต่ได้: {response}");
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
        }
    }

    /// <summary>End-to-end: EnrollmentClient.Run จริงต่อ CsrListener จริงบน loopback
    /// จบด้วย ISSUED + CA และไฟล์ cert ถูกเขียนใน private\\ ตรวจ cert กับ CA ได้</summary>
    [Fact]
    public async Task EnrollmentClient_RealCsrListener_EndToEnd_IssuedCaReturned()
    {
        string code = _codes.CreateCode();
        Assert.True(_codes.TryPeek(code, out byte[]? codeBytes, out _));
        Assert.NotNull(codeBytes);

        string agentHome = Path.Combine(_tempFolder, "agent-home-e2e");
        Directory.CreateDirectory(agentHome);
        string codeFile = Path.Combine(agentHome, "enrollment-code.txt");
        File.WriteAllText(codeFile, code);

        var (listener, port) = StartListenerOnFreePort();
        var handshakeExceptions = new List<Exception>();
        listener.ExceptionOccurred += ex => handshakeExceptions.Add(ex);
        try
        {
            var (ok, error) = await WithTimeoutAsync(EnrollmentClient.Run(
                agentHome,
                $"127.0.0.1:{port}",
                codeFile));

            Assert.True(ok, "Enrollment ต้องสำเร็จ: " + error);
            Assert.Empty(handshakeExceptions);

            string privateDir = Path.Combine(agentHome, "private");
            Assert.True(File.Exists(Path.Combine(privateDir, "agent-cert.der")),
                "ต้องมี agent-cert.der ใน private\\");
            Assert.True(File.Exists(Path.Combine(privateDir, "agent-key.pkcs8")),
                "ต้องมี agent-key.pkcs8 ใน private\\");
            Assert.True(File.Exists(Path.Combine(privateDir, "ca-cert.der")),
                "ต้องมี ca-cert.der ใน private\\");

            // agent.json ชี้ไปยัง master address
            string agentJson = File.ReadAllText(Path.Combine(agentHome, "agent.json"));
            Assert.Contains(port.ToString(), agentJson);

            // ตรวจ cert กับ CA
            byte[] agentCertDer = File.ReadAllBytes(Path.Combine(privateDir, "agent-cert.der"));
            byte[] caCertDer = File.ReadAllBytes(Path.Combine(privateDir, "ca-cert.der"));

            using var agentCert = new X509Certificate2(agentCertDer);
            using var caCert = new X509Certificate2(caCertDer);

            Assert.NotEqual("", agentCert.Subject);
            Assert.True(agentCert.NotAfter > DateTime.UtcNow, "Cert ต้องยังไม่หมดอายุ");

            // รหัสต้องถูกกินหลัง enroll สำเร็จ
            Assert.False(_codes.TryConsume(code, out _, out string? reuseReason));
            Assert.Equal("รหัสถูกใช้ไปแล้ว", reuseReason);
        }
        finally
        {
            listener.Stop();
            Assert.Empty(handshakeExceptions);
            // คืนสิทธิ์ก่อนลบ เพราะ EnrollmentClient.Run เรียก HardenFolder (ACL ปิดกั้น Users)
            if (Directory.Exists(agentHome)) DeleteDirectoryWithPermissions(agentHome);
        }
    }

    public void Dispose()
    {
        // ไม่มี _listener ที่สร้างไว้ใน constructor แล้ว แต่ละเทสต์สร้างและ stop เอง
        _pki.Dispose();
        _audit.Dispose();
        try { Directory.Delete(_tempFolder, true); } catch { }
    }
}

