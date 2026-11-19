using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using RainDrop.Agent;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>เทสต์ฝั่ง Agent ของ EnrollmentClient (C2b). Master ปลอมทดลองบทบาท MITM:
/// ให้ channel binding ต่างจาก Master จริง หรือส่ง PROOF ปลอม -> Agent ต้องยุติ
/// ก่อนส่ง PUBKEY/POP ทุกกรณี ใช้พอร์ตสุ่ม + timeout 15 วินาทีกันเทสต์แขวน</summary>
[Collection("EnrollmentClient serial")]
public class EnrollmentClientTests : IDisposable
{
    private readonly string _tempFolder;
    private readonly TcpListener _fakeMaster;
    private readonly int _fakeMasterPort;
    private readonly X509Certificate2 _serverCert;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private bool _sendWrongProof;
    private bool _closeAfterNonce;
    private bool _receivedPubkey;

    public EnrollmentClientTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), $\"raindrop-enrollclient-{Guid.NewGuid():N}\");
        Directory.CreateDirectory(_tempFolder);
        Directory.CreateDirectory(Path.Combine(_tempFolder, \"private\"));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            new X500DistinguishedName(\"CN=Fake-Master-Enrollment-Test\"),
            key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        _serverCert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        _fakeMaster = new TcpListener(IPAddress.Loopback, 0);
        _fakeMaster.Start();
        _fakeMasterPort = ((IPEndPoint)_fakeMaster.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, int seconds = 15)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(seconds));
        if (await Task.WhenAny(task, timeout) != task)
            throw new TimeoutException($\"ทดสอบหมดเวลา ({seconds} วินาที)\");
        return await task;
    }

    private static async Task WithTimeoutAsync(Task task, int seconds = 15)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(seconds));
        if (await Task.WhenAny(task, timeout) != task)
            throw new TimeoutException($\"ทดสอบหมดเวลา ({seconds} วินาที)\");
        await task;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await _fakeMaster.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
            catch { break; }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var tcp = client;
        using var stream = tcp.GetStream();
        using var ssl = new SslStream(stream, false, (_, _, _, _) => true);
        try
        {
            await WithTimeoutAsync(ssl.AuthenticateAsServerAsync(_serverCert, false, false));
        }
        catch { return; }

        using var sr = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        string? codeLine = await WithTimeoutAsync(sr.ReadLineAsync());
        if (string.IsNullOrWhiteSpace(codeLine)) return;
        string code = codeLine.Trim().StartsWith(\"CODE \", StringComparison.Ordinal)
            ? codeLine.Trim()[5..] : \"\";

        byte[] nonceM = EnrollmentChannel.NewNonce();
        await WithTimeoutAsync(writer.WriteLineAsync($\"NONCE {Convert.ToHexString(nonceM)}\"));
        await writer.FlushAsync();

        if (_closeAfterNonce)
        {
            // Master ตัดการเชื่อมต่อทันทีหลังส่ง NONCE (ไม่รอ PROOF จาก Agent)
            return;
        }

        string? agentLine = await WithTimeoutAsync(sr.ReadLineAsync());
        if (string.IsNullOrWhiteSpace(agentLine)) return;

        var parts = agentLine.Trim().Split(' ');
        if (parts.Length < 4 || parts[0] != \"PROOF\") return;

        byte[] agentProof = Convert.FromHexString(parts[1]);
        byte[] nonceA = Convert.FromHexString(parts[2]);
        byte[] transcript = Convert.FromHexString(parts[3]);

        byte[] codeBytes = Encoding.UTF8.GetBytes(code);
        using var serverKey = _serverCert.GetECDsaPublicKey()!;
        byte[] channelBinding = EnrollmentChannel.DeriveChannelBindingFromCert(serverKey);
        byte[] k = EnrollmentChannel.HkdfSha256(codeBytes, channelBinding, EnrollmentChannel.Info, 32);
        byte[] expectedAgent = EnrollmentChannel.ComputeAgentProof(k, nonceA, nonceM, transcript);
        if (!EnrollmentChannel.VerifyProof(expectedAgent, agentProof)) return;

        // ส่ง PROOF ของ Master และ flush ให้แน่ใจว่าส่งถึง Agent ก่อนที่ Agent จะอ่าน
        if (_sendWrongProof)
        {
            await WithTimeoutAsync(writer.WriteLineAsync($\"PROOF {Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}\"));
        }
        else
        {
            byte[] masterProof = EnrollmentChannel.ComputeMasterProof(k, nonceM, nonceA, transcript);
            await WithTimeoutAsync(writer.WriteLineAsync($\"PROOF {Convert.ToHexString(masterProof)}\"));
        }
        await writer.FlushAsync();

        // รออ่านจาก Agent (PUBKEY หรือการปิด connection) ก่อนจบ HandleClientAsync
        // เช่นกันทำงานตามลำดับ ไม่ใช้ Task.Delay แล้วหวัง
        string? next = await WithTimeoutAsync(sr.ReadLineAsync());
        if (next is not null && next.StartsWith(\"PUBKEY \", StringComparison.Ordinal))
            _receivedPubkey = true;
    }

    /// <summary>MITM ตอบ PROOF ปลอม -> Agent ไม่ส่ง PUBKEY (หยุดก่อนเปิดเผยกุญแจสาธารณะ)</summary>
    [Fact]
    public async Task FakeMasterSendsWrongProof_AgentStopsBeforeSendingPubkey()
    {
        _sendWrongProof = true;
        _closeAfterNonce = false;
        _receivedPubkey = false;

        string code = \"ABCD1234EFGH5678\";
        string codeFile = Path.Combine(_tempFolder, \"code.txt\");
        File.WriteAllText(codeFile, code);

        var (ok, error) = await WithTimeoutAsync(EnrollmentClient.Run(
            Path.Combine(_tempFolder, \"agent-home\"),
            $\"127.0.0.1:{_fakeMasterPort}\",
            codeFile));

        Assert.False(ok, \"Agent ต้องล้มเหลวเมื่อ Master ส่ง PROOF ปลอม: \" + error);
        Assert.Contains(\"ไม่ตรงกัน\", error);
        Assert.False(_receivedPubkey, \"Agent ต้องไม่ส่ง PUBKEY ก่อนที่ proof ของ Master จะผ่านการตรวจ\");
        Assert.False(File.Exists(codeFile), \"ไฟล์รหัสต้องถูกลบทันทีหลังอ่านแล้ว\");
    }

    /// <summary>Master ตัดการเชื่อมต่อหลังส่ง NONCE -> Agent ต้องไม่ส่ง PUBKEY</summary>
    [Fact]
    public async Task FakeMasterDisconnectsAfterNonce_AgentFailsNoPubkey()
    {
        _sendWrongProof = false;
        _closeAfterNonce = true;
        _receivedPubkey = false;

        string code = \"ABCD1234EFGH5678\";
        string codeFile = Path.Combine(_tempFolder, \"code2.txt\");
        File.WriteAllText(codeFile, code);

        var (ok, error) = await WithTimeoutAsync(EnrollmentClient.Run(
            Path.Combine(_tempFolder, \"agent-home-2\"),
            $\"127.0.0.1:{_fakeMasterPort}\",
            codeFile));

        Assert.False(ok, \"Agent ต้องล้มเหลวเมื่อการเชื่อมต่อขาดหลัง NONCE: \" + error);
        Assert.False(_receivedPubkey, \"Agent ต้องไม่ส่ง PUBKEY เมื่อการเชื่อมต่อขาด\");
        Assert.False(File.Exists(codeFile), \"ไฟล์รหัสต้องถูกลบทันทีหลังอ่านแล้ว\");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _fakeMaster.Stop();
        try { _acceptLoop.GetAwaiter().GetResult(); } catch { }
        _serverCert.Dispose();
    }
}
