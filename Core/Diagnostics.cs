using System.Text;

namespace RainDrop.Shared;

/// <summary>
/// บันทึกข้อความวินิจฉัยลงไฟล์แบบ best-effort ใช้กับจุดที่เดิมเคยกลืน error เงียบ ๆ
/// (ตั้งค่าอ่าน/เขียนไม่ได้, ตั้ง watcher ไม่ได้, เขียน hosts ไม่สำเร็จ ฯลฯ)
///
/// กติกา: ตัว logger เองต้องไม่ทำให้โปรแกรมล้ม ถ้าเขียนไม่ได้จะปิดตัวเองเงียบ ๆ
///ที่อยู่: %LocalAppData%\RainDrop\logs\&lt;process&gt;-&lt;date&gt;.log
/// (Agent รันสิทธิ์ผู้ใช้ / AgentService รันสิทธิ์ SYSTEM จึงแยกโฟลเดอร์กันโดยธรรมชาติ)
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private const long MaxBytes = 5 * 1024 * 1024;
    private static bool _disabled;

    /// <summary>พาธไฟล์ล็อกของวันนี้ (ใช้แสดงในหน้าต่างสถานะ/ตอนดีบัก)</summary>
    public static string LogPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainDrop", "logs");
            string name = Environment.ProcessPath is { Length: > 0 } p
                ? Path.GetFileNameWithoutExtension(p)
                : "raindrop";
            return Path.Combine(dir, $"{name}-{DateTime.Now:yyyy-MM-dd}.log");
        }
    }

    public static void Warn(string area, string message, Exception? ex = null) => Write("WARN", area, message, ex);

    public static void Error(string area, string message, Exception? ex = null) => Write("ERROR", area, message, ex);

    private static void Write(string level, string area, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (_disabled) return;
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // กันไฟล์บวมไม่สิ้นสุด: ถ้าเกินโควตาแล้วหยุดเขียนสำหรับรอบนี้
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) return;

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {area}: {message}" +
                              (ex is null ? "" : $" | {ex.GetType().Name}: {ex.Message}");

                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
            catch
            {
                // จงใจเพิกเฉย: นี่คือตัว log เอง ถ้าเขียนไม่ได้ (สิทธิ์/ดิสก์เต็ม/พาธใช้ไม่ได้)
                // จะโยนต่อไม่ได้เพราะจะทำให้ caller ที่กำลังจัดการ error อยู่ล้มซ้ำ
                // จึงปิดตัวเองถาวรในโปรเซสนี้ แล้วปล่อยให้การคิดหลักทำงานต่อ
                _disabled = true;
            }
        }
    }
}
