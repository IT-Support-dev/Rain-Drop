using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RainDrop.Shared;

namespace RainDrop.AgentService;

/// <summary>
/// หัวใจของ Service: คอยให้ RainDrop.Agent.exe ทำงานอยู่ในเซสชันของผู้ใช้ตลอดเวลา
/// - เปิดตอนบูต/ตอนผู้ใช้ล็อกอิน
/// - ถ้า Agent ถูกปิด (End Task ฯลฯ) จะเปิดใหม่ภายในไม่กี่วินาที
/// - หยุดปลุกก็ต่อเมื่อมีคำขอผ่าน named pipe พร้อมรหัสผ่านผู้ดูแลที่ Service ตรวจเองว่าถูกต้อง
///   (ไม่ใช้ environment variable หรือไฟล์สัญญาณที่ผู้ใช้อย่าน/เขียนได้อีกต่อไป)
/// </summary>
internal sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly string _agentExe;

    private uint _launchedSession = SessionLauncher.InvalidSession;
    private int _agentPid = -1;
    private int _stopAuthorized;   // 0 = ยังไม่อนุญาต, 1 = ผู้ดูแลใส่รหัสถูกแล้ว

    public Worker(ILogger<Worker> log)
    {
        _log = log;
        _agentExe = Path.Combine(AppContext.BaseDirectory, "RainDrop.Agent.exe");
    }

    private void Log(string message) => _log.LogInformation("{Message}", message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ServiceStore.EnsureLayout(Log);
        }
        catch (Exception ex)
        {
            // ตั้ง ACL ไม่ได้แปลว่าไฟล์ค่าตั้งอาจถูกผู้ใช้ทั่วไปแก้ไขได้ ต้องแจ้งชัดเจนแต่ยังทำงานต่อ
            _log.LogError(ex, "ตั้งสิทธิ์โฟลเดอร์ค่าตั้งไม่สำเร็จ ตรวจสิทธิ์ {Root} ด้วยมือ", ServiceStore.Root);
        }

        var (salt, hash) = ServiceStore.LoadCredentials(Log);
        var gate = new PasswordGate(salt, hash);
        if (!gate.HasPassword)
            _log.LogWarning("ไม่พบรหัสผ่านผู้ดูแลใน {Path} จะไม่มีใครสั่งหยุด Agent ได้จนกว่าจะตั้งใหม่",
                ServiceStore.CredentialsPath);

        AuditLog audit;
        try { audit = new AuditLog(ServiceStore.AuditFolder); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit chain ใช้ไม่ได้ Agent จะปฏิเสธคำสั่งที่ต้องบันทึก audit");
            throw;
        }
        using var auditScope = audit;
        var pipe = new ControlPipeServer(
            gate, Log,
            onAuthorizedStop: () => Interlocked.Exchange(ref _stopAuthorized, 1),
            agentPid: () => _agentPid,
            syncHosts: () => HostsBlocker.Sync(Log), audit: audit);

        // ใช้ CTS แยกที่ผูกกับ stoppingToken แต่ Worker ยกเลิกเองได้ตอนอนุญาตให้หยุด
        // ถ้าใช้ stoppingToken ตรง ๆ ตอน return จะเกิด deadlock: finally รอ pipeTask
        // แต่ pipeTask ไม่จบจนกว่า stoppingToken ถูกยกเลิก ซึ่งเกิดหลัง ExecuteAsync return เท่านั้น
        using var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task pipeTask = pipe.RunAsync(pipeCts.Token);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Volatile.Read(ref _stopAuthorized) == 1)
                    {
                        Log("ได้รับคำสั่งออกจากโปรแกรมที่ตรวจรหัสผ่านแล้ว หยุดการปลุก Agent");
                        HostsBlocker.Clear(Log);
                        pipeCts.Cancel();   // ปิดท่อควบคุมก่อน เพื่อไม่ให้ finally ค้าง
                        return;   // หยุด Service (จะกลับมาทำงานเองเมื่อบูตครั้งถัดไป)
                    }

                    EnsureAgentRunning();
                    HostsBlocker.Sync(Log);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "รอบตรวจสอบ Agent มีปัญหา");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Service ถูกสั่งหยุดโดยผู้ดูแล/ระบบ เป็นทางออกปกติ
        }
        finally
        {
            // ยกเลิกเสมอทุกทางออก (idempotent) การันตีว่า pipeTask จบก่อนรอ ไม่ให้ Service ค้างตอนหยุด
            pipeCts.Cancel();
            try { await pipeTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // ท่อควบคุมจบตาม cancellation ที่เราสั่ง ไม่ต้องทำอะไรเพิ่ม
            }
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
            // ไม่ส่งความลับใด ๆ ผ่าน environment อีกต่อไป การขออนุญาตหยุดใช้ named pipe + รหัสผ่าน
            uint pid = SessionLauncher.LaunchInSession(session, _agentExe, new Dictionary<string, string>());
            _launchedSession = session;
            _agentPid = (int)pid;
            Log($"เปิด Agent ในเซสชัน {session} (PID {pid})");
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
        catch (ArgumentException)
        {
            // โปรเซสนี้ไม่มีอยู่แล้ว = ต้องเปิดใหม่ เป็นผลลัพธ์ที่ถูกต้อง ไม่ใช่ error ที่ต้องแจ้ง
            return false;
        }
        catch (InvalidOperationException)
        {
            // โปรเซสจบลงระหว่างสอบถาม เช่นเดียวกับข้างบน
            return false;
        }
    }
}
