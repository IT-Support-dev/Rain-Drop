using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RainDrop.AgentService;

/// <summary>
/// บล็อกเว็บ/ลิงก์ด้วยการแก้ไฟล์ hosts ของ Windows (ต้องใช้สิทธิ์ SYSTEM ซึ่ง Service มี)
/// อ่านรายการจาก blocklist.json ที่ Agent เขียนไว้ แล้วใส่บรรทัด "0.0.0.0 โดเมน"
/// ไว้ระหว่างเครื่องหมาย # RainDrop-BEGIN / # RainDrop-END เท่านั้น (ไม่ยุ่งกับบรรทัดอื่น)
/// </summary>
internal static class HostsBlocker
{
    private const string Begin = "# RainDrop-BEGIN (จัดการโดยอัตโนมัติ ห้ามแก้)";
    private const string End = "# RainDrop-END";

    private static readonly string HostsPath = Path.Combine(
        Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
        @"System32\drivers\etc\hosts");

    private static readonly string BlocklistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RainDrop", "blocklist.json");

    private sealed class Rules { public List<string>? Programs { get; set; } public List<string>? Urls { get; set; } }

    private static string _lastApplied = "\0"; // กันเขียนซ้ำถ้าไม่มีอะไรเปลี่ยน

    /// <summary>ลบบล็อกของเราออกจาก hosts (เรียกตอน Service หยุดอย่างถูกต้อง)</summary>
    public static void Clear(Action<string> log)
    {
        try { WriteHosts(""); FlushDns(); log("ล้างการบล็อกเว็บออกจากไฟล์ hosts แล้ว"); }
        catch (Exception ex) { log("ล้าง hosts ไม่สำเร็จ: " + ex.Message); }
    }

    public static void Sync(Action<string> log)
    {
        List<string> domains = LoadDomains(log);
        string block = BuildBlock(domains);
        if (block == _lastApplied) return;

        try
        {
            WriteHosts(block);
            FlushDns();
            _lastApplied = block;
            log($"อัปเดตการบล็อกเว็บในไฟล์ hosts: {domains.Count} โดเมน");
        }
        catch (Exception ex)
        {
            log("แก้ไฟล์ hosts ไม่สำเร็จ: " + ex.Message);
        }
    }

    private static List<string> LoadDomains(Action<string> log)
    {
        var domains = new List<string>();
        try
        {
            if (!File.Exists(BlocklistPath)) return domains;
            var rules = JsonSerializer.Deserialize<Rules>(File.ReadAllText(BlocklistPath));
            foreach (var raw in rules?.Urls ?? new List<string>())
            {
                string host = ToHost(raw);
                if (host.Length == 0) continue;
                domains.Add(host);
                if (!host.StartsWith("www.")) domains.Add("www." + host);
            }
        }
        catch (Exception ex)
        {
            // อันตรายถ้าเงียบ: อ่านรายการไม่ได้ => ได้รายการว่าง => BuildBlock("") => ลบทุกโดเมนออกจาก hosts
            // เท่ากับเว็บที่เคยบล็อกถูกปลดโดยไม่มีใครสั่ง จึงต้องแจ้งเข้า log ของ Service เสมอ
            log("อ่านรายการบล็อกเว็บไม่สำเร็จ รอบนี้อาจปลดบล็อกเว็บชั่วคราว: " + ex.Message);
        }
        return domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>ตัด scheme, path, พอร์ต ออกให้เหลือแต่โดเมน เช่น "https://www.facebook.com/x" -> "www.facebook.com"</summary>
    internal static string ToHost(string raw)
    {
        string s = raw.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        return s.Trim().ToLowerInvariant();
    }

    internal static string BuildBlock(List<string> domains)
    {
        if (domains.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(Begin);
        foreach (var d in domains)
        {
            sb.AppendLine($"0.0.0.0 {d}");
            sb.AppendLine($":: {d}"); // IPv6 ด้วย
        }
        sb.Append(End);
        return sb.ToString();
    }

    private static void WriteHosts(string block)
    {
        string content = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";

        // ตัดบล็อกเก่าของเราออกก่อน
        int bi = content.IndexOf(Begin, StringComparison.Ordinal);
        int ei = content.IndexOf(End, StringComparison.Ordinal);
        if (bi >= 0 && ei > bi)
        {
            int endPos = ei + End.Length;
            content = (content[..bi] + content[endPos..]).TrimEnd() + Environment.NewLine;
        }

        if (block.Length > 0)
            content = content.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;

        File.WriteAllText(HostsPath, content, new UTF8Encoding(false));
    }

    private static void FlushDns()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
        }
        catch
        {
            // จงใจเพิกเฉย: ipconfig /flushdns เป็นเพียงการเร่งให้ค่าใหม่มีผลเร็วขึ้น
            // ไฟล์ hosts ถูกเขียนไปแล้วก่อนหน้า Windows จะอ่านค่าใหม่เองเมื่อ cache หมดอายุ
        }
    }
}
