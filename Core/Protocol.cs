using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>
/// ชนิดของข้อความที่รับส่งระหว่าง Master กับ Agent
/// รูปแบบแพ็กเก็ต: [1 byte MsgType][4 byte ความยาว (little-endian)][payload]
/// </summary>
public enum MsgType : byte
{
    // ยืนยันตัวตน
    AuthChallenge = 1,   // Agent -> Master : nonce 32 ไบต์
    AuthResponse = 2,    // Master -> Agent : ลายเซ็น ECDSA ของ nonce
    AuthResult = 3,      // Agent -> Master : 1 = ผ่าน, 0 = ไม่ผ่าน

    // หน้าจอ
    ScreenRequest = 10,  // Master -> Agent : ScreenRequest (JSON)
    ScreenFrame = 11,    // Agent -> Master : ภาพ JPEG

    // ควบคุมระยะไกล
    MouseEvent = 20,     // MouseCmd (JSON)
    KeyEvent = 21,       // KeyCmd (JSON)

    // คำสั่ง
    Lock = 30,           // LockCmd (JSON)
    Unlock = 31,
    ShowMessage = 32,    // MessageCmd (JSON)
    PowerAction = 40,    // PowerCmd (JSON)
    RunCommand = 41,     // RunCmd (JSON)

    // แชท (ส่งได้ทั้งสองทิศทาง)
    ChatMessage = 50,    // ChatMsg (JSON)
    VoiceMessage = 51,   // VoiceMsg (JSON, เสียงเป็นไฟล์ WAV เข้ารหัส base64)

    // ตรวจสอบกิจกรรมการใช้งาน
    ActivityRequest = 60,  // Master -> Agent : ว่าง
    ActivitySnapshot = 61, // Agent -> Master : ActivitySnapshot (JSON)

    // แจ้งเตือน และ บล็อกโปรแกรม/เว็บ
    Alert = 62,            // Agent -> Master : AlertMsg (JSON) ส่งเองเมื่อมีเหตุการณ์
    SetBlocklist = 63,     // Master -> Agent : BlockRules (JSON) ตั้งรายการบล็อก
}

/// <summary>ข้อความแชท ใช้ได้ทั้ง Master -> Agent และ Agent -> Master</summary>
public record ChatMsg(string Sender, string Text, DateTime Time);

/// <summary>ข้อความเสียง: ไฟล์ WAV (PCM 16-bit mono 16 kHz ประมาณ 32 KB ต่อวินาที)</summary>
public record VoiceMsg(string Sender, DateTime Time, double Seconds, byte[] Wav);

public record ScreenRequest(int MaxWidth, int Quality);

public enum MouseAction { Move, Down, Up, Wheel }

/// <summary>X, Y เป็นค่าสัดส่วน 0..1 ของหน้าจอทั้งหมด (รวมทุกจอ) เพื่อไม่ต้องสนใจความละเอียดจริง</summary>
public record MouseCmd(double X, double Y, MouseAction Action, int Button, int Delta);

public record KeyCmd(int VirtualKey, bool Down);

public record LockCmd(string Text);

public record MessageCmd(string Title, string Text);

public enum PowerAction { Shutdown, Restart, Logoff }

public record PowerCmd(PowerAction Action);

/// <summary>Target เป็น URL หรือพาธโปรแกรมก็ได้</summary>
public record RunCmd(string Target, string? Arguments);

// ---------- ข้อมูลกิจกรรมการใช้งาน ----------

/// <summary>เวลาที่ใช้แต่ละโปรแกรม (สะสมตั้งแต่ Agent เริ่มทำงาน)</summary>
public record AppUsage(string Process, string Title, int ActiveSeconds);

/// <summary>เหตุการณ์ไฟล์: Kind = "ดาวน์โหลด" หรือ "ส่งออก (USB)"</summary>
public record FileEvent(DateTime Time, string Kind, string Path);

/// <summary>การแจ้งเตือนที่ Agent ส่งหา Master เอง: Kind = "USB", "ส่งออกไฟล์", "ดาวน์โหลด", "บล็อกโปรแกรม"</summary>
public record AlertMsg(string Kind, string Text, DateTime Time);

/// <summary>รายการบล็อก: ชื่อโปรแกรม (ไม่ต้องมี .exe) และโดเมน/ลิงก์ที่ห้ามเข้า</summary>
public record BlockRules(List<string> Programs, List<string> Urls);

/// <summary>สรุปกิจกรรม ณ เวลาหนึ่งของเครื่องลูกหนึ่งเครื่อง</summary>
public record ActivitySnapshot(
    string ForegroundProcess,
    string ForegroundTitle,
    List<string> RunningApps,
    List<AppUsage> Usage,
    List<FileEvent> FileEvents,
    DateTime StartedAt,
    DateTime SampledAt);

public static class Protocol
{
    public const int DefaultPort = 11400;
    private const int MaxPayload = 32 * 1024 * 1024;
    private static readonly byte[] AuthContext = Encoding.ASCII.GetBytes("RainDrop-auth-v1:");

    public static async Task WriteAsync(Stream stream, MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // รวม header + payload ไว้ในบัฟเฟอร์เดียว เพื่อส่งเป็นก้อนเดียว
        var buffer = new byte[5 + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), payload.Length);
        payload.Span.CopyTo(buffer.AsSpan(5));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(MsgType Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayload)
            throw new InvalidDataException($"ขนาดแพ็กเก็ตไม่ถูกต้อง: {length}");

        var payload = new byte[length];
        if (length > 0)
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return ((MsgType)header[0], payload);
    }

    public static byte[] ToJson<T>(T obj) => JsonSerializer.SerializeToUtf8Bytes(obj);

    public static T FromJson<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("JSON ว่าง");

    // ---------- ยืนยันตัวตนด้วยคู่กุญแจ ECDSA (public key อยู่ที่เครื่องลูก, private key อยู่ที่เครื่องแม่) ----------
    // Master ถือ private key, Agent ถือแค่ public key
    // ผู้ใช้เครื่องลูกจึงอ่านไฟล์ของ Agent ได้โดยไม่สามารถปลอมตัวเป็น Master ได้

    private static byte[] AuthData(byte[] nonce)
    {
        var data = new byte[AuthContext.Length + nonce.Length];
        AuthContext.CopyTo(data, 0);
        nonce.CopyTo(data, AuthContext.Length);
        return data;
    }

    public static byte[] Sign(ECDsa privateKey, byte[] nonce) =>
        privateKey.SignData(AuthData(nonce), HashAlgorithmName.SHA256);

    /// <summary>ลายนิ้วมือของกุญแจสาธารณะ ใช้เทียบด้วยตาว่า Master กับ Agent ใช้กุญแจชุดเดียวกัน</summary>
    public static string Fingerprint(ECDsa key)
    {
        var hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        return string.Join(":", hash.Take(6).Select(b => b.ToString("X2")));
    }

    public static bool Verify(ECDsa publicKey, byte[] nonce, byte[] signature) =>
        publicKey.VerifyData(AuthData(nonce), signature, HashAlgorithmName.SHA256);
}
