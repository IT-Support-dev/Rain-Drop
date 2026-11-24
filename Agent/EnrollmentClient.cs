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

/// <summary>à¸à¸±à¹ˆà¸‡ Agent à¸ªà¸³à¸«à¸£à¸±à¸š enrollment à¸”à¹‰à¸§à¸¢ pairing code (C2b)</summary>
internal static class EnrollmentClient
{
    private const int MaxLineLength = 8 * 1024; // 8 KB à¸ªà¸¹à¸‡à¸ªà¸¸à¸”à¸•à¹ˆà¸­à¸šà¸£à¸£à¸—endueà¸à¸±à¸™ DoS
    private const int StepTimeoutSeconds = 10;  // timeout à¸•à¹ˆà¸­à¸‚à¸±à¹‰à¸™à¸•à¸­à¸™à¸‚à¸­à¸‡à¹‚à¸›à¸£à¹‚à¸•à¸„à¸­à¸¥

    /// <summary>à¸£à¸±à¸™ enrollment à¹à¸šà¸š pairing code: à¸­à¹ˆà¸²à¸™à¸£à¸«à¸±à¸ªà¸ˆà¸²à¸à¹„à¸Ÿà¸¥à¹Œà¸Šà¸±à¹ˆà¸§à¸„à¸£à¸²à¸§à¹à¸¥à¹‰à¸§à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­ Master à¸žà¸­à¸£à¹Œà¸•à¹à¸¢à¸</summary>
    public static async Task<(bool Success, string Error)> Run(string folder, string masterAddress, string? codeFilePath = null,
        bool keepCurrentUser = false)
    {
        // --- à¸­à¹ˆà¸²à¸™à¸£à¸«à¸±à¸ªà¸ˆà¸²à¸à¹„à¸Ÿà¸¥à¹Œà¸Šà¸±à¹ˆà¸§à¸„à¸£à¸²à¸§ (à¹„à¸¡à¹ˆà¸£à¸±à¸šà¸ˆà¸²à¸ command line à¸à¸±à¸™à¸«à¸¥à¸¸à¸”à¸¥à¸‡ log) ---
        string code;
        try
        {
            if (string.IsNullOrEmpty(codeFilePath))
            {
                return (false, "à¹„à¸¡à¹ˆà¸žà¸šà¹€à¸ªà¹‰à¸™à¸—à¸²à¸‡à¹„à¸Ÿà¸¥à¹Œà¸£à¸«à¸±à¸ªà¸ˆà¸±à¸šà¸„à¸¹à¹ˆ (à¸ªà¹ˆà¸‡à¸œà¹ˆà¸²à¸™ --code-file à¹€à¸—à¹ˆà¸²à¸™à¸±à¹‰à¸™ à¸«à¹‰à¸²à¸¡à¸­à¸¢à¸¹à¹ˆà¸šà¸™ command line)");
            }
            code = File.ReadAllText(codeFilePath)?.Trim() ?? "";
            // à¸¥à¸šà¹„à¸Ÿà¸¥à¹Œà¸£à¸«à¸±à¸ªà¸—à¸±à¸™à¸—à¸µà¸«à¸¥à¸±à¸‡à¸­à¹ˆà¸²à¸™ - à¹„à¸¡à¹ˆà¸—à¸´à¹‰à¸‡à¸£à¸«à¸±à¸ªà¹„à¸§à¹‰à¸šà¸™à¹€à¸„à¸£à¸·à¹ˆà¸­à¸‡
            try { File.Delete(codeFilePath); } catch { }
        }
        catch (Exception ex)
        {
            return (false, "à¸­à¹ˆà¸²à¸™à¹„à¸Ÿà¸¥à¹Œà¸£à¸«à¸±à¸ªà¸ˆà¸±à¸šà¸„à¸¹à¹ˆà¹„à¸¡à¹ˆà¸ªà¸³à¹€à¸£à¹‡à¸ˆ: " + ex.Message);
        }
        if (string.IsNullOrEmpty(code))
        {
            return (false, "à¸£à¸«à¸±à¸ªà¸ˆà¸±à¸šà¸„à¸¹à¹ˆà¸§à¹ˆà¸²à¸‡à¹€à¸›à¸¥à¹ˆà¸²à¹ƒà¸™à¹„à¸Ÿà¸¥à¹Œ");
        }

        try
        {
            // à¹à¸¢à¸ host:port à¸ˆà¸²à¸ master address
            var (host, port) = ParseMasterAddress(masterAddress);
            int enrollmentPort = port; // à¸žà¸­à¸£à¹Œà¸• enrollment à¸ˆà¸²à¸ master-address (à¹à¸¢à¸à¸ˆà¸²à¸ control channel)

            // --- à¸ªà¸£à¹‰à¸²à¸‡à¸„à¸¹à¹ˆà¸à¸¸à¸à¹à¸ˆ P-256 à¸šà¸™à¹€à¸„à¸£à¸·à¹ˆà¸­à¸‡à¸à¹ˆà¸­à¸™ (private key à¹„à¸¡à¹ˆà¹€à¸„à¸¢à¸­à¸­à¸à¸ˆà¸²à¸à¹€à¸„à¸£à¸·à¹ˆà¸­à¸‡à¸™à¸µà¹‰) ---
            using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            // --- à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­ Master à¸žà¸­à¸£à¹Œà¸• enrollment à¸”à¹‰à¸§à¸¢ TLS ---
            using var tcp = new TcpClient();
            // à¸ˆà¸³à¸à¸±à¸”à¹€à¸§à¸¥à¸²à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­ TCP
            var connectTask = tcp.ConnectAsync(host, enrollmentPort);
            if (await Task.WhenAny(connectTask, Task.Delay(StepTimeoutSeconds * 1000)) != connectTask)
            {
                return (false, "à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­ Master à¸«à¸¡à¸”à¹€à¸§à¸¥à¸² (TCP connect)");
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
                return (false, "à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­ Master à¸«à¸¡à¸”à¹€à¸§à¸¥à¸² (TLS handshake)");
            }
            await authTask;

            // Channel binding: SHA-256 à¸‚à¸­à¸‡ public key à¸‚à¸­à¸‡ cert à¸—à¸µà¹ˆ Master à¹‚à¸Šà¸§à¹Œà¹ƒà¸™ TLS
            byte[] channelBinding;
            var remoteCert = ssl.RemoteCertificate;
            if (remoteCert is null)
            {
                return (false, "Master à¹„à¸¡à¹ˆà¸ªà¹ˆà¸‡à¹ƒà¸šà¸£à¸±à¸šà¸£à¸­à¸‡");
            }
            using var cert2 = new X509Certificate2(remoteCert);
            var serverKey = cert2.GetECDsaPublicKey();
            if (serverKey is null)
                return (false, "Master public key invalid");
            channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);

            // K = HKDF-SHA256(ikm=codeBytes, salt=channelBinding, info="RainDrop-enroll-v1")
            // codeBytes à¸•à¹‰à¸­à¸‡à¹€à¸—à¹ˆà¸²à¸à¸±à¸šà¸—à¸µà¹ˆ Master à¹€à¸à¹‡à¸šà¹ƒà¸™ PairingCodeStore (10 à¹„à¸šà¸•à¹Œà¸ªà¸¸à¹ˆà¸¡à¸—à¸µà¹ˆ encode à¹€à¸›à¹‡à¸™ base32 string)
            byte[] codeBytes = PairingCodeStore.Base32Decode(code);
            byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
            CryptographicOperations.ZeroMemory(codeBytes);

            using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);

