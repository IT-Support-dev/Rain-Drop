using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RainDrop.Shared;

/// <summary>รหัสจับคู่ (pairing code) สำหรับการ enroll เครื่องลูกใหม่
/// รหัส 16 ตัวอักษร base32 (80 บิต) สุ่มจาก CSPRNG ใช้ครั้งเดียว หมดอายุ 10 นาที
/// เก็บใน memory ของ Master เท่านั้น ปิดหน้าต่างหรือครบเวลาแล้วใช้ไม่ได้ทันที
/// ผิด 3 ครั้งยกเลิกรหัสทั้งอันทันที พร้อม rate limit ต่อ IP (ตารางมีขีดจำกัดขนาด)</summary>
public sealed class PairingCodeStore : IDisposable
{
    private const int CodeLength = 16;      // base32 ตัวอักษร = 80 บิต
    private const int ExpiryMinutes = 10;
    private const int MaxWrongAttempts = 3;
    private const int MaxEntries = 1000;    // ขีดจำกัดขนาดตารางกัน DoS

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateEntry> _rateByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeProvider _clock;

    private sealed class Entry
    {
        public string Code { get; init; } = "";
        public byte[] CodeBytes { get; init; } = Array.Empty<byte>();
        public DateTime ExpiresAt { get; init; }
        public int WrongAttempts { get; set; }
        public bool Consumed { get; set; }
    }

    private sealed class RateEntry
    {
        public int Failures { get; set; }
        public DateTime BlockedUntil { get; set; }
    }

    public delegate DateTime DateTimeProvider();

    public PairingCodeStore(DateTimeProvider? clock = null)
    {
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    /// <summary>สร้างรหัสจับคู่ใหม่ (หนึ่งรหัสต่อเครื่องที่จะ enroll)</summary>
    public string CreateCode()
    {
        lock (_gate)
        {
            EvictExpired();
            if (_codes.Count >= MaxEntries)
                throw new InvalidOperationException("ตารางรหัสจับคู่เต็ม โปรดลองอีกครั้งภายหลัง");

            var bytes = RandomNumberGenerator.GetBytes(10); // 80 บิต
            string code = Base32Encode(bytes);
            var entry = new Entry
            {
                Code = code,
                CodeBytes = bytes,
                ExpiresAt = _clock().AddMinutes(ExpiryMinutes),
            };
            _codes[code] = entry;
            return code;
        }
    }


    /// <summary>ตรวจว่ารหัสถูกต้องและยังใช้งานได้โดยไม่ทำให้ถูกใช้ (สำหรับตรวจก่อนเข้ารหัสขั้นตอน crypto)</summary>
    public bool TryPeek(string code, out byte[]? codeBytes, out string? reason)
    {
        codeBytes = null;
        reason = null;
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out var entry))
            {
                reason = "รหัสไม่ถูกต้องหรือหมดอายุแล้ว";
                return false;
            }

            if (entry.WrongAttempts >= MaxWrongAttempts)
            {
                reason = "รหัสถูกยกเลิกเพราะผิดเกินกำหนด";
                return false;
            }

            if (entry.Consumed)
            {
                reason = "รหัสถูกใช้ไปแล้ว";
                return false;
            }

            if (_clock() > entry.ExpiresAt)
            {
                reason = "รหัสหมดอายุแล้ว";
                return false;
            }

            codeBytes = entry.CodeBytes;
            return true;
        }
    }

    /// <summary>ตรวจสอบและใช้รหัส (consume) คืน true เมื่อรหัสถูกต้องและยังไม่หมดอายุ/ยังไม่ถูกใช้</summary>
    public bool TryConsume(string code, out byte[]? codeBytes, out string? reason)
    {
        codeBytes = null;
        reason = null;
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out var entry))
            {
                reason = "รหัสไม่ถูกต้องหรือหมดอายุแล้ว";
                return false;
            }

            if (entry.Consumed)
            {
                reason = "รหัสถูกใช้ไปแล้ว";
                return false;
            }

            if (_clock() > entry.ExpiresAt)
            {
                entry.Consumed = true; // กันการใช้ซ้ำหลังหมดอายุ
                reason = "รหัสหมดอายุแล้ว";
                return false;
            }

            if (entry.WrongAttempts >= MaxWrongAttempts)
            {
                reason = "รหัสถูกยกเลิกเพราะผิดเกินกำหนด";
                return false;
            }

            entry.Consumed = true;
            codeBytes = entry.CodeBytes;
            return true;
        }
    }

    /// <summary>บันทึกความผิดพลาดสำหรับรหัสนี้ (ผิด 3 ครั้งยกเลิกทั้งอัน)</summary>
    public void RecordWrong(string code)
    {
        lock (_gate)
        {
            if (_codes.TryGetValue(code, out var entry))
            {
                entry.WrongAttempts++;
                if (entry.WrongAttempts >= MaxWrongAttempts)
                    entry.Consumed = true; // ยกเลิกรหัสทันที
            }
        }
    }

    /// <summary>บันทึกความผิดพลาดต่อ IP สำหรับ rate limit</summary>
    public void RecordIpFailure(string ip)
    {
        var entry = _rateByIp.AddOrUpdate(ip,
            _ => new RateEntry { Failures = 1, BlockedUntil = DateTime.MinValue },
            (_, existing) =>
            {
                existing.Failures++;
                if (existing.Failures >= MaxWrongAttempts)
                    existing.BlockedUntil = _clock().AddMinutes(ExpiryMinutes);
                return existing;
            });
    }

    /// <summary>ตรวจว่า IP นี้ถูก block จาก rate limit หรือไม่</summary>
    public bool IsIpBlocked(string ip, out int? retryAfterSeconds)
    {
        retryAfterSeconds = null;
        if (!_rateByIp.TryGetValue(ip, out var entry)) return false;
        if (entry.BlockedUntil != DateTime.MinValue && _clock() < entry.BlockedUntil)
        {
            retryAfterSeconds = (int)Math.Ceiling((entry.BlockedUntil - _clock()).TotalSeconds);
            return true;
        }
        return false;
    }

    private void EvictExpired()
    {
        var now = _clock();
        // ลบรายการที่หมดอายุแล้ว หรือถูกใช้ไปแล้ว (cleanup ตารางเพื่อกัน DoS)
        var expired = _codes.Where(kv => kv.Value.ExpiresAt < now || kv.Value.Consumed).Select(kv => kv.Key).ToList();
        foreach (var key in expired) _codes.Remove(key);
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                EvictExpired();
                return _codes.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) _codes.Clear();
    }

    // ---------- base32 (RFC 4648, ตัวอักษร A-Z 2-7 ไม่มีตัวเลข 0/1 ที่คล้ายกัน) ----------

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(value >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(Base32Alphabet[(value << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}
