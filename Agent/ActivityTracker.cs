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
    private bool _driveScanDone;
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
        catch { /* ข้ามรอบนี้ */ }
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
            catch { /* เข้าถึงไม่ได้ */ }
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
        catch { /* ตั้ง watcher ไม่ได้ */ }
    }

    private void RefreshRemovableDrives()
    {
        try
        {
            var watched = new HashSet<string>(_watchers.Select(w => w.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Removable && d.IsReady && !watched.Contains(d.RootDirectory.FullName))
                {
                    AddWatcher(d.RootDirectory.FullName, "ส่งออก (USB)");
                    // แจ้งเตือนทันทีเมื่อเสียบ USB ใหม่ (ข้ามรอบแรกตอนเพิ่งเริ่มโปรแกรม)
                    if (_driveScanDone)
                    {
                        string label = SafeVolumeLabel(d);
                        Alert?.Invoke(new AlertMsg("USB", $"เสียบ USB: {d.RootDirectory.FullName} {label}", DateTime.Now));
                    }
                }
            }
            _driveScanDone = true;
        }
        catch { /* ข้าม */ }
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
            foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
    }
}
