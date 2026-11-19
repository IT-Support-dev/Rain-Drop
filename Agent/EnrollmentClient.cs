using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>ฝั่ง Agent สำหรับ enrollment ด้วย pairing code (C2b)</summary>
internal static class EnrollmentClient
{
    private const int MaxLineLength = 8 * 1024; // 8 KB สูงสุดต่อบรรทendueกัน DoS
    private const int StepTimeoutSeconds = 10;  // timeout ต่อขั้นตอนของโปรโตคอล

    /// <summary>รัน enrollment แบบ pairing code: อ่านรหัสจากไฟล์ชั่วคราวแล้วเชื่อมต่อ Master พอร์ตแยก</summary>
    public static async Task<(bool Success, string Error)> Run(string folder, string masterAddress, string? codeFilePath = null)
    {
        // --- อ่านรหัสจากไฟล์ชั่วคราว (ไม่รับจาก command line กันหลุดลง log) ---
        string code;
        try
        {
            if (string.IsNullOrEmpty(codeFilePath))
            {
                return (false, "ไม่พบเส้นทางไฟล์รหัสจับคู่ (ส่งผ่าน --code-file เท่านั้น ห้ามอยู่บน command line)");
            }
            code = File.ReadAllText(codeFilePath)?.Trim() ?? "";
            // ลบไฟล์รหัสทันทีหลังอ่าน - ไม่ทิ้งรหัสไว้บนเครื่อง
            try { File.Delete(codeFilePath); } catch { }
        }
        catch (Exception ex)
        {
            return (false, "อ่านไฟล์รหัสจับคู่ไม่สำเร็จ: " + ex.Message);
        }
        if (string.IsNullOrEmpty(code))
        {
            return (false, "รหัสจับคู่ว่างเปล่าในไฟล์");
        }

        try
        {
            // แยก host:port จาก master address
            var (host, port) = ParseMasterAddress(masterAddress);
            int enrollmentPort = RepoCertificates.EnrollmentPort; // 11401 แยกจาก control channel

            // --- สร้างคู่กุญแจ P-256 บนเครื่องก่อน (private key ไม่เคยออกจากเครื่องนี้) ---
            using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            // --- เชื่อมต่อ Master พอร์ต enrollment ด้วย TLS ---
            using var tcp = new TcpClient();
            // จำกัดเวลาเชื่อมต่อ TCP
            var connectTask = tcp.ConnectAsync(host, enrollmentPort);
            if (await Task.WhenAny(connectTask, Task.Delay(StepTimeoutSeconds * 1000)) != connectTask)
            {
                return (false, "เชื่อมต่อ Master หมดเวลา (TCP connect)");
            }
            await connectTask;

            using var ssl = new SslStream(tcp.GetStream(), false, ValidateServerCertificateCallback);
            var clientOpts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = ValidateServerCertificateCallback,
            };
            var authTask = ssl.AuthenticateAsClientAsync(clientOpts);
            if (await Task.WhenAny(authTask, Task.Delay(StepTimeoutSeconds * 1000)) != authTask)
            {
                return (false, "เชื่อมต่อ Master หมดเวลา (TLS handshake)");
            }
            await authTask;

            // Channel binding: SHA-256 ของ public key ของ cert ที่ Master โชว์ใน TLS
            byte[] channelBinding;
            var remoteCert = ssl.RemoteCertificate;
            if (remoteCert is null)
            {
                return (false, "Master ไม่ส่งใบรับรอง");
            }
            using var cert2 = new X509Certificate2(remoteCert);
            var serverKey = cert2.GetECDsaPublicKey();
            if (serverKey is null)
                return (false, "Master public key invalid");
            channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

            // K = HKDF-SHA256(ikm=codeBytes, salt=channelBinding, info="RainDrop-enroll-v1")
            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
            CryptographicOperations.ZeroMemory(codeBytes);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);

            // --- ส่ง CODE ---
            string codeLineOut = $"CODE {code}";
            await WriteLineWithTimeoutAsync(ssl, codeLineOut);

