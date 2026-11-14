using System.Diagnostics;
using System.Text;
using RainDrop.Shared;
using static RainDrop.Agent.NativeMethods;

namespace RainDrop.Agent;

/// <summary>
/// เก็บข้อมูลการใช้งานเครื่องลูกแบบต่อเนื่อง:
/// - โปรแกรมที่กำลังใช้งาน (foreground) และนับเวลาที่ใช้แต่ละโปรแกรม (วินาที)
/// - รายชื่อโปรแกรมที่เปิดอยู่ (มีหน้าต่าง)
/// - ไฟล์ที่ดาวน์โหลด (โฟลเดอร์ Downloads) และไฟล์ที่ส่งออกลงไดรฟ์ USB
/// Master จะดึง Snapshot ไปแสดงเป็นระยะ
/// </summary>
internal sealed class ActivityTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _seconds = new(StringComparer.OrdinalIgnoreCase);   // process -> วินาทีสะสม
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);  // process -> ชื่อหน้าต่างล่าสุด
    private readonly LinkedList<FileEvent> _fileEvents = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _sampler;
    private readonly System.Threading.Timer _driveRefresh;
    private readonly DateTime _startedAt = DateTime.Now;

    private string _fgProcess = "";
    private string _fgTitle = "";
    private bool _driveScanDone;                 // guarded by _gate
    private int _driveRefreshBusy;              // กัน timer callback ซ้อนกัน (0 = ว่าง, 1 = กำลังทำ)
    private const int MaxFileEvents = 300;

    /// <summary>ยิงเมื่อมีไฟล์ดาวน์โหลด/ส่งออก หรือเสียบ USB (ใช้ส่งแจ้งเตือนไป Master)</summary>
    public event Action<AlertMsg>? Alert;

    public ActivityTracker()
    {
        // ติดตามโฟลเดอร์ Downloads (นับเป็น "ดาวน์โหลด")
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        AddWatcher(downloads, "ดาวน์โหลด");
        RefreshRemovableDrives();

        // สุ่มดู foreground ทุก 1 วินาที และรีเฟรชรายการไดรฟ์ USB ทุก 20 วินาที
        _sampler = new System.Threading.Timer(_ => Sample(), null, 1000, 1000);
        _driveRefresh = new System.Threading.Timer(_ => RefreshRemovableDrives(), null, 20000, 20000);
    }

    // ---------- foreground + เวลาใช้งาน ----------

    private void Sample()
    {
        try
        {
            var (process, title) = Foreground();
            if (string.IsNullOrEmpty(process)) return;
            lock (_gate)
            {
                _seconds[process] = _seconds.GetValueOrDefault(process) + 1;
                if (!string.IsNullOrEmpty(title)) _titles[process] = title;
                _fgProcess = process;
                _fgTitle = title;
            }
        }
        catch (Exception ex)
        {
            // จงใจเพิกเฉยต่อรอบนี้: การสุ่ม foreground ทำทุก 1 วินาที และพลาดได้เป็นปกติ
            // (หน้าต่างปิดพอดี หรือโปรเซสจบลงระหว่างอ่าน) การโยนต่อจะฆ่า timer callback
            // จึงบันทึกไว้แล้วข้ามรอบนี้ รอบถัดไปยังทำงานต่อ
            Diag.Warn("ActivityTracker.Sample", "อ่านหน้าต่างที่แอคทีฟไม่สำเร็จ ข้ามรอบนี้", ex);
        }
    }

    private static (string Process, string Title) Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ("", "");

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return ("", "");

        string process;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch { return ("", ""); }

        int len = GetWindowTextLength(hwnd);
        string title = "";
        if (len > 0)
        {
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            title = sb.ToString();
        }
        return (process, title);
    }

    private static List<string> RunningApps()
    {
        // โปรแกรมที่มีหน้าต่าง (ผู้ใช้เปิดใช้งานอยู่) ไม่รวมบริการเบื้องหลัง
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) names.Add(p.ProcessName); }
            catch
            {
                // จงใจเพิกเฉย: โปรเซสที่สิทธิ์สูงกว่า (SYSTEM/โปรเซสป้องกัน) อ่าน MainWindowHandle ไม่ได้
                // เป็นเรื่องปกติของ Windows ไม่ใช่ความผิดพลาดของเรา แค่ไม่นับโปรเซสนั้นในรายการ
            }
            finally { p.Dispose(); }
        }
        return names.ToList();
    }

    // ---------- ไฟล์ดาวน์โหลด / ส่งออก USB ----------

    private void AddWatcher(string path, string kind)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            w.Created += (_, e) => RecordFile(kind, e.FullPath);
            lock (_gate) _watchers.Add(w);
        }
        catch (Exception ex)
        {
            // การตั้ง watcher ไม่ได้แปลว่า "ไม่ได้ตรวจจับไฟล์ในโฟลเดอร์นั้น" แบบเงียบ ๆ
            // ซึ่งผู้ใช้/Master ควรทราบ จึงบันทึกไว้ (ยังไม่โยนต่อ เพราะโฟลเดอร์อื่นต้องทำงานต่อได้)
            Diag.Warn("ActivityTracker.AddWatcher", $"ตั้งตัวติดตามไฟล์ที่ '{path}' ({kind}) ไม่สำเร็จ", ex);
        }
    }

    private void RefreshRemovableDrives()
    {
        // timer ยิงทุก 20 วินาที ถ้ารอบก่อนยังทำงานไม่เสร็จ (ไดรฟ์ค้าง) ห้ามซ้อนกัน
        // ไม่งั้นสองรอบจะเห็น snapshot เดียวกันแล้วตั้ง watcher ซ้ำให้ไดรฟ์เดิม
        if (Interlocked.CompareExchange(ref _driveRefreshBusy, 1, 0) != 0) return;
        try
        {
            // อ่าน _watchers ภายใต้ล็อกเดียวกันกับที่ AddWatcher/Dispose ใช้
            // เดิมอ่านโดยไม่ล็อก ขณะที่ timer รอบอื่น/AddWatcher กำลังเพิ่มรายการ => เสี่ยงต่อ
            // InvalidOperationException (collection was modified) ระหว่าง enumerate
            HashSet<string> watched;
            bool alertEnabled;
            lock (_gate)
            {
                watched = new HashSet<string>(_watchers.Select(w => w.Path), StringComparer.OrdinalIgnoreCase);
                alertEnabled = _driveScanDone;
            }

            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Removable && d.IsReady && !watched.Contains(d.RootDirectory.FullName))
                {
                    AddWatcher(d.RootDirectory.FullName, "ส่งออก (USB)");
                    // แจ้งเตือนทันทีเมื่อเสียบ USB ใหม่ (ข้ามรอบแรกตอนเพิ่งเริ่มโปรแกรม)
                    // เรียกนอก lock เพื่อไม่ให้ handler ของ Alert (ซึ่งส่งข้อมูลไป Master) ถูกกั้นด้วย _gate
                    if (alertEnabled)
                    {
                        string label = SafeVolumeLabel(d);
                        Alert?.Invoke(new AlertMsg("USB", $"เสียบ USB: {d.RootDirectory.FullName} {label}", DateTime.Now));
                    }
                }
            }
            lock (_gate) _driveScanDone = true;
        }
        catch (Exception ex)
        {
            // ไดรฟ์ถูกถอดระหว่างสอบถาม ทำให้ DriveInfo โยนได้เป็นเรื่องปกติ แต่ต้องมองเห็นตอนดีบัก
            Diag.Warn("ActivityTracker.RefreshRemovableDrives", "รีเฟรชรายการไดรฟ์ USB ไม่สำเร็จ ข้ามรอบนี้", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _driveRefreshBusy, 0);
        }
    }

    private void RecordFile(string kind, string fullPath)
    {
        // ข้ามไฟล์ชั่วคราวของเบราว์เซอร์ระหว่างดาวน์โหลด
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is ".tmp" or ".crdownload" or ".part") return;

        lock (_gate)
        {
            _fileEvents.AddFirst(new FileEvent(DateTime.Now, kind, fullPath));
            while (_fileEvents.Count > MaxFileEvents) _fileEvents.RemoveLast();
        }
        string alertKind = kind.Contains("USB") ? "ส่งออกไฟล์" : "ดาวน์โหลด";
        Alert?.Invoke(new AlertMsg(alertKind, $"{kind}: {Path.GetFileName(fullPath)}", DateTime.Now));
    }

    private static string SafeVolumeLabel(DriveInfo d)
    {
        try { return string.IsNullOrEmpty(d.VolumeLabel) ? "" : $"({d.VolumeLabel})"; }
        catch { return ""; }
    }

    // ---------- สร้าง snapshot ให้ Master ----------

    public ActivitySnapshot Snapshot()
    {
        List<AppUsage> usage;
        List<FileEvent> files;
        string fgP, fgT;
        lock (_gate)
        {
            usage = _seconds
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new AppUsage(kv.Key, _titles.GetValueOrDefault(kv.Key, ""), kv.Value))
                .ToList();
            files = _fileEvents.ToList();
            fgP = _fgProcess;
            fgT = _fgTitle;
        }
        return new ActivitySnapshot(fgP, fgT, RunningApps(), usage, files, _startedAt, DateTime.Now);
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _driveRefresh.Dispose();
        lock (_gate)
            foreach (var w in _watchers)
            {
                try { w.Dispose(); }
                catch (Exception ex)
                {
                    // จงใจไปต่อ: กำลังปิดโปรแกรม และ watcher ตัวหนึ่งพังต้องไม่กันไม่ให้ตัวที่เหลือถูก Dispose
                    Diag.Warn("ActivityTracker.Dispose", $"ปิดตัวติดตามไฟล์ที่ '{w.Path}' ไม่สำเร็จ", ex);
                }
            }
    }
}
