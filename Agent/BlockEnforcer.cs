using System.Diagnostics;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>
/// บังคับใช้รายการบล็อกบนเครื่องลูก:
/// - โปรแกรม: ตรวจทุก 2 วินาที ถ้าพบโปรแกรมที่ถูกบล็อกกำลังทำงาน จะปิดทันที
/// - เว็บ/ลิงก์: ส่งให้ Windows Service (สิทธิ์ SYSTEM) ผ่าน named pipe เพื่อตรวจรูปแบบแล้วแก้ไฟล์ hosts
///   Agent ไม่เขียนไฟล์ค่าตั้งเองอีกต่อไป เพราะผู้ใช้ทั่วไปจะแก้ไฟล์นั้นเพื่อปลดบล็อกได้
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

    public BlockEnforcer()
    {
        // ดึงรายการที่ Service เก็บไว้กลับมา (Agent ไม่มีสิทธิ์เขียนไฟล์นี้อีกต่อไป)
        BlockRules? stored = ServiceClient.GetBlocklist(out string? error);
        if (stored is not null) ApplyRules(stored);
        else if (error is not null)
            Diag.Warn("BlockEnforcer", "ยังดึงรายการบล็อกจากบริการไม่ได้ จะรอรับจาก Master เมื่อเชื่อมต่อ: " + error);

        _timer = new System.Threading.Timer(_ => Enforce(), null, 2000, 2000);
    }

    public void SetRules(BlockRules rules)
    {
        ApplyRules(rules);

        // ส่งให้ Service ตรวจรูปแบบและเขียนลงที่ที่ผู้ใช้ทั่วไปแก้ไม่ได้ แทนการเขียนไฟล์เอง
        ControlResponse? response = ServiceClient.SetBlocklist(rules, out string? error);
        if (response is null)
            Diag.Error("BlockEnforcer",
                "ส่งรายการบล็อกให้บริการไม่สำเร็จ การบล็อกเว็บผ่าน hosts จะไม่อัปเดต: " + (error ?? "ไม่ทราบสาเหตุ"));
        else if (!response.Ok)
            Diag.Error("BlockEnforcer", "บริการปฏิเสธรายการบล็อก: " + response.Message);

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
            catch
            {
                // จงใจข้าม:ชื่อดูไม่ถูกต้องหรือระบบปฏิเสธการ enumerate ชื่อนี้ ยังบล็อกชื่ออื่นต่อได้
                // ไม่ใช่ความลับที่หายไป จึงไม่โยนทิ้งทั้งรอบ แต่บันทึกไว้ให้ตรวจสอบว่าบล็อกชื่อนี้ไม่สำเร็จ
                Diag.Warn("BlockEnforcer.Enforce", $" enumerate โปรเซสชื่อ '{name}' ไม่สำเร็จ ข้ามไปชื่อถัดไป");
                continue;
            }

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