            // --- à¸ªà¹ˆà¸‡ CODE ---
            string codeLineOut = $"CODE {code}";
            await WriteLineWithTimeoutAsync(ssl, codeLineOut);

            // --- à¸£à¸±à¸š NONCE (à¸ˆà¸³à¸à¸±à¸”à¸„à¸§à¸²à¸¡à¸¢à¸²à¸§à¸šà¸£à¸£à¸—endueà¸à¸±à¸™ DoS) ---
            string nonceLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(nonceLine) || !nonceLine.StartsWith("NONCE ", StringComparison.Ordinal))
            {
                return (false, "Master à¹„à¸¡à¹ˆà¸•à¸­à¸šà¸à¸¥à¸±à¸š NONCE à¸—à¸µà¹ˆà¸–à¸¹à¸à¸•à¹‰à¸­à¸‡");
            }
            string nonceHex = nonceLine[6..].Trim();
            if (nonceHex.Length != EnrollmentChannel.NonceSize * 2)
            {
                return (false, "NONCE à¸¡à¸µà¸„à¸§à¸²à¸¡à¸¢à¸²à¸§à¹„à¸¡à¹ˆà¸–à¸¹à¸à¸•à¹‰à¸­à¸‡");
            }
            byte[] nonceM = Convert.FromHexString(nonceHex);

            // --- à¸ªà¹ˆà¸‡ PROOF à¸‚à¸­à¸‡ Agent ---
            byte[] nonceA = EnrollmentChannel.NewNonce();
            byte[] transcript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine));
            byte[] agentProof = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
            string agentLine = $"PROOF {Convert.ToHexString(agentProof)} {Convert.ToHexString(nonceA)} {Convert.ToHexString(transcript)}";
            await WriteLineWithTimeoutAsync(ssl, agentLine);

            // --- à¸£à¸±à¸š PROOF à¸‚à¸­à¸‡ Master à¹à¸¥à¸°à¸•à¸£à¸§à¸ˆà¸”à¹‰à¸§à¸¢ FixedTimeEquals à¸à¹ˆà¸­à¸™à¸ªà¹ˆà¸‡à¸‚à¹‰à¸­à¸¡à¸¹à¸¥à¹ƒà¸” à¹† à¸•à¹ˆà¸­ ---
            string masterProofLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(masterProofLine) || !masterProofLine.StartsWith("PROOF ", StringComparison.Ordinal))
            {
                return (false, "Master à¸ªà¹ˆà¸‡à¸žà¸´à¸ªà¸¹à¸ˆà¸™à¹Œà¸•à¸±à¸§à¸•à¸™à¹„à¸¡à¹ˆà¸–à¸¹à¸à¸•à¹‰à¸­à¸‡");
            }
            string masterProofHex = masterProofLine[6..].Trim();
            if (masterProofHex.Length != EnrollmentChannel.HmacSize * 2)
            {
                return (false, "Master PROOF à¸¡à¸µà¸„à¸§à¸²à¸¡à¸¢à¸²à¸§à¹„à¸¡à¹ˆà¸–à¸¹à¸à¸•à¹‰à¸­à¸‡");
            }
            byte[] masterProof = Convert.FromHexString(masterProofHex);

            byte[] masterTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine));
            byte[] expectedMaster = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, masterTranscript);

            // FixedTimeEquals: à¸–à¹‰à¸²à¹„à¸¡à¹ˆà¸•à¸£à¸‡à¸«à¸¢à¸¸à¸”à¸—à¸±à¸™à¸—à¸µ à¹„à¸¡à¹ˆà¸ªà¹ˆà¸‡ PUBKEY/POP à¸•à¹ˆà¸­
            if (!EnrollmentChannel.VerifyProof(expectedMaster, masterProof))
            {
                return (false, "à¸žà¸´à¸ªà¸¹à¸ˆà¸™à¹Œà¸•à¸±à¸§à¸•à¸™à¸‚à¸­à¸‡ Master à¹„à¸¡à¹ˆà¸•à¸£à¸‡à¸à¸±à¸™ (à¸­à¸²à¸ˆà¸¡à¸µ MITM) à¸«à¸¢à¸¸à¸”à¸à¸²à¸£à¸ªà¹ˆà¸‡à¸‚à¹‰à¸­à¸¡à¸¹à¸¥");
            }

            // --- à¸ªà¹ˆà¸‡ machine name + public key SPKI + proof-of-possession (à¸«à¸¥à¸±à¸‡à¸ˆà¸²à¸ proof à¸‚à¸­à¸‡ Master à¸œà¹ˆà¸²à¸™à¹à¸¥à¹‰à¸§à¹€à¸—à¹ˆà¸²à¸™à¸±à¹‰à¸™) ---
            string machineName = Environment.MachineName;
            await WriteLineWithTimeoutAsync(ssl, $"MACHINE {machineName}");
            byte[] spki = agentKey.ExportSubjectPublicKeyInfo();
            await WriteLineWithTimeoutAsync(ssl, $"PUBKEY {Convert.ToBase64String(spki)}");

            // Transcript hash à¸„à¸£à¸­à¸šà¸—à¸¸à¸à¸‚à¹‰à¸­à¸„à¸§à¸²à¸¡à¹à¸¥à¸à¹€à¸›à¸¥à¸µà¹ˆà¸¢à¸™à¸£à¸§à¸¡à¸–à¸¶à¸‡ PUBKEY (anti-downgrade, anti-MITM)
            byte[] popTranscript = EnrollmentChannel.TranscriptHash(
                Encoding.UTF8.GetBytes(codeLineOut),
                Encoding.UTF8.GetBytes(nonceLine),
                Encoding.UTF8.GetBytes(agentLine),
                Encoding.UTF8.GetBytes(masterProofLine),
                Encoding.UTF8.GetBytes($"MACHINE {machineName}"),
                Encoding.UTF8.GetBytes($"PUBKEY {Convert.ToBase64String(spki)}"));
            byte[] popSignature = agentKey.SignData(popTranscript, HashAlgorithmName.SHA256);
            await WriteLineWithTimeoutAsync(ssl, $"POP {Convert.ToBase64String(popSignature)}");

            // --- à¸£à¸±à¸š ISSUED + CA ---
            string issuedLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(issuedLine) || !issuedLine.StartsWith("ISSUED ", StringComparison.Ordinal))
            {
                return (false, "Master à¹„à¸¡à¹ˆà¸­à¸­à¸à¹ƒà¸šà¸£à¸±à¸šà¸£à¸­à¸‡: " + issuedLine);
            }
            byte[] agentCertDer = Convert.FromBase64String(issuedLine[7..].Trim());

            string caLine = await ReadLineWithTimeoutAsync(sr);
            if (string.IsNullOrWhiteSpace(caLine) || !caLine.StartsWith("CA ", StringComparison.Ordinal))
            {
                return (false, "Master à¹„à¸¡à¹ˆà¸ªà¹ˆà¸‡ CA public cert");
            }
            byte[] caCertDer = Convert.FromBase64String(caLine[3..].Trim());

            // --- à¸•à¸±à¹‰à¸‡ ACL à¸‚à¸­à¸‡ private\ à¸à¹ˆà¸­à¸™à¹€à¸‚à¸µà¸¢à¸™ agent-key.pkcs8 ---
            Directory.CreateDirectory(folder);
            string privateDir = Path.Combine(folder, "private");
            Directory.CreateDirectory(privateDir);
            AgentConfig.HardenFolder(folder, usersRead: true, keepCurrentUser);
            AgentConfig.HardenFolder(privateDir, usersRead: false, keepCurrentUser);

            File.WriteAllBytes(Path.Combine(privateDir, "agent-cert.der"), agentCertDer);
            File.WriteAllBytes(Path.Combine(privateDir, "agent-key.pkcs8"), agentKey.ExportPkcs8PrivateKey());
            File.WriteAllBytes(Path.Combine(privateDir, "ca-cert.der"), caCertDer);

            // agent.json à¸Šà¸µà¹‰ control channel à¹„à¸›à¸¢à¸±à¸‡ Master
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(folder, "agent.json"),
                System.Text.Json.JsonSerializer.Serialize(new AgentConfig { Port = port, HubAddress = masterAddress }, options));

            // à¸£à¸«à¸±à¸ªà¹„à¸¡à¹ˆà¸–à¸¹à¸à¹€à¸à¹‡à¸šà¹„à¸§à¹‰à¹ƒà¸™à¹€à¸„à¸£à¸·à¹ˆà¸­à¸‡à¸«à¸¥à¸±à¸‡à¸ˆà¸²à¸à¸™à¸µà¹‰ - à¸­à¹ˆà¸²à¸™à¸ˆà¸²à¸à¹„à¸Ÿà¸¥à¹Œà¹à¸¥à¹‰à¸§à¸¥à¸šà¸—à¸±à¸™à¸—à¸µ
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

    /// <summary>à¸Šà¸·à¹ˆà¸­à¹€à¸¡à¸˜à¸­à¸”à¹€à¸”à¸µà¸¢à¸§à¸ªà¸³à¸«à¸£à¸±à¸šà¸•à¸£à¸§à¸ˆà¸ªà¸­à¸š server cert à¸›à¸¥à¸­à¸”à¸ à¸±à¸¢à¹€à¸žà¸£à¸²à¸° channel binding + proof à¸—à¸µà¹ˆà¸•à¸²à¸¡à¸¡à¸²
    /// Agent à¸¢à¸±à¸‡à¹„à¸¡à¹ˆà¸¡à¸µ CA à¸‚à¸­à¸‡ Master à¹ƒà¸«à¹‰ pin - à¸¢à¸­à¸¡à¸£à¸±à¸šà¸—à¸¸à¸ cert à¹à¸¥à¹‰à¸§à¸žà¸¶à¹ˆà¸‡ channel binding (SHA-256 à¸‚à¸­à¸‡ public key)
    /// à¸šà¸§à¸à¸à¸±à¸š bidirectional HMAC proof + proof-of-possession à¹€à¸žà¸·à¹ˆà¸­à¸à¸±à¸™ MITM
    /// à¸«à¹‰à¸²à¸¡à¸ªà¹ˆà¸‡à¸‚à¹‰à¸­à¸¡à¸¹à¸¥à¸¥à¸±à¸šà¹ƒà¸” à¹† à¸à¹ˆà¸­à¸™à¸—à¸µà¹ˆ proof à¸ˆà¸°à¸œà¹ˆà¸²à¸™à¸à¸²à¸£à¸•à¸£à¸§à¸ˆà¸”à¹‰à¸§à¸¢ FixedTimeEquals</summary>
    private static bool ValidateServerCertificateCallback(
        object? sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        // à¸¢à¸­à¸¡à¸£à¸±à¸šà¸—à¸¸à¸ cert à¸•à¸±à¹‰à¸‡à¹à¸•à¹ˆà¹à¸£à¸ à¹à¸•à¹ˆà¸à¸²à¸£à¸¢à¸·à¸™à¸¢à¸±à¸™à¸•à¸±à¸§à¸•à¸™à¸ˆà¸£à¸´à¸‡à¹€à¸à¸´à¸”à¸‚à¸¶à¹‰à¸™à¸«à¸¥à¸±à¸‡à¸ˆà¸²à¸à¸™à¸µà¹‰à¸œà¹ˆà¸²à¸™
        // channel binding + HMAC proof + proof-of-possession à¸”à¹‰à¸§à¸¢ private key à¸‚à¸­à¸‡ Agent
        return true;
    }

    private static async Task WriteLineWithTimeoutAsync(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader sr)
    {
        var readTask = sr.ReadLineAsync(); // à¸­à¹ˆà¸²à¸™à¸—à¸±à¹‰à¸‡à¸šà¸£à¸£à¸—à¸±à¸” (SslStream.ReadAsync(char[],0,1) à¸­à¸²à¸ˆà¸„à¸·à¸™ 0 à¸à¹ˆà¸­à¸™à¹€à¸§à¸¥à¸²à¸­à¸±à¸™à¸„à¸§à¸£à¸—à¸³à¹ƒà¸«à¹‰à¹€à¸‚à¹‰à¸²à¹ƒà¸ˆà¸œà¸´à¸”à¸§à¹ˆà¸² stream à¸›à¸´à¸”)
        var timeout = Task.Delay(StepTimeoutSeconds * 1000);
        if (await Task.WhenAny(readTask, timeout) != readTask)
            throw new TimeoutException($"à¸­à¹ˆà¸²à¸™à¸šà¸£à¸£à¸—endueà¸«à¸¡à¸”à¹€à¸§à¸¥à¸² ({StepTimeoutSeconds} à¸§à¸´à¸™à¸²à¸—à¸µ)");
        var line = await readTask;
        if (line is not null && line.Length > MaxLineLength)
            throw new InvalidDataException($"à¸šà¸£à¸£à¸—à¹ˆà¸§à¸¢à¸²à¸§à¹€à¸à¸´à¸™à¸à¸³à¸«à¸™à¸” ({MaxLineLength} bytes) à¸–à¸¹à¸à¸•à¸±à¸”à¸à¸²à¸£à¹€à¸Šà¸·à¹ˆà¸­à¸¡à¸•à¹ˆà¸­");
        return line ?? "";
    }
}

