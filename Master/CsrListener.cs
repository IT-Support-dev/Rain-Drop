using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// Enrollment listener on Master port 11401 (separate from control channel port 11400).
/// Open only while there is an unexpired pairing code; closes itself when all codes expire.
/// TLS uses a Master temporary cert. The Agent side has no CA to pin, so plain TLS is forbidden:
/// authentication is bound to the pairing code via channel binding (SHA-256 of the Master temp
/// cert public key as .NET 8 fallback) plus bidirectional HMAC proofs. The code itself is NEVER
/// sent outside TLS and NEVER logged.
/// </summary>
internal sealed class CsrListener : IDisposable
{
    private const int MaxLineLength = 8 * 1024;   // 8 KB สูงสุดต่อบรรทendueกัน DoS
    private const int StepTimeoutSeconds = 10;     // timeout ต่อขั้นตอน
    private const int MaxConcurrentConnections = 8; // จำกัดการเชื่อมต่อพร้อมกัน

    private readonly PairingCodeStore _codes;
    private readonly MasterPki _pki;
    private readonly AuditLog _audit;
    private readonly IPEndPoint _endPoint;
    private readonly X509Certificate2 _masterTempCert;
    private readonly Socket _listenSocket;
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private CancellationTokenSource? _cts;
    private bool _running;
    private int _activeConnections;

    // per-IP failure table with hard size cap (requirement 4)
    private readonly Dictionary<string, RateEntry> _ipTable = new();
    private const int MaxIpEntries = 1000;
    private const int RateMaxFailures = 10;

    /// <summary>Hook ภายในสำหรับเทสต์ assert ว่าไม่มี exception ระหว่าง TLS handshake</summary>
    internal event Action<Exception>? ExceptionOccurred;

