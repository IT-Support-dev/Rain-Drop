using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>ฝั่ง Agent สำหรับ enrollment ด้วย pairing code (C2b)</summary>
internal static class EnrollmentClient
{
    /// <summary>รัน enrollment แบบ pairing code: อ่านรหัสจาก stdin แล้วเชื่อมต่อ Master พอร์ตแยก</summary>
    public static bool Run(string folder, string masterAddress, out string error)
    {
        error = "";
        try
        {
            // --- อ่านรหัสจาก stdin (บรรทัดเดียว) ไม่รับจาก argument กันหลุดลง log ---
            string? codeLine = Console.In.ReadLine();
            string code = codeLine?.Trim() ?? "";
            if (string.IsNullOrEmpty(code))
            {
                // Fallback: environment variable of the child process only (not command line, not installer log)
                code = Environment.GetEnvironmentVariable("RAINDROP_PAIRING_CODE")?.Trim() ?? "";
            }
            if (string.IsNullOrEmpty(code))
            {
                error = "ไม่พบ pairing code ใน stdin หรือ environment variable RAINDROP_PAIRING_CODE (ส่งผ่าน stdin หรือ env ของโปรเซสลูกเท่านั้น ห้ามอยู่บน command line)";
                return false;
            }

            // แยก host:port จาก master address
            var (host, port) = ParseMasterAddress(masterAddress);
            int enrollmentPort = RepoCertificates.EnrollmentPort; // 11401 แยกจาก control channel

            // --- สร้างคู่กุญแจ P-256 บนเครื่องก่อน (private key ไม่เคยออกจากเครื่องนี้) ---
            using var agentKey = ECDsa.Create(ECCurve.CreateNamedCurve(ECCurve.NamedCurves.nistP256));
            var csrReq = BuildCsr(agentKey, Environment.MachineName, new[] { host });

            // --- เชื่อมต่อ Master พอร์ต enrollment ด้วย TLS (ไม่ pin CA, ต้องไม่ใช่ TLS เปล่าๆ) ---
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(host, enrollmentPort);
            using var ssl = new SslStream(tcp.GetStream(), false, ValidateServerCert);
            var clientOpts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // Agent ยังไม่มี CA ให้ pin
            };
            await ssl.AuthenticateAsClientAsync(clientOpts).ConfigureAwait(false);

            // Channel binding: SHA-256 ของ public key ของ cert ที่ Master โชว์ใน TLS
            byte[] channelBinding;
            using (var serverKey = (ECDsa)ssl.RemoteCertificate.GetECDsaPublicKey())
                channelBinding = SHA256.HashData(serverKey.ExportSubjectPublicKeyInfo());

            // K = HKDF-SHA256(ikm=codeBytes, salt=channelBinding, info="RainDrop-enroll-v1")
            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
            CryptographicOperations.ZeroMemory(codeBytes);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);

            // --- ส่ง CODE ---
            string codeLineOut = $"CODE {code}";
            await WriteLineAsync(ssl, codeLineOut);

