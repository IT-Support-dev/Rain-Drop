using System.Text.RegularExpressions;

namespace RainDrop.Shared;

/// <summary>
/// ตรวจชื่อโฮสต์อย่างเข้มงวดก่อนนำไปเขียนลงไฟล์ hosts
/// เหตุผลด้านความปลอดภัย: ถ้าปล่อยอักขระอย่าง newline / space / '#' / ';' หลุดไป
/// ผู้ใช้ที่คุมข้อความได้จะแทรกบรรทัดใหม่ลง hosts เช่นชี้โดเมนธนาคารไปที่ IP ของตัว
/// จึงยอมให้เหลือเฉพาะ [a-z0-9.-] และตรวจรูปแบบราย label ตามกฎ DNS
/// </summary>
public static class Hostname
{
    public const int MaxLength = 253;
    public const int MaxLabelLength = 63;

    // label: ขึ้นต้นและลงท้ายด้วยตัวอักษร/เลข กลางมีขีดได้ ยาว 1..63
    private static readonly Regex LabelPattern =
        new(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant);

    /// <summary>ต้องเป็นตัวพิมพ์เล็กแล้ว และไม่มีอักขระนอกเซตที่อนุญาต</summary>
    public static bool IsValid(string host)
    {
        if (host.Length is 0 or > MaxLength) return false;

        foreach (char c in host)
        {
            bool allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-';
            if (!allowed) return false;   // รวม newline, tab, space, '#', ';', '/', '\', ':', อักขระควบคุม, ยูนิโค้ด
        }

        string[] labels = host.Split('.');
        bool allNumeric = true;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength) return false;   // กัน "a..b", ".a", "a."
            if (!LabelPattern.IsMatch(label)) return false;            // กัน "-a", "a-"
            if (!label.All(char.IsAsciiDigit)) allNumeric = false;
        }

        // IPv4 literal ไม่ใช่ชื่อโฮสต์ และเขียนลง hosts เพื่อ "บล็อก" ไม่ได้อยู่แล้ว
        if (allNumeric) return false;

        return true;
    }

    /// <summary>
    /// แปลงค่าที่ผู้ดูแลกรอก (อาจเป็น URL เต็ม) ให้เป็นชื่อโฮสต์ที่ใช้ได้
    /// คืน false เมื่อรูปผิดหรือมีอักขระอันตราย — ผู้เรียกต้องทิ้งค่านั้น ไม่ใช่เขียนลง hosts
    /// </summary>
    public static bool TryFromUrl(string? raw, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();

        // ปฏิเสธอักขระควบคุมตั้งแต่ต้นทาง แม้จะอยู่ในส่วน path/query ที่จะถูกตัดทิ้งภายหลัง
        // เพราะการมีอยู่ของมันแปลว่า input ไม่ใช่ URL ที่ผู้ดูแลตั้งใจกรอกตามปกติ
        foreach (char c in s)
            if (char.IsControl(c)) return false;

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        // ตัด path/query/fragment ก่อนเสมอ ไม่งั้น "example.com/foo@bar" จะถูกเข้าใจผิดว่า bar คือโฮสต์
        int cut = s.AsSpan().IndexOfAny('/', '?', '#');
        if (cut >= 0) s = s[..cut];

        int at = s.IndexOf('@');                       // ตัด userinfo (กัน "good.com@evil.com" -> โฮสต์จริงคือ evil.com)
        if (at >= 0) s = s[(at + 1)..];

        int colon = s.IndexOf(':');                    // ตัดพอร์ต
        if (colon >= 0) s = s[..colon];

        s = s.Trim().ToLowerInvariant();
        if (!IsValid(s)) return false;

        host = s;
        return true;
    }
}
