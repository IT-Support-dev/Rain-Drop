namespace RainDrop.Shared;

/// <summary>
/// ตรวจและจัดรูปรายการบล็อกที่ Agent ส่งให้ Service ก่อน Service เขียนลงไฟล์ hosts / ไฟล์ตั้งค่า
/// หลักการ: fail-closed — ถ้ามีรายการใดผิดรูป ปฏิเสธทั้งคำขอพร้อมเหตุผล
/// ดีกว่าเขียนบางส่วนแล้วปล่อยให้ค่าอันตรายหลุดลงไฟล์ที่ SYSTEM เป็นคนเขียน
/// </summary>
public static class BlocklistValidator
{
    public const int MaxPrograms = 200;
    public const int MaxUrls = 500;
    public const int MaxProgramLength = 128;
    public const int MaxUrlLength = 2048;

    public static bool TryNormalize(BlockRules? rules, out BlockRules normalized, out string error)
    {
        normalized = new BlockRules(new List<string>(), new List<string>());
        error = "";

        if (rules is null)
        {
            error = "ไม่มีรายการบล็อกส่งมา";
            return false;
        }

        // BlockRules ประกาศเป็น non-nullable แต่ JSON จากภายนอกอาจส่ง null ได้ จึงรับเป็น nullable ก่อนตรวจ
        List<string>? rawPrograms = rules.Programs;
        List<string>? rawUrls = rules.Urls;
        if (rawPrograms is null || rawUrls is null)
        {
            error = "รายการบล็อกต้องมีทั้ง Programs และ Urls (ห้ามเป็น null)";
            return false;
        }

        if (rawPrograms.Count > MaxPrograms)
        {
            error = $"รายการโปรแกรมมากเกินกำหนด ({rawPrograms.Count} > {MaxPrograms})";
            return false;
        }
        if (rawUrls.Count > MaxUrls)
        {
            error = $"รายการเว็บมากเกินกำหนด ({rawUrls.Count} > {MaxUrls})";
            return false;
        }

        var programs = new List<string>();
        foreach (string? raw in rawPrograms)
        {
            if (raw is null)
            {
                error = "พบรายการโปรแกรมที่เป็น null";
                return false;
            }
            string p = raw.Trim();
            if (p.Length == 0) continue;

            if (p.Length > MaxProgramLength)
            {
                error = $"ชื่อโปรแกรมยาวเกิน {MaxProgramLength} อักขระ";
                return false;
            }
            foreach (char c in p)
            {
                // ชื่อโปรเซสห้ามมีอักขระควบคุมหรือตัวคั่นพาธ เพราะไม่ได้ใช้เปิดไฟล์ ใช้เทียบกับชื่อโปรเซสเท่านั้น
                if (char.IsControl(c) || c is '\\' or '/' or ':' or '"' or '\'' or '*' or '?' or '<' or '>' or '|')
                {
                    error = "ชื่อโปรแกรมมีอักขระที่ไม่อนุญาต (อักขระควบคุมหรือตัวคั่นพาธ)";
                    return false;
                }
            }
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
            if (p.Length == 0)
            {
                error = "ชื่อโปรแกรมว่างหลังตัด .exe";
                return false;
            }
            if (!programs.Contains(p, StringComparer.OrdinalIgnoreCase)) programs.Add(p);
        }

        var hosts = new List<string>();
        foreach (string? raw in rawUrls)
        {
            if (raw is null)
            {
                error = "พบรายการเว็บที่เป็น null";
                return false;
            }
            if (raw.Length > MaxUrlLength)
            {
                error = $"รายการเว็บยาวเกิน {MaxUrlLength} อักขระ";
                return false;
            }
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            if (!Hostname.TryFromUrl(trimmed, out string host))
            {
                // ไม่เปิดเผยอดีตค่าทั้งสตริงลง log ยาว ๆ ตัดให้สั้นพอวินิจฉัย
                string shown = trimmed.Length > 60 ? trimmed[..60] + "..." : trimmed;
                error = $"รายการเว็บไม่ใช่ชื่อโฮสต์ที่ถูกต้อง: '{shown}'";
                return false;
            }
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        }

        normalized = new BlockRules(programs, hosts);
        return true;
    }
}
