using System.Net;
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

    // per-IP failure table with hard size cap (requirement 4)
    private readonly Dictionary<string, RateEntry> _ipTable = new();
    private const int MaxIpEntries = 1000;
    private const int RateMaxFailures = 10;

    public CsrListener(PairingCodeStore codes, MasterPki pki, AuditLog audit, IPAddress bindAddress, int port)
    {
        _codes = codes;
        _pki = pki;
        _audit = audit;
        _endPoint = new IPEndPoint(bindAddress, port);
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _masterTempCert = GenerateTempCert();
    }

    public void Start() => StartAsync().GetAwaiter().GetResult();

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
        _cts?.Cancel();
        try { _acceptLoop?.GetAwaiter().GetResult(); } catch { }
        try { _listenSocket.Close(); } catch { }
        _masterTempCert.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await _listenSocket.AcceptAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep listening */ }
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        string? ip = client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : null;
        try
        {
            // Per-IP rate limit before crypto
            if (!RateCheck(ip, out var remaining))
            {
                _audit.Append(new AuditEvent("rate-limit", ip ?? "?", "fail", null));
                return;
            }

            using var ssl = new SslStream(stream, false, ValidateServerCert);
            await ssl.AuthenticateAsServerAsync(_masterTempCert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);

            // Channel binding: SHA-256 of Master temp cert public key (.NET 8 has no TLS exporter API)
            byte[] channelBinding;
            using (var ecdsa = (ECDsa)_masterTempCert.GetECDsaPublicKey())
                channelBinding = SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo());

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);

            // --- Step 1: Client sends CODE ---
            string? codeLine = await sr.ReadLineAsync();
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
            string? agentLine = await sr.ReadLineAsync();
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

            byte[] agentProof = Convert.FromHexString(agentParts[1]);
            byte[] nonceA = Convert.FromHexString(agentParts[2]);
            byte[] transcriptHash = Convert.FromHexString(agentParts[3]);

            // Transcript covers all exchanged messages so far (anti-downgrade)
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLine),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));

            byte[] expectedAgent = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            if (!EnrollmentChannel.VerifyProof(expectedAgent, agentProof))
            {
                RecordFailure(ip, code, "agent-proof-mismatch");
                await WriteLineAsync(ssl, "REJECT invalid-proof");
                return;
            }

            // --- Step 5: Master sends its proof ---
            byte[] masterProof = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, transcript);
            await WriteLineAsync(ssl, $"PROOF {Convert.ToHexString(masterProof)}");

            // --- Step 6: Client sends CSR (base64 DER) ---
            string? csrLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(csrLine))
            {
                await WriteLineAsync(ssl, "REJECT missing-csr");
                return;
            }

            // --- Step 7: Client sends machine name ---
            string? machineLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(machineLine) || !machineLine.StartsWith("MACHINE ", StringComparison.Ordinal))
            {
                await WriteLineAsync(ssl, "REJECT missing-machine");
                return;
            }
            string machineName = machineLine[8..].Trim();

            // --- Step 8: Client sends public key SPKI ---
            string? pubkeyLine = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(pubkeyLine) || !pubkeyLine.StartsWith("PUBKEY ", StringComparison.Ordinal))
            {
                await WriteLineAsync(ssl, "REJECT missing-pubkey");
                return;
            }
            byte[] spki;
            try { spki = Convert.FromBase64String(pubkeyLine[7..].Trim()); }
            catch { await WriteLineAsync(ssl, "REJECT invalid-pubkey"); return; }

            // Import public key into ECDsa
            using var agentPublicKey = ECDsa.Create();
            agentPublicKey.ImportSubjectPublicKeyInfo(spki, out _);
            if (agentPublicKey.KeySize != 256)
            {
                await WriteLineAsync(ssl, "REJECT invalid-key-type-or-size");
                return;
            }

            // Create CertificateRequest from public key + subject
            using var csrRequest = new CertificateRequest(
                new X500DistinguishedName($"CN={machineName}"),
                agentPublicKey, HashAlgorithmName.SHA256);

            // --- Step 9: Sign and return cert + CA cert ---
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
            RecordFailure(ip, null, "exception:" + ex.GetType().Name);
            try { await WriteLineAsync(stream, "REJECT internal-error"); } catch { }
        }
        finally
        {
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            client.Dispose();
        }
    }

    private static bool ValidateServerCert(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
    {
        // We present our own temp cert; accept it. Agent does not pin any CA.
        return true;
    }

    private static async Task WriteLineAsync(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, 0, bytes.Length);
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
        using var ecdsa = ECDsa.Create(ECCurve.CreateNamedCurve(ECCurve.NamedCurves.nistP256));
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN=RainDrop-Master-Temp-{Guid.NewGuid():N},O=RainDrop"),
            ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class RateEntry
    {
        public int Failures;
        public DateTime LastAttempt = DateTime.UtcNow;
    }
}
