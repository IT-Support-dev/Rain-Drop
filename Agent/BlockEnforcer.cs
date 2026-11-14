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

    /// <summary>
    /// ล็อกแยกสำหรับสถานะกันสแปมแจ้งเตือน: Enforce() ถูกเรียกทั้งจาก timer (thread pool)
    /// และจาก SetRules() (thread ของ packet handler) จึงแก้ HashSet/DateTime ตัวนี้พร้อมกันได้
    /// ใช้ล็อกแยกจาก _gate เพื่อไม่ต้องถือ _gate ตอนยิง Alert ออกไปข้างนอก
    /// </summary>
    private readonly object _alertGate = new();

    private readonly System.Threading.Timer _timer;
    private HashSet<string> _programs = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastAlertReset = DateTime.Now;                       // guarded by _alertGate
    private readonly HashSet<string> _alertedRecently = new(StringComparer.OrdinalIgnoreCase); // guarded by _alertGate

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
        catch (Exception ex)
        {
            // ถ้าอ่านไฟล์ไม่ได้ = รายการบล็อกที่ผู้ดูแลตั้งไว้หายไปเงียบ ๆ ต้องมีร่องรอยให้ตรวจสอบ
            Diag.Warn("BlockEnforcer", $"อ่านรายการบล็อกที่ '{BlocklistPath}' ไม่สำเร็จ จึงเริ่มด้วยรายการว่าง", ex);
        }

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
        catch (Exception ex)
        {
            // เขียนไม่ได้ = Service มองไม่เห็นรายการเว็บที่ต้องบล็อก และรายการจะหายเมื่อ Agent รีสตาร์ท
            // การบล็อกโปรแกรมในหน่วยความจำยังทำงาน จึงไม่โยนต่อ แต่ต้องบันทึกให้เห็นว่าเว็บไม่ถูกบล็อก
            Diag.Error("BlockEnforcer", $"เขียนรายการบล็อกที่ '{BlocklistPath}' ไม่สำเร็จ การบล็อกเว็บผ่าน hosts จะไม่อัปเดต", ex);
        }
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
            // หมายเหตุ: ไม่ต้องเก็บ rules.Urls ไว้ในหน่วยความจำ เพราะ Agent ไม่ได้บังคับใช้เอง
            // URLs ถูกเขียนลง blocklist.json ใน SetRules ให้ AgentService (สิทธิ์ SYSTEM) นำไปแก้ hosts
        }
    }

    private void Enforce()
    {
        string[] programs;
        lock (_gate) programs = _programs.ToArray();
        if (programs.Length == 0) return;

        // รีเซ็ตรายการที่เพิ่งแจ้งเตือนทุก 30 วินาที กันสแปม
        // ต้องล็อก: Enforce อาจถูกเรียกซ้อนจาก timer และ SetRules พร้อมกัน
        lock (_alertGate)
        {
            if ((DateTime.Now - _lastAlertReset).TotalSeconds > 30)
            {
                _alertedRecently.Clear();
                _lastAlertReset = DateTime.Now;
            }
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
                catch (Exception ex)
                {
                    // ปิดไม่ได้: โปรเซสสิทธิ์สูงกว่า หรือจบเองไปก่อนแล้ว (แข่งกันระหว่าง enumerate กับ Kill)
                    // ยังบล็อกโปรเซสอื่นต่อได้ จึงไม่โยน แต่บันทึกไว้เพราะแปลว่าบล็อกไม่สำเร็จจริง
                    Diag.Warn("BlockEnforcer.Enforce", $"ปิดโปรแกรมที่ถูกบล็อก '{name}' (PID {SafePid(p)}) ไม่สำเร็จ", ex);
                }
                finally { p.Dispose(); }
            }
            if (killed)
            {
                // ล็อกเฉพาะตอนแก้ HashSet แล้วค่อยยิง event ข้างนอกล็อก (คง short-circuit เดิม: Add เฉพาะเมื่อ killed)
                bool firstAlert;
                lock (_alertGate) firstAlert = _alertedRecently.Add(name);
                if (firstAlert)
                    Alert?.Invoke(new AlertMsg("บล็อกโปรแกรม", $"ปิดโปรแกรมที่ถูกบล็อก: {name}", DateTime.Now));
            }
        }
    }

    /// <summary>อ่าน PID แบบไม่โยน (โปรเซสอาจจบไปแล้วตอนกำลังจะบันทึก log)</summary>
    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(); }
        catch
        {
            // จงใจเพิกเฉย: ใช้เพื่อประกอบการบันทึกเท่านั้น โปรเซสที่จบแล้วอ่าน Id ไม่ได้ ไม่ใช่ความผิดพลาด
            return "?";
        }
    }

    public void Dispose() => _timer.Dispose();
}
