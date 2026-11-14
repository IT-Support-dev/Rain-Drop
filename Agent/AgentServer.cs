using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RainDrop.Shared;

namespace RainDrop.Agent;

internal interface IAgentHandler
{
    void ReportError(string message);
    void Lock(string text);
    void Unlock();
    void ShowMessage(string title, string text);
    ActivitySnapshot GetActivity();
    void SetBlocklist(BlockRules rules);
    void ReceiveChat(ChatMsg msg);
    void ReceiveVoice(VoiceMsg msg);
}

/// <summary>
/// TCP control server: mutual TLS first, then the unchanged C1 ECDSA challenge/response,
/// then a versioned revocation snapshot. No plaintext path exists.
/// </summary>
internal sealed class AgentServer
{
    private readonly int _port;
    private readonly ECDsa? _publicKey;
    private readonly object _keyLock = new();
    private readonly IAgentHandler _ctx;
    private readonly AgentSecurity? _security;
    private readonly ConnectionRateLimiter _limiter;
    private readonly bool _discovery;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    private readonly ConcurrentDictionary<Session, byte> _sessions = new();

    private sealed class Session
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public Stream Stream { get; }
        public string Remote { get; }
        public string Serial { get; }
        public Session(Stream stream, string remote, string serial)
        {
            Stream = stream;
            Remote = remote;
            Serial = serial;
        }