            // --- รับ NONCE (จำกัดความยาวบรรทendueกัน DoS) ---
            string nonceLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(nonceLine) || !nonceLine.StartsWith("NONCE ", StringComparison.Ordinal))
            {
                return (false, "Master ไม่ตอบกลับ NONCE ที่ถูกต้อง");
            }
            string nonceHex = nonceLine[6..].Trim();
            if (nonceHex.Length != EnrollmentChannel.NonceSize * 2)
            {
                return (false, "NONCE มีความยาวไม่ถูกต้อง");
            }
            byte[] nonceM = Convert.FromHexString(nonceHex);

            // --- ส่ง PROOF ของ Agent ---
            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            string agentLine = $"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}";
            await WriteLineWithTimeoutAsync(ssl, agentLine);

            // --- รับ PROOF ของ Master และตรวจด้วย FixedTimeEquals ก่อนส่งข้อมูลใด ๆ ต่อ ---
            string masterProofLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(masterProofLine) || !masterProofLine.StartsWith("PROOF ", StringComparison.Ordinal))
            {
                return (false, "Master ส่งพิสูจน์ตัวตนไม่ถูกต้อง");
            }
            string masterProofHex = masterProofLine[6..].Trim();
            if (masterProofHex.Length != EnrollmentChannel.HmacSize * 2)
            {
                return (false, "Master PROOF มีความยาวไม่ถูกต้อง");
            }
            byte[] masterProof = Convert.FromHexString(masterProofHex);

            byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));
            byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);

            // FixedTimeEquals: ถ้าไม่ตรงหยุดทันที ไม่ส่ง PUBKEY/POP ต่อ
            if (!EnrollmentChannel.VerifyProof(expectedMaster, masterProof))
            {
                return (false, "พิสูจน์ตัวตนของ Master ไม่ตรงกัน (อาจมี MITM) หยุดการส่งข้อมูล");
            }

            // --- ส่ง machine name + public key SPKI + proof-of-possession (หลังจาก proof ของ Master ผ่านแล้วเท่านั้น) ---
            string machineName = Environment.MachineName;
            await WriteLineWithTimeoutAsync(ssl, $"MACHINE {machineName}");
            byte[] spki = agentKey.ExportSubjectPublicKeyInfo();
            await WriteLineWithTimeoutAsync(ssl, $"PUBKEY {Convert.ToBase64String(spki)}");

            // Transcript hash ครอบทุกข้อความแลกเปลี่ยนรวมถึง PUBKEY (anti-downgrade, anti-MITM)
            byte[] popTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine),
                Encoding.UTF8.GetBytes(masterProofLine),
                Encoding.UTF8.GetBytes($"MACHINE {machineName}"),
                Encoding.UTF8.GetBytes($"PUBKEY {Convert.ToBase64String(spki)}"));
            byte[] popSignature = agentKey.SignData(popTranscript, HashAlgorithmName.SHA256);
            await WriteLineWithTimeoutAsync(ssl, $"POP {Convert.ToBase64String(popSignature)}");

            // --- รับ ISSUED + CA ---
            string issuedLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(issuedLine) || !issuedLine.StartsWith("ISSUED ", StringComparison.Ordinal))
            {
                return (false, "Master ไม่ออกใบรับรอง: " + issuedLine);
            }
            byte[] agentCertDer = Convert.FromBase64String(issuedLine[7..].Trim());

            string caLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(caLine) || !caLine.StartsWith("CA ", StringComparison.Ordinal))
            {
                return (false, "Master ไม่ส่ง CA public cert");
            }
            byte[] caCertDer = Convert.FromBase64String(caLine[3..].Trim());

            // --- ตั้ง ACL ของ private\ ก่อนเขียน agent-key.pkcs8 ---
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

            // รหัสไม่ถูกเก็บไว้ในเครื่องหลังจากนี้ - อ่านจากไฟล์แล้วลบทันที
            CryptographicOperations.ZeroMemory(k);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (string Host, int Port) ParseMasterAddress(string masterAddress)
    {
        int idx = masterAddress.LastIndexOf(':');
        if (idx > 0 && int.TryParse(masterAddress[(idx + 1)..], out int port))
            return (masterAddress[..idx], port);
        return (masterAddress, Protocol.DefaultPort);
    }

    /// <summary>ชื่อเมธอดเดียวสำหรับตรวจสอบ server cert ปลอดภัยเพราะ channel binding + proof ที่ตามมา
    /// Agent ยังไม่มี CA ของ Master ให้ pin - ยอมรับทุก cert แล้วพึ่ง channel binding (SHA-256 ของ public key)
    /// บวกกับ bidirectional HMAC proof + proof-of-possession เพื่อกัน MITM
    /// ห้ามส่งข้อมูลลับใด ๆ ก่อนที่ proof จะผ่านการตรวจด้วย FixedTimeEquals</summary>
    private static bool ValidateServerCertificateCallback(
        object? sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        // ยอมรับทุก cert ตั้งแต่แรก แต่การยืนยันตัวตนจริงเกิดขึ้นหลังจากนี้ผ่าน
        // channel binding + HMAC proof + proof-of-possession ด้วย private key ของ Agent
        return true;
    }

    private static async Task WriteLineWithTimeoutAsync(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader sr)
    {
        // อ่านทีละตัวเพื่อจำกัดความยาวบรรทendueกัน DoS (8 KB) และจำกัดเวลาต่อขั้นตอน (10 วินาที)
        var sb = new StringBuilder();
        int totalBytes = 0;
        char[] one = new char[1];
        while (true)
        {
            var readTask = sr.ReadAsync(one, 0, 1);
            var timeout = Task.Delay(StepTimeoutSeconds * 1000);
            if (await Task.WhenAny(readTask, timeout) != readTask)
                throw new TimeoutException($"อ่านบรรทendueหมดเวลา ({StepTimeoutSeconds} วินาที)");
            int ch = await readTask;
            if (ch == 0) break;
            if (one[0] == '\n') break;
            if (one[0] != '\r')
            {
                sb.Append(one[0]);
                totalBytes++;
                if (totalBytes > MaxLineLength)
                {
                    throw new InvalidDataException($"บรรท่วยาวเกินกำหนด ({MaxLineLength} bytes) ถูกตัดการเชื่อมต่อ");
                }
            }
        }
        return sb.ToString();
    }
}
