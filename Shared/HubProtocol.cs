using System.Buffers.Binary;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>
/// โปรโตคอลของ "เซิร์ฟเวอร์กลาง" (Hub) แบบ Discord แยกจากโปรโตคอลควบคุม (Protocol.cs)
/// รูปแบบเฟรม: [1 ไบต์ชนิด][4 ไบต์ความยาว little-endian][payload]
/// - payload ของข้อความทั่วไปเป็น JSON (UTF-8)
/// - payload ของเสียง/ไฟล์เป็นไบนารีล้วน เพื่อความเร็ว (เสียงส่ง ~50 ครั้ง/วินาที)
/// </summary>
public enum HubMsg : byte
{
    // ไคลเอนต์ -> Hub ตอนเชื่อมต่อ
    Hello = 1,       // HelloMsg (JSON): ชื่อ, บทบาท, ลายนิ้วมือกุญแจชุมชน
    Welcome = 2,     // WelcomeMsg (JSON): id ของเรา + รายชื่อช่อง + roster + ประวัติล่าสุด
    Rejected = 3,    // string (JSON): เหตุผลที่ถูกปฏิเสธ

    // สถานะสมาชิก
    Roster = 10,     // RosterMsg (JSON): รายชื่อสมาชิกทั้งหมดและสถานะ (ส่งใหม่ทุกครั้งที่เปลี่ยน)
    Presence = 11,   // PresenceMsg (JSON): ไคลเอนต์แจ้งเปลี่ยนสถานะ (ออนไลน์/ไม่อยู่)

    // แชทข้อความ
    Chat = 20,       // ChatPost (JSON): โพสต์ลงช่องข้อความ (อาจแนบไฟล์)
    FileChunk = 21,  // ไบนารี: [16 ไบต์ fileId][4 ไบต์ seq][1 ไบต์ last][data...]

    // คอลเสียง
    VoiceJoin = 30,  // VoiceJoinMsg (JSON): เข้าช่องเสียง (channelId) หรือออก (channelId ว่าง)
    VoiceRoster = 31,// VoiceRosterMsg (JSON): ใครอยู่ในช่องเสียงไหนบ้าง
    VoiceFrame = 32, // ไบนารี: [4 ไบต์ senderId][PCM 16-bit mono 16kHz]

    Ping = 40,       // ว่าง: กันการเชื่อมต่อหลุด
}

public enum MemberRole { Admin, Client }
public enum PresenceState { Online, Away }
public enum ChannelKind { Text, Voice }

public record ChannelInfo(string Id, string Name, ChannelKind Kind);

public record MemberInfo(int Id, string Name, MemberRole Role, PresenceState State, string? VoiceChannelId);

public record HelloMsg(string Name, MemberRole Role, string CommunityFingerprint, string Version);

public record WelcomeMsg(int SelfId, List<ChannelInfo> Channels, List<MemberInfo> Members, List<ChatPost> History);

public record RosterMsg(List<MemberInfo> Members);

public record PresenceMsg(PresenceState State);

public record AttachmentInfo(string FileId, string FileName, long Size, bool IsImage);

public record ChatPost(string ChannelId, int SenderId, string SenderName, string Text, DateTime Time, AttachmentInfo? Attachment);

public record VoiceJoinMsg(string? ChannelId);

public record VoiceRosterMsg(Dictionary<string, List<int>> ByChannel);

public static class HubProtocol
{
    public const int DefaultPort = 11500;
    public const int VoiceSampleRate = 16000;
    public const int FileChunkSize = 48 * 1024;
    public const long MaxFileSize = 25L * 1024 * 1024;
    private const int MaxPayload = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, HubMsg type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var buffer = new byte[5 + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), payload.Length);
        payload.Span.CopyTo(buffer.AsSpan(5));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(Stream stream, HubMsg type, T obj, CancellationToken ct = default) =>
        WriteAsync(stream, type, JsonSerializer.SerializeToUtf8Bytes(obj), ct);

    public static async Task<(HubMsg Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayload)
            throw new InvalidDataException($"ขนาดเฟรมไม่ถูกต้อง: {length}");

        var payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return ((HubMsg)header[0], payload);
    }

    public static T FromJson<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("JSON ว่าง");

    public static byte[] SerializeChat(ChatPost post) => JsonSerializer.SerializeToUtf8Bytes(post);

    // ---------- ตัวช่วยสำหรับ payload ไบนารีของเสียง ----------

    public static byte[] MakeVoiceFrame(int senderId, ReadOnlySpan<byte> pcm)
    {
        var buf = new byte[4 + pcm.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, senderId);
        pcm.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static (int SenderId, byte[] Pcm) ReadVoiceFrame(byte[] payload)
    {
        int sender = BinaryPrimitives.ReadInt32LittleEndian(payload);
        return (sender, payload[4..]);
    }

    // ---------- ตัวช่วยสำหรับ payload ไบนารีของไฟล์ ----------

    public static byte[] MakeFileChunk(Guid fileId, int seq, bool last, ReadOnlySpan<byte> data)
    {
        var buf = new byte[16 + 4 + 1 + data.Length];
        fileId.TryWriteBytes(buf.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), seq);
        buf[20] = (byte)(last ? 1 : 0);
        data.CopyTo(buf.AsSpan(21));
        return buf;
    }

    public static (Guid FileId, int Seq, bool Last, byte[] Data) ReadFileChunk(byte[] payload)
    {
        var id = new Guid(payload.AsSpan(0, 16));
        int seq = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(16, 4));
        bool last = payload[20] != 0;
        return (id, seq, last, payload[21..]);
    }
}