        public async Task SendAsync(MsgType type, byte[] payload)
        {
            await _writeLock.WaitAsync();
            try { await Protocol.WriteAsync(Stream, type, payload); }
            finally { _writeLock.Release(); }
        }
    }

    public bool HasMaster => !_sessions.IsEmpty;
    public IReadOnlyList<string> ConnectedMasters => _sessions.Keys.Select(s => s.Remote).Distinct().ToList();
    public string? StartError { get; private set; }
    public int Port => _port;
    public int ListeningPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;
    public bool HasPublicKey => _publicKey is not null;
    public string? KeyFingerprint => _publicKey is null ? null : Protocol.Fingerprint(_publicKey);

    public int BroadcastChat(ChatMsg msg) => Broadcast(MsgType.ChatMessage, Protocol.ToJson(msg));
    public int BroadcastVoice(VoiceMsg msg) => Broadcast(MsgType.VoiceMessage, Protocol.ToJson(msg));
    public int BroadcastAlert(AlertMsg msg) => Broadcast(MsgType.Alert, Protocol.ToJson(msg));

    private int Broadcast(MsgType type, byte[] payload)
    {
        int count = 0;
        foreach (var session in _sessions.Keys)
        {
            count++;
            _ = Task.Run(async () =>
            {
                try { await session.SendAsync(type, payload); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // จงใจไม่ส่งซ้ำ: สายหลุด ลูปอ่านจะถอด session เอง; บันทึกเหตุผลไว้ตรวจสอบ
                    Diag.Warn("AgentServer.Broadcast", "ส่งข้อความไป Master ไม่สำเร็จเพราะการเชื่อมต่อปิดแล้ว", ex);
                }
            });
        }
        return count;
    }

    public AgentServer(int port, ECDsa? publicKey, IAgentHandler ctx, AgentSecurity? security,
        ConnectionRateLimiter? limiter = null, bool discovery = true)
    {
        _port = port;
        _publicKey = publicKey;
        _ctx = ctx;
        _security = security;
        _limiter = limiter ?? new ConnectionRateLimiter();
        _discovery = discovery;
    }

    public async Task RunAsync()
    {
        if (_security is null)
        {
            StartError = "ยังไม่มี Agent identity จาก Service จึงไม่เปิดรับการเชื่อมต่อแบบ plaintext";
            _ctx.ReportError(StartError);
            return;
        }
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            StartError = $"เปิดพอร์ต {_port} ไม่ได้: {ex.Message}";
            _ctx.ReportError(StartError);
            return;
        }

        if (_discovery && KeyFingerprint is { } fingerprint)
            _ = Task.Run(() => Discovery.RespondAsync(_port, fingerprint, _port, _ctx.ReportError, _cts.Token));

        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (!_cts.IsCancellationRequested) Diag.Warn("AgentServer.Accept", " listener stopped unexpectedly", ex);
                break;
            }
            var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!_limiter.TryConnect(ip))
            {
                client.Dispose();
                _security.Audit(new AuditEvent("rate-limit", ip.ToString(), "denied", "connection-limit"));
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ip));
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var session in _sessions.Keys) session.Stream.Dispose();
    }

    private async Task HandleClientAsync(TcpClient client, IPAddress ip)
    {
        using (client)
        {
            client.NoDelay = true;
            Stream? tls = null;
            try
            {
                var policy = new ControlTlsPolicy(_security!.Ca, _security.Revocation, ControlRole.MasterClient);
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                handshakeCts.CancelAfter(ControlTls.HandshakeTimeout);
                try
                {
                    tls = await ControlTls.AuthenticateServerAsync(client.GetStream(), _security.Certificate, policy, handshakeCts.Token);
                }
                catch (ControlTlsException ex)
                {
                    _limiter.RecordFailure(ip);
                    string detail = (ex.Reason + ":" + (ex.InnerException?.Message ?? "")).Replace("\r", " ").Replace("\n", " ");
                    if (detail.Length > 128) detail = detail[..128];
                    _security.Audit(new AuditEvent("tls-fail", ip.ToString(), "denied", detail));
                    return;
                }

                if (!await AuthenticateAsync(tls))
                {
                    _limiter.RecordFailure(ip);
                    _security.Audit(new AuditEvent("auth-fail", ip.ToString(), "denied", "challenge"));
                    return;
                }

                using var revocationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                revocationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var (type, payload) = await Protocol.ReadAsync(tls, revocationTimeout.Token);
                if (type != ControlMessages.RevocationList) throw new InvalidDataException("Expected revocation snapshot");
                var incoming = Protocol.FromJson<RevocationList>(payload);
                if (!_security.ApplyRevocation(incoming)) throw new IOException("Revocation persistence failed");
                await Protocol.WriteAsync(tls, ControlMessages.RevocationAck, Array.Empty<byte>(), revocationTimeout.Token);

                var ssl = (System.Net.Security.SslStream)tls;
                using var remoteCert = new X509Certificate2(ssl.RemoteCertificate!);
                var session = new Session(tls, ip.ToString(), RepoCertificates.Serial(remoteCert));
                if (!_security.Audit(new AuditEvent("connect", ip.ToString(), "ok", session.Serial)))
                    return; // audit failure is fail-closed
                _sessions.TryAdd(session, 0);
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var (packetType, packetPayload) = await Protocol.ReadAsync(tls, _cts.Token);
                        await HandlePacketAsync(session, packetType, packetPayload);
                    }
                }
                finally
                {
                    _sessions.TryRemove(session, out _);
                    _security.Audit(new AuditEvent("disconnect", ip.ToString(), "ok", session.Serial));
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
            {
                // การเชื่อมต่อขาด/ข้อมูลผิดรูปแบบ: ปิดสายนี้ ไม่ fallback เป็น plaintext
                if (!_cts.IsCancellationRequested)
                    Diag.Warn("AgentServer.HandleClient", "control connection closed: " + ex.GetType().Name);
            }
            finally
            {
                tls?.Dispose();
            }
        }
    }

    /// <summary>C1 challenge/response, byte-for-byte unchanged, now carried inside TLS.</summary>
    private async Task<bool> AuthenticateAsync(Stream stream)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await Protocol.WriteAsync(stream, MsgType.AuthChallenge, nonce);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (type, signature) = await Protocol.ReadAsync(stream, timeout.Token);
        bool ok = false;
        if (type == MsgType.AuthResponse && _publicKey is not null)
        {
            lock (_keyLock) ok = Protocol.Verify(_publicKey, nonce, signature);
        }
        await Protocol.WriteAsync(stream, MsgType.AuthResult, new[] { (byte)(ok ? 1 : 0) });
        if (!ok) await Task.Delay(1000);
        return ok;
    }

    private bool AuditCommand(Session session, string eventName, string result, string detail = "")
    {
        if (_security!.Audit(new AuditEvent(eventName, session.Remote, result, detail))) return true;
        session.Stream.Dispose(); // fail closed when the protected audit chain cannot be appended
        return false;
    }

    private async Task HandlePacketAsync(Session session, MsgType type, byte[] payload)
    {
        switch (type)
        {
            case ControlMessages.RevocationList:
            {
                var incoming = Protocol.FromJson<RevocationList>(payload);
                bool saved = _security!.ApplyRevocation(incoming);
                if (!AuditCommand(session, "revoke", saved ? "ok" : "denied", "snapshot")) return;
                await session.SendAsync(ControlMessages.RevocationAck, Array.Empty<byte>());
                break;
            }
            case MsgType.ScreenRequest:
            {
                var req = Protocol.FromJson<ScreenRequest>(payload);
                var jpeg = ScreenCapture.CaptureJpeg(req.MaxWidth, req.Quality);
                await session.SendAsync(MsgType.ScreenFrame, jpeg);
                break;
            }
            case MsgType.MouseEvent:
                InputSimulator.Mouse(Protocol.FromJson<MouseCmd>(payload));
                break;
            case MsgType.KeyEvent:
                InputSimulator.Key(Protocol.FromJson<KeyCmd>(payload));
                break;
            case MsgType.Lock:
                if (!AuditCommand(session, "lock", "ok")) return;
                _ctx.Lock(Protocol.FromJson<LockCmd>(payload).Text);
                break;
            case MsgType.Unlock:
                if (!AuditCommand(session, "unlock", "ok")) return;
                _ctx.Unlock();
                break;
            case MsgType.ShowMessage:
            {
                var msg = Protocol.FromJson<MessageCmd>(payload);
                _ctx.ShowMessage(msg.Title, msg.Text);
                break;
            }
            case MsgType.PowerAction:
            {
                var action = Protocol.FromJson<PowerCmd>(payload).Action;
                if (!AuditCommand(session, "power", "ok", action.ToString())) return;
                PowerActions.Execute(action);
                break;
            }
            case MsgType.ChatMessage:
                _ctx.ReceiveChat(Protocol.FromJson<ChatMsg>(payload));
                break;
            case MsgType.VoiceMessage:
                _ctx.ReceiveVoice(Protocol.FromJson<VoiceMsg>(payload));
                break;
            case MsgType.ActivityRequest:
                await session.SendAsync(MsgType.ActivitySnapshot, Protocol.ToJson(_ctx.GetActivity()));
                break;
            case MsgType.SetBlocklist:
            {
                var rules = Protocol.FromJson<BlockRules>(payload);
                if (!AuditCommand(session, "blocklist", "ok", $"{rules.Programs.Count}/{rules.Urls.Count}")) return;
                _ctx.SetBlocklist(rules);
                break;
            }
            case MsgType.RunCommand:
            {
                var run = Protocol.FromJson<RunCmd>(payload);
                var decision = RunCommandPolicy.Evaluate(run, _security!.Allowlist);
                if (!AuditCommand(session, "run", decision.Allowed ? "ok" : "denied", decision.Reason)) return;
                if (!decision.Allowed || decision.StartInfo is null) return;
                try { Process.Start(decision.StartInfo)?.Dispose(); }
                catch (Exception ex)
                {
                    // ไม่บันทึก target/arguments เพราะอาจเป็นความลับ; บันทึกเฉพาะชนิด error
                    Diag.Warn("AgentServer.RunCommand", "allowlisted launch failed: " + ex.GetType().Name);
                }
                break;
            }
        }
    }
}

internal static class PowerActions
{
    public static void Execute(PowerAction action)
    {
        string args = action switch
        {
            PowerAction.Shutdown => "/s /t 0",
            PowerAction.Restart => "/r /t 0",
            PowerAction.Logoff => "/l",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        using var process = Process.Start(new ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false });
    }
}