    public CsrListener(PairingCodeStore codes, MasterPki pki, AuditLog audit, IPAddress bindAddress, int port)
    {
        _codes = codes;
        _pki = pki;
        _audit = audit;
        _endPoint = new IPEndPoint(bindAddress, port);
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _masterTempCert = GenerateTempCert();
    }

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    private async Task StartAsync()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }
        _cts = new CancellationTokenSource();
        _listenSocket.Bind(_endPoint);
        _listenSocket.Listen(16);
        _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }

        // ยกเลิก accept loop ก่อน แล้วค่อยรอให้จบ ถึงจะ dispose cert
        _cts?.Cancel();
        if (_acceptLoop is not null)
        {
            try { _acceptLoop.GetAwaiter().GetResult(); }
            catch { }
        }

        try { _listenSocket.Close(); } catch { }
        // Dispose cert หลังจาก accept loop จบแล้ว (กัน object disposed ขณะยังมีการเชื่อมต่อ)
        _masterTempCert.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync()
    {
        var cts = _cts;
        if (cts is null) return;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                // จำกัดการเชื่อมต่อพร้อมกัน
                while (_activeConnections >= MaxConcurrentConnections)
                {
                    await Task.Delay(100, cts.Token);
                }

                var client = await _listenSocket.AcceptAsync(cts.Token);
                if (client is null) continue;
                Interlocked.Increment(ref _activeConnections);
                _ = Task.Run(() => HandleClientAsync(client), cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep listening */ }
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            string? ip = client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : null;
            try
            {
                // Per-IP rate limit before crypto
                if (!RateCheck(ip, out var remaining))
                {
                    _audit.Append(new AuditEvent("rate-limit", ip ?? "?", "fail", ""));
                    return;
                }

                using var ssl = new SslStream(stream, false, ValidateServerCertificateCallback);
                var authTask = ssl.AuthenticateAsServerAsync(_masterTempCert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                if (await Task.WhenAny(authTask, Task.Delay(StepTimeoutSeconds * 1000)) != authTask)
                {
                    RecordFailure(ip, null, "tls-timeout");
                    return;
                }
                await authTask;

                // Channel binding: SHA-256 of Master temp cert public key (.NET 8 has no TLS exporter API)
                byte[] channelBinding;
                var serverKey = _masterTempCert.GetECDsaPublicKey();
                if (serverKey is null)
                {
                    // ยังไม่ได้อ่านรหัสจับคู่เลย จึงส่ง code = null (ไม่มีรหัสให้ถือว่าผิดในจุดนี้)
                    RecordFailure(ip, null, "master-public-key-invalid");
                    await WriteLineAsync(ssl, "REJECT invalid-master-key");
                    return;
                }
                using (serverKey)
                    channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

                using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);

                // --- Step 1: Client sends CODE ---
                string codeLine = await ReadLineWithTimeoutAsync(sr);
                if (string.IsNullOrWhiteSpace(codeLine)) return;
                var parts = codeLine.Trim().Split(' ', 2);
                if (parts.Length < 2 || parts[0] != "CODE")
                {
                    await WriteLineAsync(ssl, "REJECT expected-code");
                    return;
                }
                string code = parts[1].Trim();

                // Peek (non-consuming) validity check
                if (!_codes.TryPeek(code, out var codeBytes, out var peekReason))
                {
                    RecordFailure(ip, code, "code-invalid:" + peekReason);
                    await WriteLineAsync(ssl, "REJECT invalid-or-expired-code");
                    return;
                }

                // --- Step 2: Derive K = HKDF-SHA256(ikm=codeBytes, salt=channelBinding, info="RainDrop-enroll-v1") ---
                byte[] k = EnrollmentChannel.HkdfSha256(codeBytes!, channelBinding, EnrollmentChannel.Info, 32);

                // --- Step 3: Master sends NONCE ---
                byte[] nonceM = EnrollmentChannel.NewNonce();
                string nonceLine = $"NONCE {Convert.ToHexString(nonceM)}";
                await WriteLineAsync(ssl, nonceLine);

                // --- Step 4: Client sends PROOF ---
                string agentLine = await ReadLineWithTimeoutAsync(sr);
                if (string.IsNullOrWhiteSpace(agentLine))
                {
                    RecordFailure(ip, code, "missing-agent-proof");
                    await WriteLineAsync(ssl, "REJECT missing-proof");
                    return;
                }

                var agentParts = agentLine.Trim().Split(' ');
                if (agentParts.Length < 4 || agentParts[0] != "PROOF")
                {
                    RecordFailure(ip, code, "malformed-agent-proof");
                    await WriteLineAsync(ssl, "REJECT malformed-proof");
                    return;
                }

                string agentProofHex = agentParts[1];
                string nonceAHex = agentParts[2];
                string transcriptHex = agentParts[3];

                // ตรวจความยาว hex ที่คาดไว้ก่อน FromHexString
                if (agentProofHex.Length != EnrollmentChannel.HmacSize * 2 ||
                    nonceAHex.Length != EnrollmentChannel.NonceSize * 2 ||
                    transcriptHex.Length != 32 * 2)
                {
                    RecordFailure(ip, code, "hex-length-invalid");
                    await WriteLineAsync(ssl, "REJECT invalid-hex-length");
                    return;
                }

                byte[] agentProof = Convert.FromHexString(agentProofHex);
                byte[] nonceA = Convert.FromHexString(nonceAHex);
                byte[] transcriptHash = Convert.FromHexString(transcriptHex);

                // Transcript สำหรับตรวจ agent proof: รวมเฉพาะข้อความที่แลกเปลี่ยนก่อน PROOF ของ agent
                // (CODE + NONCE เท่านั้น ไม่รวม agentLine เอง เพราะยังไม่ถูกส่งตอนคำนวณ proof)
                byte[] agentTranscript = EnrollmentChannel.TranscriptHash(
                    Encoding.UTF8.GetBytes(codeLine),
                    Encoding.UTF8.GetBytes(nonceLine));

                byte[] expectedAgent = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, agentTranscript);
                if (!EnrollmentChannel.VerifyProof(expectedAgent, agentProof))
                {
                    RecordFailure(ip, code, "agent-proof-mismatch");
                    await WriteLineAsync(ssl, "REJECT invalid-proof");
                    return;
                }

                // --- Step 5: Master sends its proof ---
                // Transcript สำหรับ master proof: รวมทุกข้อความจนถึง agentLine
                byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                    Encoding.UTF8.GetBytes(codeLine),
                    Encoding.UTF8.GetBytes(nonceLine),
                    Encoding.UTF8.GetBytes(agentLine));

                byte[] masterProof = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);
                await WriteLineAsync(ssl, $"PROOF {Convert.ToHexString(masterProof)}");

                // --- Step 6: Client sends machine name ---
                string machineLine = await ReadLineWithTimeoutAsync(sr);
                if (string.IsNullOrWhiteSpace(machineLine) || !machineLine.StartsWith("MACHINE ", StringComparison.Ordinal))
                {
                    await WriteLineAsync(ssl, "REJECT missing-machine");
                    return;
                }
                string machineName = machineLine[8..].Trim();

                // --- Step 7: Client sends public key SPKI ---
                string pubkeyLine = await ReadLineWithTimeoutAsync(sr);
                if (string.IsNullOrWhiteSpace(pubkeyLine) || !pubkeyLine.StartsWith("PUBKEY ", StringComparison.Ordinal))
                {
                    await WriteLineAsync(ssl, "REJECT missing-pubkey");
                    return;
                }
                byte[] spki;
                try { spki = Convert.FromBase64String(pubkeyLine[7..].Trim()); }
                catch { await WriteLineAsync(ssl, "REJECT invalid-pubkey"); return; }

                // --- Step 8: Client sends proof-of-possession ---
                string popLine = await ReadLineWithTimeoutAsync(sr);
                if (string.IsNullOrWhiteSpace(popLine) || !popLine.StartsWith("POP ", StringComparison.Ordinal))
                {
                    await WriteLineAsync(ssl, "REJECT missing-pop");
                    return;
                }
                byte[] popSignature;
                try { popSignature = Convert.FromBase64String(popLine[4..].Trim()); }
                catch { await WriteLineAsync(ssl, "REJECT invalid-pop"); return; }

                // Import public key และตรวจ proof-of-possession ก่อนเซ็น cert
                using var agentPublicKey = ECDsa.Create();
                try
                {
                    agentPublicKey.ImportSubjectPublicKeyInfo(spki, out _);
                }
                catch
                {
                    await WriteLineAsync(ssl, "REJECT invalid-pubkey");
                    return;
                }

                // Transcript hash ครอบทุกข้อความจนถึง PUBKEY
                byte[] popTranscript = EnrollmentChannel.TranscriptHash(
                    Encoding.UTF8.GetBytes(codeLine),
                    Encoding.UTF8.GetBytes(nonceLine),
                    Encoding.UTF8.GetBytes(agentLine),
                    Encoding.UTF8.GetBytes($"PROOF {Convert.ToHexString(masterProof)}"),
                    Encoding.UTF8.GetBytes(machineLine),
                    Encoding.UTF8.GetBytes(pubkeyLine));

                if (!agentPublicKey.VerifyData(popTranscript, popSignature, HashAlgorithmName.SHA256))
                {
                    RecordFailure(ip, code, "pop-mismatch");
                    await WriteLineAsync(ssl, "REJECT invalid-proof-of-possession");
                    return;
                }

                // ตรวจว่ากุญแจเป็น ECDSA P-256
                if (agentPublicKey.KeySize != 256)
                {
                    await WriteLineAsync(ssl, "REJECT invalid-key-type-or-size");
                    return;
                }

                // Create CertificateRequest from public key + subject
                var csrRequest = new CertificateRequest(
                    new X500DistinguishedName($"CN={machineName}"),
                    agentPublicKey, HashAlgorithmName.SHA256);

                // Sign and return cert + CA cert
                string machineId = Guid.NewGuid().ToString("N");
                string serial; DateTime notAfter;
                var issued = _pki.SignAgentCsr(csrRequest, machineId, machineName,
                    new[] { ip ?? "127.0.0.1" }, out serial, out notAfter);

                await WriteLineAsync(ssl, $"ISSUED {Convert.ToBase64String(issued.RawData)}");
                await WriteLineAsync(ssl, $"CA {Convert.ToBase64String(_pki.CaCertificate.RawData)}");

                // Code consumed only after successful enrollment
                _codes.TryConsume(code, out _, out _);
                _audit.Append(new AuditEvent("enroll", ip ?? "?", "ok", serial));
            }
            catch (Exception ex)
            {
                // InvalidDataException คือ protocol violation ตามปกติ (เช่น ส่งบรรทุดยาวเกิน 8 KB)
                // ไม่ใช่ exception ที่ไม่คาดคิด → ไม่ raise ExceptionOccurred
                if (ex is not InvalidDataException)
                {
                    ExceptionOccurred?.Invoke(ex);
                    Diag.Error("CsrListener.HandleClientAsync", ex.ToString(), ex);
                }
                RecordFailure(ip, null, "exception:" + ex.GetType().Name);
                try { await WriteLineAsync(stream, "REJECT internal-error"); } catch { }
            }
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent("conn-error", "?", "fail", ex.GetType().Name));
        }
        finally
        {
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            client.Dispose();
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>ชื่อเมธอดเดียวสำหรับตรวจสอบ server cert ปลอดภัยเพราะ channel binding + proof ที่ตามมา
    /// Master เสนอ cert ชั่วคราวของตัวเอง Agent ไม่มี CA ให้ pin จึงยอมรับทุก cert
    /// แต่การยืนยันตัวตนจริงเกิดขึ้นผ่าน channel binding (SHA-256 ของ public key) +
    /// bidirectional HMAC proof + proof-of-possession ก่อนเซ็น cert ใด ๆ
    /// ห้ามส่งข้อมูลลับใด ๆ ก่อนที่ proof จะผ่านการตรวจด้วย FixedTimeEquals</summary>
    private static bool ValidateServerCertificateCallback(
        object? sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        // ยอมรับทุก cert ตั้งแต่แรก แต่การยืนยันตัวตนจริงเกิดขึ้นหลังจากนี้ผ่าน
        // channel binding + HMAC proof + proof-of-possession
        return true;
    }

    private static async Task WriteLineAsync(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, 0, bytes.Length);
        await s.FlushAsync(); // กันข้อมูลค้างใน SslStream buffer แล้ว client ได้ unexpected EOF
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader sr)
    {
        var readTask = sr.ReadLineAsync(); // อ่านทั้งบรรทัด (SslStream.ReadAsync(char[],0,1) อาจคืน 0 ก่อนเวลาอันควรทำให้เข้าใจผิดว่า stream ปิด)
        var timeout = Task.Delay(StepTimeoutSeconds * 1000);
        if (await Task.WhenAny(readTask, timeout) != readTask)
            throw new TimeoutException($"อ่านบรรทendueหมดเวลา ({StepTimeoutSeconds} วินาที)");
        var line = await readTask;
        if (line is not null && line.Length > MaxLineLength)
            throw new InvalidDataException($"บรรท่วยาวเกินกำหนด ({MaxLineLength} bytes) ถูกตัดการเชื่อมต่อ");
        return line ?? "";
    }

    private void RecordFailure(string? ip, string? code, string reason)
    {
        if (ip is not null)
        {
            lock (_gate)
            {
                if (_ipTable.Count >= MaxIpEntries && !_ipTable.ContainsKey(ip))
                {
                    var oldest = _ipTable.OrderBy(kv => kv.Value.LastAttempt).First();
                    _ipTable.Remove(oldest.Key);
                }
                if (!_ipTable.TryGetValue(ip, out var entry)) entry = new RateEntry();
                entry.Failures++;
                entry.LastAttempt = DateTime.UtcNow;
                _ipTable[ip] = entry;
            }
        }
        if (code is not null) _codes.RecordWrong(code);
        _audit.Append(new AuditEvent("auth-fail", ip ?? "?", "fail", reason));
    }

    private bool RateCheck(string? ip, out int remaining)
    {
        remaining = 0;
        if (ip is null) return true;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var stale = _ipTable.Where(kv => kv.Value.LastAttempt < now.AddMinutes(-15)).ToList();
            foreach (var kv in stale) _ipTable.Remove(kv.Key);

            if (!_ipTable.TryGetValue(ip, out var entry)) return true;
            remaining = Math.Max(0, RateMaxFailures - entry.Failures);
            return entry.Failures < RateMaxFailures;
        }
    }

    private static X509Certificate2 GenerateTempCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN=RainDrop-Master-Temp-{Guid.NewGuid():N},O=RainDrop"),
            ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, true));

        // Export PFX ในหน่วยความจำแล้วโหลดกลับด้วย X509KeyStorageFlags.Exportable (ไม่ใช้ PersistKeySet, ไม่ลง key store)
        // วิธีเดียวกับ C2a (ControlTls.CreateSchannelCertificate) ให้ Schannel บน Windows รับ private key ได้
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        const string password = "RainDrop-Temp-Cert";
        byte[] pfx = cert.Export(X509ContentType.Pfx, password);
        try
        {
            var result = new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
            if (!result.HasPrivateKey)
                throw new CryptographicException("Master temp cert has no private key after PFX round-trip");
            return result;
        }
        finally
        {
            Array.Clear(pfx);
        }
    }

    private sealed class RateEntry
    {
        public int Failures;
        public DateTime LastAttempt = DateTime.UtcNow;
    }
}
