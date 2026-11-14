using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RainDrop.Shared;

namespace RainDrop.Hub;

/// <summary>
/// เซิร์ฟเวอร์กลางแบบ Discord: ทุกเครื่อง (ผู้ดูแล + เครื่องลูก) ต่อเข้ามาที่นี่
/// หน้าที่: จัดการรายชื่อสมาชิก (roster), relay ข้อความแชท/ไฟล์/เสียงไปยังสมาชิกคนอื่น
/// และเก็บประวัติแชทล่าสุดของแต่ละช่องไว้ให้คนที่เข้ามาทีหลังเห็น
/// </summary>
public sealed class HubServer
{
    private readonly int _port;
    private readonly string _expectedFingerprint;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, HubSession> _sessions = new();
    private readonly List<ChannelInfo> _channels;
    private readonly ConcurrentDictionary<string, Queue<ChatPost>> _history = new();
    private int _nextId;

    private const int HistoryPerChannel = 50;

    public HubServer(int port, string expectedFingerprint, Action<string> log)
    {
        _port = port;
        _expectedFingerprint = expectedFingerprint;
        _log = log;
        _channels = new List<ChannelInfo>
        {
            new("general", "ทั่วไป", ChannelKind.Text),
            new("announce", "ประกาศ", ChannelKind.Text),
            new("help", "ขอความช่วยเหลือ", ChannelKind.Text),
            new("voice-main", "ห้องเสียงหลัก", ChannelKind.Voice),
            new("voice-group", "ห้องเสียงกลุ่ม", ChannelKind.Voice),
        };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log($"Hub กำลังรอการเชื่อมต่อที่พอร์ต {_port}");

        using var reg = ct.Register(listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            HubSession? session = null;
            try
            {
                var (type, payload) = await HubProtocol.ReadAsync(stream, ct);
                if (type != HubMsg.Hello) return;
                var hello = HubProtocol.FromJson<HelloMsg>(payload);

                if (hello.CommunityFingerprint != _expectedFingerprint)
                {
                    await HubProtocol.WriteJsonAsync(stream, HubMsg.Rejected,
                        "ชุมชนไม่ตรงกัน (กุญแจคนละชุดกับเซิร์ฟเวอร์)", ct);
                    return;
                }

                int id = Interlocked.Increment(ref _nextId);
                session = new HubSession(id, hello.Name, hello.Role, stream);
                _sessions[id] = session;
                _log($"เข้าร่วม: {hello.Name} ({hello.Role}) #{id}  รวม {_sessions.Count} คน");

                await session.SendJsonAsync(HubMsg.Welcome,
                    new WelcomeMsg(id, _channels, Roster(), RecentHistory()), ct);
                await BroadcastRosterAsync(ct);
                await BroadcastVoiceRosterAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    var (mt, mp) = await HubProtocol.ReadAsync(stream, ct);
                    await DispatchAsync(session, mt, mp, ct);
                }
            }
            catch
            {
                // การเชื่อมต่อหลุดหรือข้อมูลผิดรูปแบบ
            }
            finally
            {
                if (session is not null)
                {
                    _sessions.TryRemove(session.Id, out _);
                    _log($"ออก: {session.Name} #{session.Id}  เหลือ {_sessions.Count} คน");
                    await BroadcastRosterAsync(ct);
                    await BroadcastVoiceRosterAsync(ct);
                }
            }
        }
    }

    private async Task DispatchAsync(HubSession from, HubMsg type, byte[] payload, CancellationToken ct)
    {
        switch (type)
        {
            case HubMsg.Chat:
            {
                var post = HubProtocol.FromJson<ChatPost>(payload) with { SenderId = from.Id, SenderName = from.Name, Time = DateTime.Now };
                StoreHistory(post);
                await BroadcastAsync(HubMsg.Chat, HubProtocol.SerializeChat(post), ct); // ส่งให้ทุกคนรวมผู้ส่ง (ยืนยันว่าถึงแล้ว)
                break;
            }
            case HubMsg.FileChunk:
                // relay ไฟล์ไปยังทุกคน (ยกเว้นผู้ส่ง) ไคลเอนต์ปลายทางประกอบกลับเอง
                await BroadcastAsync(HubMsg.FileChunk, payload, ct, except: from.Id);
                break;

            case HubMsg.Presence:
                from.State = HubProtocol.FromJson<PresenceMsg>(payload).State;
                await BroadcastRosterAsync(ct);
                break;

            case HubMsg.VoiceJoin:
                from.VoiceChannelId = HubProtocol.FromJson<VoiceJoinMsg>(payload).ChannelId;
                await BroadcastRosterAsync(ct);
                await BroadcastVoiceRosterAsync(ct);
                break;

            case HubMsg.VoiceFrame:
            {
                // relay เสียงไปยังคนอื่นที่อยู่ในช่องเสียงเดียวกันเท่านั้น
                if (from.VoiceChannelId is null) break;
                var frame = HubProtocol.MakeVoiceFrame(from.Id, payload);
                foreach (var s in _sessions.Values)
                    if (s.Id != from.Id && s.VoiceChannelId == from.VoiceChannelId)
                        await s.SendAsync(HubMsg.VoiceFrame, frame, ct);
                break;
            }
            case HubMsg.Ping:
                break;
        }
    }

    // ---------- roster / history ----------

    private List<MemberInfo> Roster() =>
        _sessions.Values.OrderBy(s => s.Name)
            .Select(s => new MemberInfo(s.Id, s.Name, s.Role, s.State, s.VoiceChannelId)).ToList();

    private async Task BroadcastRosterAsync(CancellationToken ct) =>
        await BroadcastAsync(HubMsg.Roster, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new RosterMsg(Roster())), ct);

    private async Task BroadcastVoiceRosterAsync(CancellationToken ct)
    {
        var byChannel = _sessions.Values
            .Where(s => s.VoiceChannelId is not null)
            .GroupBy(s => s.VoiceChannelId!)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToList());
        await BroadcastAsync(HubMsg.VoiceRoster,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new VoiceRosterMsg(byChannel)), ct);
    }

    private void StoreHistory(ChatPost post)
    {
        var q = _history.GetOrAdd(post.ChannelId, _ => new Queue<ChatPost>());
        lock (q)
        {
            q.Enqueue(post);
            while (q.Count > HistoryPerChannel) q.Dequeue();
        }
    }

    private List<ChatPost> RecentHistory()
    {
        var all = new List<ChatPost>();
        foreach (var q in _history.Values)
            lock (q) all.AddRange(q);
        return all.OrderBy(p => p.Time).ToList();
    }

    private async Task BroadcastAsync(HubMsg type, byte[] payload, CancellationToken ct, int except = -1)
    {
        foreach (var s in _sessions.Values)
        {
            if (s.Id == except) continue;
            try { await s.SendAsync(type, payload, ct); }
            catch { /* คนนี้หลุด เดี๋ยวลูปอ่านของเขาจะจัดการเอง */ }
        }
    }
}

/// <summary>หนึ่งการเชื่อมต่อของสมาชิก มีล็อกกันการเขียนชนกัน (เสียงกับแชทส่งพร้อมกันได้)</summary>
internal sealed class HubSession
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public int Id { get; }
    public string Name { get; }
    public MemberRole Role { get; }
    public PresenceState State { get; set; } = PresenceState.Online;
    public string? VoiceChannelId { get; set; }

    public HubSession(int id, string name, MemberRole role, Stream stream)
    {
        Id = id;
        Name = name;
        Role = role;
        _stream = stream;
    }

    public async Task SendAsync(HubMsg type, byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await HubProtocol.WriteAsync(_stream, type, payload, ct); }
        finally { _writeLock.Release(); }
    }

    public Task SendJsonAsync<T>(HubMsg type, T obj, CancellationToken ct) =>
        SendAsync(type, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj), ct);
}
