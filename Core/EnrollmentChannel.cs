using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

namespace RainDrop.Shared;

/// <summary>ช่องการส่ง pairing code ผ่าน TLS ด้วย channel binding
/// ออกแบบตามข้อกำหนด C2b:
///   K = HKDF-SHA256(ikm = pairing_code_bytes, salt = channel_binding, info = "RainDrop-enroll-v1")
///   channel_binding = TLS exporter keying material (label "EXPORTER-RainDrop-enroll", 32 ไบต์)
///     ถ้า SslStream ใน .NET 8 ไม่เปิดให้ใช้ exporter → ใช้ SHA-256 ของ public key ของ cert ชั่วคราวฝั่ง Master
///   พิสูจน์สองทิศทาง: ทั้งสองฝั่งส่ง nonce 32 ไบต์ แล้วส่ง HMAC-SHA256(K, label || nonceA || nonceM || transcript_hash)
///   label ต่างกันตามทิศทางกัน reflection transcript_hash ครอบทุกข้อความที่แลกเปลี่ยนกัน downgrade
///   ห้ามส่งรหัสหรือ hash ของรหัสออกในช่อง HMAC</summary>
public static class EnrollmentChannel
{
    public const string Info = "RainDrop-enroll-v1";
    public const string ExporterLabel = "EXPORTER-RainDrop-enroll";
    public const int NonceSize = 32;
    public const int KeySize = 32;
    public const int HmacSize = 32;

    /// <summary>HKDF-SHA256 extract-then-expand ใช้ HKDF.DeriveKey ของ .NET 8 (ไม่เขียน KDF เอง)</summary>
    public static byte[] HkdfSha256(byte[] ikm, byte[] salt, string info, int length)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, salt ?? Array.Empty<byte>(),
            Encoding.UTF8.GetBytes(info), length);
    }

    /// <summary>สร้าง channel binding จาก TLS exporter keying material
    /// คืนค่า null เมื่อ SslStream ไม่รองรับ exporter (caller ต้องใช้ fallback)</summary>
    public static byte[]? DeriveChannelBinding(SslStream ssl)
    {
        try
        {
            // .NET 8 SslStream ไม่มี API โดยตรงสำหรับ TLS exporter (RFC 5705)
            // ถ้าเวอร์ชันถัดไปของ .NET เปิดให้ใช้ ให้เรียกที่นี่
            // ปัจจุบันใช้ fallback: SHA-256 ของ public key ของ cert ฝั่ง Master
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fallback channel binding: SHA-256 ของ public key ของ cert ชั่วคราวฝั่ง Master
    /// Agent คำนวณจาก cert ที่เห็นในการเชื่อมต่อ TLS จริง Master คำนวณจาก cert ของตัวเอง
    /// ถ้ามี MITM ค่าจะต่างและ HMAC จะล้ม</summary>
    public static byte[] DeriveChannelBindingFromCert(ECDsa masterPublicKey)
    {
        var spki = masterPublicKey.ExportSubjectPublicKeyInfo();
        return SHA256.HashData(spki);
    }

    /// <summary>สร้าง transcript hash จากข้อความทั้งหมดที่แลกเปลี่ยนในช่อง enroll</summary>
    public static byte[] TranscriptHash(params byte[][] messages)
    {
        using var combined = new MemoryStream();
        foreach (var m in messages)
            combined.Write(m, 0, m.Length);
        return SHA256.HashData(combined.ToArray());
    }

    /// <summary>สร้าง HMAC proof สำหรับ Agent: HMAC-SHA256(K, "agent" || nonceA || nonceM || transcript_hash)</summary>
    public static byte[] ComputeAgentProof(byte[] key, byte[] nonceA, byte[] nonceM, byte[] transcriptHash)
    {
        using var hmac = new HMACSHA256(key);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("agent"), 0, 5);
        ms.Write(nonceA, 0, nonceA.Length);
        ms.Write(nonceM, 0, nonceM.Length);
        ms.Write(transcriptHash, 0, transcriptHash.Length);
        return hmac.ComputeHash(ms.ToArray());
    }

    /// <summary>สร้าง HMAC proof สำหรับ Master: HMAC-SHA256(K, "master" || nonceM || nonceA || transcript_hash)</summary>
    public static byte[] ComputeMasterProof(byte[] key, byte[] nonceM, byte[] nonceA, byte[] transcriptHash)
    {
        using var hmac = new HMACSHA256(key);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("master"), 0, 6);
        ms.Write(nonceM, 0, nonceM.Length);
        ms.Write(nonceA, 0, nonceA.Length);
        ms.Write(transcriptHash, 0, transcriptHash.Length);
        return hmac.ComputeHash(ms.ToArray());
    }

    /// <summary>ตรวจ proof ด้วย FixedTimeEquals (constant-time comparison)</summary>
    public static bool VerifyProof(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>สร้าง nonce สุ่ม 32 ไบต์</summary>
    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);
}