            // --- รับ NONCE ---
            string? nonceLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(nonceLine) || !nonceLine.StartsWith("NONCE ", StringComparison.Ordinal))
            {
                error = "Master ไม่ตอบกลับ NONCE ที่ถูกต้อง";
                return false;
            }
            byte[] nonceM = Convert.FromHexString(nonceLine[6..].Trim());

            // --- ส่ง PROOF ของ Agent ---
            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            string agentLine = $"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}";
            await WriteLineAsync(ssl, agentLine);

            // --- รับ PROOF ของ Master และตรวจ ---
            string? masterProofLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(masterProofLine) || !masterProofLine.StartsWith("PROOF ", StringComparison.Ordinal))
            {
                error = "Master ส่งพิสูจน์ตัวตนไม่ถูกต้อง";
                return false;
            }
            byte[] masterProof = Convert.FromHexString(masterProofLine[6..].Trim());
            byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));
            byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);
            if (!EnrollmentChannel.VerifyProof(expectedMaster, masterProof))
            {
                error = "พิสูจน์ตัวตนของ Master ไม่ตรงกัน (อาจมี MITM)";
                return false;
            }

            // --- ส่ง CSR ---
            byte[] csrDer;
            using (var ms = new System.IO.MemoryStream())
            {
                csrReq.Export(ms);
                csrDer = ms.ToArray();
            }
            await WriteLineAsync(ssl, $"CSR {Convert.ToBase64String(csrDer)}");

            // ส่ง machine name และ public key SPKI แยกต่างหาก (Master ต้องการสร้าง CertificateRequest โดยไม่ต้องพาร์ส CSR)
            await WriteLineAsync(ssl, $"MACHINE {Environment.MachineName}");
            await WriteLineAsync(ssl, $"PUBKEY {Convert.ToBase64String(agentKey.ExportSubjectPublicKeyInfo())}");

            // --- รับ ISSUED + CA ---
            string? issuedLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(issuedLine) || !issuedLine.StartsWith("ISSUED ", StringComparison.Ordinal))
            {
                error = "Master ไม่ออกใบรับรอง: " + issuedLine;
                return false;
            }
            byte[] agentCertDer = Convert.FromBase64String(issuedLine[7..].Trim());

            string? caLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(caLine) || !caLine.StartsWith("CA ", StringComparison.Ordinal))
            {
                error = "Master ไม่ส่ง CA public cert";
                return false;
            }
            byte[] caCertDer = Convert.FromBase64String(caLine[3..].Trim());

            // --- เก็บไฟล์ลง private\ ด้วย ACL แบบ C1 (SYSTEM + admins เท่านั้น) ---
            Directory.CreateDirectory(folder);
            string privateDir = Path.Combine(folder, "private");
            Directory.CreateDirectory(privateDir);
            AgentConfig.HardenFolder(folder, usersRead: true);
            AgentConfig.HardenFolder(privateDir, usersRead: false);

            File.WriteAllBytes(Path.Combine(privateDir, "agent-cert.der"), agentCertDer);
            File.WriteAllBytes(Path.Combine(privateDir, "agent-key.pkcs8"), agentKey.ExportPkcs8PrivateKey());
            File.WriteAllBytes(Path.Combine(privateDir, "ca-cert.der"), caCertDer);

            // agent.json ชี้ control channel ไปยัง Master
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(folder, "agent.json"),
                System.Text.Json.JsonSerializer.Serialize(new AgentConfig { Port = port, HubAddress = masterAddress }, options));

            // รหัสไม่ถูกเก็บไว้ในเครื่องหลังจากนี้ — อ่านจาก stdin แล้วทิ้งไปทันที
            CryptographicOperations.ZeroMemory(k);
            Environment.SetEnvironmentVariable("RAINDROP_PAIRING_CODE", null);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static (string Host, int Port) ParseMasterAddress(string masterAddress)
    {
        int idx = masterAddress.LastIndexOf(':');
        if (idx > 0 && int.TryParse(masterAddress[(idx + 1)..], out int port))
            return (masterAddress[..idx], port);
        return (masterAddress, Protocol.DefaultPort);
    }

    private static CertificateRequest BuildCsr(ECDsa key, string machineName, string[] addresses)
    {
        var subject = new X500DistinguishedName($"CN={machineName}");
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        // SAN
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        foreach (string address in addresses)
        {
            if (System.Net.IPAddress.TryParse(address, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(address);
        }
        req.CertificateExtensions.Add(san.Build());

        // BasicConstraints: ไม่ใช่ CA, critical
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // KeyUsage: DigitalSignature, critical
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        // EKU: ServerAuth, critical (control channel uses ServerAuth for AgentServer role)
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(RepoCertificates.ServerAuthOid) }, true));
        // SKI
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(key, false));

        return req;
    }

    private static bool ValidateServerCert(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
    {
        // Agent ยังไม่มี CA ของ Master ให้ pin — ยอมรับทุก cert แล้วพึ่ง channel binding แทน
        // (ไม่ยอมให้ TLS เปล่าๆ ผ่านโดยไม่มีการผูกกับรหัส)
        return true;
    }

    private static async Task WriteLineAsync(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, 0, bytes.Length);
    }
}
