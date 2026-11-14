using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>
/// TCP server ที่รอให้ Master เชื่อมต่อเข้ามา
/// แต่ละการเชื่อมต่อ: ยืนยันตัวตน -> วนรับคำสั่ง
/// </summary>
internal sealed class AgentServer
{
    private readonly int _port;
    private readonly ECDsa? _publicKey;
    private readonly object _keyLock = new();
    private readonly AgentContext _ctx;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    /// <summary>การเชื่อมต่อที่ยืนยันตัวตนแล้ว ใช้สำหรับส่งข้อความแชทกลับไปหา Master</summary>
    private readonly ConcurrentDictionary<Session, byte> _sessions = new();

    /// <summary>หนึ่งการเชื่อมต่อ: ล็อกการเขียนไว้ เพราะแชทอาจถูกส่งจาก UI thread พร้อมกับการตอบภาพหน้าจอ</summary>
    private sealed class Session
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public NetworkStream Stream { get; }
        public string Remote { get; }
        public Session(NetworkStream stream, string remote)
        {
            Stream = stream;
            Remote = remote;
        }

        public async Task SendAsync(MsgType type, byte[] payload)
        {
            await _writeLock.WaitAsync();
            try { await Protocol.WriteAsync(Stream, type, payload); }
            finally { _writeLock.Release(); }
        }
    }

    public bool HasMaster => !_sessions.IsEmpty;

    /// <summary>รายการ IP ของ Master ที่เชื่อมต่ออยู่ (ไม่ซ้ำ)</summary>
    public IReadOnlyList<string> ConnectedMasters =>
        _sessions.Keys.Select(s => s.Remote).Distinct().ToList();

    /// <summary>ถ้าเปิดพอร์ตไม่สำเร็จจะมีข้อความ error อยู่ที่นี่</summary>
    public string? StartError { get; private set; }

    public int Port => _port;
    public bool HasPublicKey => _publicKey is not null;
    public string? KeyFingerprint => _publicKey is null ? null : Protocol.Fingerprint(_publicKey);

    /// <summary>ส่งแชท/ข้อความเสียงไปยัง Master ทุกตัวที่เชื่อมต่ออยู่ คืนค่าจำนวนการเชื่อมต่อที่ส่งออกไป</summary>
    public int BroadcastChat(ChatMsg msg) => Broadcast(MsgType.ChatMessage, Protocol.ToJson(msg));

    public int BroadcastVoice(VoiceMsg msg) => Broadcast(MsgType.VoiceMessage, Protocol.ToJson(msg));

    /// <summary>ส่งแจ้งเตือน (เสียบ USB, ส่งออกไฟล์, บล็อกโปรแกรม) ไปยัง Master ทุกตัวที่เชื่อมต่ออยู่</summary>
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
                catch { /* การเชื่อมต่อนี้หลุดไปแล้ว */ }
            });
        }
        return count;
    }

    public AgentServer(int port, ECDsa? publicKey, AgentContext ctx)
    {
        _port = port;
        _publicKey = publicKey;
        _ctx = ctx;
    }

    public async Task RunAsync()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // ส่วนใหญ่เกิดจากมีโปรแกรมอื่นใช้พอร์ตนี้อยู่แล้ว (หรือมี Agent อีกตัวทำงานใน session อื่น)
            StartError = $"เปิดพอร์ต {_port} ไม่ได้: {ex.Message}";
            _ctx.ReportError(StartError);
            return;
        }

        // ตอบการค้นหาอัตโนมัติจาก Master (เฉพาะ Master ที่ใช้กุญแจชุดเดียวกัน)
        if (KeyFingerprint is { } fingerprint)
            _ = Task.Run(() => Discovery.RespondAsync(_port, fingerprint, _port, _ctx.ReportError, _cts.Token));

        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                if (!await AuthenticateAsync(stream)) return;

                var session = new Session(stream,
                    (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?");
                _sessions.TryAdd(session, 0);
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var (type, payload) = await Protocol.ReadAsync(stream, _cts.Token);
                        await HandlePacketAsync(session, type, payload);
                    }
                }
                finally
                {
                    _sessions.TryRemove(session, out _);
                }
            }
            catch
            {
                // Master ตัดการเชื่อมต่อหรือข้อมูลผิดรูปแบบ: ปิด connection นี้ไป
            }
        }
    }

    private async Task<bool> AuthenticateAsync(NetworkStream stream)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await Protocol.WriteAsync(stream, MsgType.AuthChallenge, nonce);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (type, signature) = await Protocol.ReadAsync(stream, timeout.Token);

        bool ok = false;
        if (type == MsgType.AuthResponse && _publicKey is not null)
        {
            lock (_keyLock)
                ok = Protocol.Verify(_publicKey, nonce, signature);
        }

        await Protocol.WriteAsync(stream, MsgType.AuthResult, new[] { (byte)(ok ? 1 : 0) });
        if (!ok) await Task.Delay(1000); // หน่วงเวลาเพื่อชะลอการเดาสุ่ม
        return ok;
    }

    private async Task HandlePacketAsync(Session session, MsgType type, byte[] payload)
    {
        switch (type)
        {
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
                _ctx.Lock(Protocol.FromJson<LockCmd>(payload).Text);
                break;

            case MsgType.Unlock:
                _ctx.Unlock();
                break;

            case MsgType.ShowMessage:
            {
                var msg = Protocol.FromJson<MessageCmd>(payload);
                _ctx.ShowMessage(msg.Title, msg.Text);
                break;
            }
            case MsgType.PowerAction:
                PowerActions.Execute(Protocol.FromJson<PowerCmd>(payload).Action);
                break;

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
                _ctx.SetBlocklist(Protocol.FromJson<BlockRules>(payload));
                break;

            case MsgType.RunCommand:
            {
                var run = Protocol.FromJson<RunCmd>(payload);
                try
                {
                    Process.Start(new ProcessStartInfo(run.Target, run.Arguments ?? string.Empty)
                    {
                        UseShellExecute = true, // ทำให้เปิด URL ด้วยเบราว์เซอร์เริ่มต้นได้
                    });
                }
                catch { /* ไม่พบโปรแกรม ฯลฯ */ }
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
        Process.Start(new ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false });
    }
}
