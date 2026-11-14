using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RainDrop.Agent;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>C2a required loopback scenarios. No test touches a real endpoint or the Windows root store.</summary>
public class ControlTlsTests : IDisposable
{
    private readonly X509Certificate2 _ca = RepoCertificates.CreateCa();
    private readonly X509Certificate2 _caPublic;
    private readonly X509Certificate2 _client;
    private readonly X509Certificate2 _agent;
    private readonly ECDsa _challenge = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ControlTlsTests()
    {
        _caPublic = new X509Certificate2(_ca.RawData);
        using var caKey = _ca.GetECDsaPrivateKey()!;
        _client = RepoCertificates.IssueClient(_ca, caKey);
        _agent = RepoCertificates.IssueAgent(_ca, caKey, Guid.NewGuid().ToString("N"), "TEST-PC", new[] { "127.0.0.1" });
    }

    public void Dispose()
    {
        _ca.Dispose(); _caPublic.Dispose(); _client.Dispose(); _agent.Dispose(); _challenge.Dispose();
    }

    private static X509Certificate2 IssueWithValidity(X509Certificate2 ca, string eku, DateTime notBefore, DateTime notAfter)
    {
        using var caKey = ca.GetECDsaPrivateKey()!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=validity-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var issued = request.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    private async Task ExpectRejectedAsync(X509Certificate2? clientCert, RevocationState? serverRevoked = null, bool useWrongEku = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverPolicy = new ControlTlsPolicy(_caPublic, serverRevoked ?? new RevocationState(), ControlRole.MasterClient);
        Task server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await Assert.ThrowsAsync<ControlTlsException>(() =>
                ControlTls.AuthenticateServerAsync(tcp.GetStream(), _agent, serverPolicy, CancellationToken.None));
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        if (clientCert is null)
        {
            using var ssl = new SslStream(client.GetStream(), false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                CertificateChainPolicy = new ControlTlsPolicy(_caPublic, new RevocationState(), ControlRole.AgentServer).ChainPolicy(),
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    new ControlTlsPolicy(_caPublic, new RevocationState(), ControlRole.AgentServer).Validate(cert, chain, errors, out string _),
            };
            // The server must close the connection because no client certificate was supplied.
            await ExpectClosedAsync(async () =>
            {
                await ssl.AuthenticateAsClientAsync(options);
                while (await ssl.ReadAsync(new byte[1]) > 0) { }
            });
        }
        else
        {
            var cert = useWrongEku ? _agent : clientCert;
            await ExpectClosedAsync(async () =>
            {
                using var ssl = await ControlTls.AuthenticateClientAsync(
                    client.GetStream(), "127.0.0.1", cert,
                    new ControlTlsPolicy(_caPublic, new RevocationState(), ControlRole.AgentServer), CancellationToken.None);
                while (await ssl.ReadAsync(new byte[1]) > 0) { }
            });
        }
        await server.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact] public Task NoClientCertificate_Rejected() => ExpectRejectedAsync(null);

    [Fact]
    public async Task ForeignCaCertificate_Rejected()
    {
        using var foreignCa = RepoCertificates.CreateCa();
        using var foreignKey = foreignCa.GetECDsaPrivateKey()!;
        using var foreignClient = RepoCertificates.IssueClient(foreignCa, foreignKey);
        await ExpectRejectedAsync(foreignClient);
    }

    [Fact]
    public async Task ExpiredCertificate_Rejected()
    {
        var caStart = _ca.NotBefore.ToUniversalTime();
        using var expired = IssueWithValidity(_ca, RepoCertificates.ClientAuthOid, caStart, caStart.AddMinutes(1));
        await ExpectRejectedAsync(expired);
    }

    [Fact]
    public async Task RevokedCertificate_Rejected()
    {
        var revoked = new RevocationState();
        revoked.Revoke(RepoCertificates.Serial(_client));
        await ExpectRejectedAsync(_client, revoked);
    }

    [Fact] public Task AgentCertificateUsedAsClient_Rejected() => ExpectRejectedAsync(_client, useWrongEku: true);

    [Fact]
    public async Task PlaintextConnection_RejectedByAgentServer()
    {
        var handler = new FakeHandler();
        var security = new AgentSecurity(_agent, new X509Certificate2(_ca.RawData), new RevocationList(1, new List<string>()),
            new RunAllowlist(new List<string>()), _ => true, _ => true);
        var server = new AgentServer(0, _challenge, handler, security, discovery: false);
        Task run = server.RunAsync();
        try
        {
            int port = await WaitPort(server);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await Protocol.WriteAsync(stream, MsgType.AuthResponse, new byte[32]);
            stream.ReadTimeout = 12000;
            try { Assert.Equal(0, stream.Read(new byte[1], 0, 1)); }
            catch (IOException) { /* TLS alert / abrupt close is also a rejection */ }
        }
        finally { server.Stop(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task MutualTlsLegacyChallengeAndRevocation_Succeed()
    {
        var handler = new FakeHandler();
        var revoked = new RevocationState();
        var audits = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var security = new AgentSecurity(_agent, new X509Certificate2(_ca.RawData), revoked.Snapshot(),
            new RunAllowlist(new List<string>()), e => { audits.Enqueue(e.Event + ":" + e.Detail); return true; }, _ => true);
        var server = new AgentServer(0, _challenge, handler, security, discovery: false);
        Task run = server.RunAsync();
        try
        {
            int port = await WaitPort(server);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            SslStream ssl;
            try
            {
                ssl = await ControlTls.AuthenticateClientAsync(tcp.GetStream(), "127.0.0.1", _client,
                    new ControlTlsPolicy(_caPublic, new RevocationState(), ControlRole.AgentServer), CancellationToken.None);
            }
            catch (Exception ex) { throw new Exception(ex.Message + " | server-audit=" + string.Join(",", audits), ex); }
            using var sslScope = ssl;
            var (type, nonce) = await Protocol.ReadAsync(ssl);
            Assert.Equal(MsgType.AuthChallenge, type);
            await Protocol.WriteAsync(ssl, MsgType.AuthResponse, Protocol.Sign(_challenge, nonce));
            var (resultType, result) = await Protocol.ReadAsync(ssl);
            Assert.Equal(MsgType.AuthResult, resultType);
            Assert.Equal(1, result[0]);
            var snapshot = new RevocationList(2, new List<string> { "ABCD" });
            await Protocol.WriteAsync(ssl, ControlMessages.RevocationList, Protocol.ToJson(snapshot));
            var (ack, _) = await Protocol.ReadAsync(ssl);
            Assert.Equal(ControlMessages.RevocationAck, ack);
            Assert.True(security.Revocation.IsRevoked("ABCD"));
            await Protocol.WriteAsync(ssl, MsgType.Lock, Protocol.ToJson(new LockCmd("test")));
            await WaitUntil(() => handler.Locks == 1);
        }
        finally { server.Stop(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task ExpectClosedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ControlTlsException) { return; }
        // A clean EOF after the handshake is also a rejection: the server refused to exchange application data.
    }

    private static async Task<int> WaitPort(AgentServer server)
    {
        for (int i = 0; i < 200; i++)
        {
            if (server.ListeningPort != 0) return server.ListeningPort;
            await Task.Delay(10);
        }
        throw new TimeoutException("listener did not start");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("condition not met");
    }

    private sealed class FakeHandler : IAgentHandler
    {
        public int Locks;
        public void ReportError(string message) { }
        public void Lock(string text) => Interlocked.Increment(ref Locks);
        public void Unlock() { }
        public void ShowMessage(string title, string text) { }
        public ActivitySnapshot GetActivity() => new("", "", new(), new(), new(), DateTime.UtcNow, DateTime.UtcNow);
        public void SetBlocklist(BlockRules rules) { }
        public void ReceiveChat(ChatMsg msg) { }
        public void ReceiveVoice(VoiceMsg msg) { }
    }
}
