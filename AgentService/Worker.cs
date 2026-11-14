using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RainDrop.AgentService;

/// <summary>
/// หัวใจของ Service: คอยให้ RainDrop.Agent.exe ทำงานอยู่ในเซสชันของผู้ใช้ตลอดเวลา
/// - เปิดตอนบูต/ตอนผู้ใช้ล็อกอิน
/// - ถ้า Agent ถูกปิด (End Task ฯลฯ) จะเปิดใหม่ภายในไม่กี่วินาที
/// - จะหยุดเปิดก็ต่อเมื่อผู้ใช้กด "ออกจากโปรแกรม" ด้วยรหัสผู้ดูแลที่ถูกต้องเท่านั้น
///   (Agent จะเขียน token ที่ตรงกับที่ Service ส่งให้ลงไฟล์สัญญาณ)
/// </summary>
internal sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly string _agentExe;
    private readonly string _stopSignal;

    // token ใหม่ทุกครั้งที่ Service เริ่ม ผู้ใช้จึงปลอมไฟล์สัญญาณเองไม่ได้
    private readonly string _stopToken = Guid.NewGuid().ToString("N");

    private uint _launchedSession = SessionLauncher.InvalidSession;
    private int _agentPid = -1;

    public Worker(ILogger<Worker> log)
    {
        _log = log;
        _agentExe = Path.Combine(AppContext.BaseDirectory, "RainDrop.Agent.exe");
        _stopSignal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RainDrop", "agent-stop.signal");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ลบสัญญาณหยุดเก่า (จาก token รอบก่อน) ทิ้ง
        TryDeleteSignal();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldStop())
                {
                    _log.LogInformation("ได้รับคำสั่งออกจากโปรแกรมที่ถูกต้อง หยุดการปลุก Agent");
                    HostsBlocker.Clear(m => _log.LogInformation("{Message}", m));
                    TryDeleteSignal();
                    return; // หยุด Service (จะกลับมาทำงานเองเมื่อบูตครั้งถัดไป)
                }

                EnsureAgentRunning();
                HostsBlocker.Sync(m => _log.LogInformation("{Message}", m)); // บล็อกเว็บผ่านไฟล์ hosts
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "รอบตรวจสอบ Agent มีปัญหา");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    /// <summary>true ถ้าไฟล์สัญญาณมี token ตรงกับของรอบนี้</summary>
    private bool ShouldStop()
    {
        try
        {
            return File.Exists(_stopSignal) && File.ReadAllText(_stopSignal).Trim() == _stopToken;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureAgentRunning()
    {
        uint session = SessionLauncher.ActiveConsoleSession;
        if (session == SessionLauncher.InvalidSession) return; // ยังไม่มีใครล็อกอิน

        // ยังอยู่เซสชันเดิมและโปรเซสยังมีชีวิต = ไม่ต้องทำอะไร
        if (session == _launchedSession && _agentPid > 0 && IsAlive(_agentPid)) return;

        if (!File.Exists(_agentExe))
        {
            _log.LogError("ไม่พบ {Exe}", _agentExe);
            return;
        }

        try
        {
            var env = new Dictionary<string, string> { ["RAINDROP_STOP_TOKEN"] = _stopToken };
            uint pid = SessionLauncher.LaunchInSession(session, _agentExe, env);
            _launchedSession = session;
            _agentPid = (int)pid;
            _log.LogInformation("เปิด Agent ในเซสชัน {Session} (PID {Pid})", session, pid);
        }
        catch (Win32Exception ex)
        {
            // ช่วงเปลี่ยนผู้ใช้/ยังไม่มี token ของเซสชัน เกิดได้ชั่วคราว รอบหน้าค่อยลองใหม่
            _log.LogDebug("ยังเปิด Agent ไม่ได้: {Message}", ex.Message);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void TryDeleteSignal()
    {
        try { if (File.Exists(_stopSignal)) File.Delete(_stopSignal); } catch { /* ไม่เป็นไร */ }
    }
}
