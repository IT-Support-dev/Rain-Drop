using System.Diagnostics;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>
/// บังคับใช้รายการบล็อกบนเครื่องลูก:
/// - โปรแกรม: ตรวจทุก 2 วินาที ถ้าพบโปรแกรมที่ถูกบล็อกกำลังทำงาน จะปิดทันที
/// - เว็บ/ลิงก์: เขียนลง blocklist.json ให้ Windows Service (สิทธิ์ SYSTEM) นำไปแก้ไฟล์ hosts
///   (เพราะการแก้ hosts ต้องใช้สิทธิ์ผู้ดูแล ซึ่ง Agent ที่รันในสิทธิ์ผู้ใช้ทำไม่ได้)
/// </summary>
internal sealed class BlockEnforcer : IDisposable
{
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private HashSet<string> _programs = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _urls = new();
    private DateTime _lastAlertReset = DateTime.Now;
    private readonly HashSet<string> _alertedRecently = new(StringComparer.OrdinalIgnoreCase);

    public event Action<AlertMsg>? Alert;

    public static string BlocklistPath => Path.Combine(AgentConfig.SharedFolder, "blocklist.json");

    public BlockEnforcer()
    {
        // โหลดรายการที่เคยตั้งไว้ (เผื่อ Agent รีสตาร์ท)
        try
        {
            if (File.Exists(BlocklistPath))
            {
                var r = JsonSerializer.Deserialize<BlockRules>(File.ReadAllText(BlocklistPath));
                if (r is not null) ApplyRules(r);
            }
        }
        catch { /* ใช้ค่าว่าง */ }

        _timer = new System.Threading.Timer(_ => Enforce(), null, 2000, 2000);
    }

    public void SetRules(BlockRules rules)
    {
        ApplyRules(rules);
        try
        {
            Directory.CreateDirectory(AgentConfig.SharedFolder);
            File.WriteAllText(BlocklistPath, JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* เขียนไม่ได้: อย่างน้อยการบล็อกโปรแกรมยังทำงาน */ }
        Enforce(); // ปิดโปรแกรมที่ถูกบล็อกทันทีไม่ต้องรอรอบถัดไป
    }

    private void ApplyRules(BlockRules rules)
    {
        lock (_gate)
        {
            _programs = rules.Programs
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _urls = rules.Urls.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        }
    }

    private void Enforce()
    {
        string[] programs;
        lock (_gate) programs = _programs.ToArray();
        if (programs.Length == 0) return;

        // รีเซ็ตรายการที่เพิ่งแจ้งเตือนทุก 30 วินาที กันสแปม
        if ((DateTime.Now - _lastAlertReset).TotalSeconds > 30)
        {
            _alertedRecently.Clear();
            _lastAlertReset = DateTime.Now;
        }

        foreach (var name in programs)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            bool killed = false;
            foreach (var p in procs)
            {
                try { p.Kill(); killed = true; }
                catch { /* ปิดไม่ได้ (สิทธิ์สูงกว่า) */ }
                finally { p.Dispose(); }
            }
            if (killed && _alertedRecently.Add(name))
                Alert?.Invoke(new AlertMsg("บล็อกโปรแกรม", $"ปิดโปรแกรมที่ถูกบล็อก: {name}", DateTime.Now));
        }
    }

    public void Dispose() => _timer.Dispose();
}
