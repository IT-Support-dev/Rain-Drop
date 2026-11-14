using System.Net.Sockets;
using System.Threading.Channels;

namespace RainDrop.Shared;

/// <summary>
/// ตัวเชื่อมต่อเซิร์ฟเวอร์กลาง (Hub) ฝั่งไคลเอนต์ ใช้ร่วมกันทั้งผู้ดูแลและเครื่องลูก
/// เหตุการณ์ทั้งหมดถูกเรียกจาก thread พื้นหลัง ผู้ใช้ UI ต้อง BeginInvoke กลับเอง
/// </summary>
public sealed class HubClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly HelloMsg _hello;
    private readonly Channel<(HubMsg, byte[])> _outbox =
        Channel.CreateUnbounded<(HubMsg, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;

    private int _failed;
    public int SelfId { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action? Connected;
    public event Action<string>? Disconnected;      // ข้อความสาเหตุ
    public event Action<WelcomeMsg>? Welcomed;
    public event Action<List<MemberInfo>>? RosterChanged;
    public event Action<ChatPost>? ChatReceived;
    public event Action<VoiceRosterMsg>? VoiceRosterChanged;
    public event Action<int, byte[]>? VoiceFrameReceived;                 // senderId, pcm
    public event Action<Guid, int, bool, byte[]>? FileChunkReceived;      // fileId, seq, last, data

    public HubClient(string host, int port, string name, MemberRole role, string communityFingerprint)
    {
        _host = host;
        _port = port;
        _hello = new HelloMsg(name, role, communityFingerprint, "1.0");
    }

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(6));
            await _tcp.ConnectAsync(_host, _port, connectCts.Token);

            var stream = _tcp.GetStream();
            await HubProtocol.WriteJsonAsync(stream, HubMsg.Hello, _hello, _cts.Token);

            IsConnected = true;
            Connected?.Invoke();
            _ = Task.Run(() => WriterLoopAsync(stream));
            _ = Task.Run(PingLoopAsync);

            while (!_cts.IsCancellationRequested)
            {
                var (type, payload) = await HubProtocol.ReadAsync(stream, _cts.Token);
                Handle(type, payload);
            }
        }
        catch (Exception ex)
        {
            Fail(ex is SocketException ? "เชื่อมต่อเซิร์ฟเวอร์ไม่ได้ (ตรวจที่อยู่/พอร์ต และว่าเปิด Hub แล้ว)" : ex.Message);
        }
    }

    private void Handle(HubMsg type, byte[] payload)
    {
        switch (type)
        {
            case HubMsg.Welcome:
            {
                var w = HubProtocol.FromJson<WelcomeMsg>(payload);
                SelfId = w.SelfId;
                Welcomed?.Invoke(w);
                break;
            }
            case HubMsg.Rejected:
                Fail(HubProtocol.FromJson<string>(payload));
                break;
            case HubMsg.Roster:
                RosterChanged?.Invoke(HubProtocol.FromJson<RosterMsg>(payload).Members);
                break;
            case HubMsg.Chat:
                ChatReceived?.Invoke(HubProtocol.FromJson<ChatPost>(payload));
                break;
            case HubMsg.VoiceRoster:
                VoiceRosterChanged?.Invoke(HubProtocol.FromJson<VoiceRosterMsg>(payload));
                break;
            case HubMsg.VoiceFrame:
            {
                var (sender, pcm) = HubProtocol.ReadVoiceFrame(payload);
                VoiceFrameReceived?.Invoke(sender, pcm);
                break;
            }
            case HubMsg.FileChunk:
            {
                var (id, seq, last, data) = HubProtocol.ReadFileChunk(payload);
                FileChunkReceived?.Invoke(id, seq, last, data);
                break;
            }
        }
    }

    private async Task WriterLoopAsync(Stream stream)
    {
        try
        {
            await foreach (var (type, payload) in _outbox.Reader.ReadAllAsync(_cts.Token))
                await HubProtocol.WriteAsync(stream, type, payload, _cts.Token);
        }
        catch { Fail("การเชื่อมต่อขาด"); }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token);
                Send(HubMsg.Ping, Array.Empty<byte>());
            }
        }
        catch
        {
            // จงใจเพิกเฉย: ลูป ping สิ้นสุดเมื่อถูกยกเลิก (ปิดการเชื่อมต่อ) หรือส่งไม่ออกเพราะสายหลุด
            // ทั้งสองกรณีเป็นทางออกปกติของลูป สถานะการเชื่อมต่อถูกจัดการโดย Fail/reader loop แล้ว
        }
    }

    private void Fail(string reason)
    {
        // แจ้งเพียงครั้งเดียว ไม่ว่าจะล้มตอนเชื่อมต่อแรกหรือหลุดระหว่างทาง
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        IsConnected = false;
        Disconnected?.Invoke(reason);
    }

    // ---------- ส่งออก ----------

    public void Send(HubMsg type, byte[] payload)
    {
        if (!_cts.IsCancellationRequested) _outbox.Writer.TryWrite((type, payload));
    }

    public void SendJson<T>(HubMsg type, T obj) =>
        Send(type, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj));

    public void PostChat(string channelId, string text, AttachmentInfo? attachment = null) =>
        SendJson(HubMsg.Chat, new ChatPost(channelId, 0, "", text, DateTime.Now, attachment));

    public void SetPresence(PresenceState state) => SendJson(HubMsg.Presence, new PresenceMsg(state));

    public void JoinVoice(string? channelId) => SendJson(HubMsg.VoiceJoin, new VoiceJoinMsg(channelId));

    public void SendVoice(ReadOnlySpan<byte> pcm) => Send(HubMsg.VoiceFrame, pcm.ToArray());

    public void SendFileChunk(Guid fileId, int seq, bool last, ReadOnlySpan<byte> data) =>
        Send(HubMsg.FileChunk, HubProtocol.MakeFileChunk(fileId, seq, last, data));

    public void Dispose()
    {
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        _tcp?.Dispose();
    }
}
