using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.AgentService;

/// <summary>
/// ท่อควบคุมภายในเครื่อง แทนกลไกเดิมที่ส่ง token ผ่าน environment variable + ไฟล์สัญญาณ
///
/// สิทธิ์:
///   - ACL ของท่อให้เฉพาะ SYSTEM, Administrators และ SID ของผู้ใช้ที่ล็อกอินอยู่ในเซสชันคอนโซล
///   - คำสั่ง "stop" ต้องส่งรหัสผ่านผู้ดูแลมาให้ Service ตรวจเอง (พร้อม lockout)
///   - คำสั่งตั้ง/อ่าน blocklist รับเฉพาะโปรเซส Agent ที่ Service เป็นคนเปิด (ตรวจ PID ของปลายท่อ)
///     กันผู้ใช้ทั่วไปยิงท่อเองเพื่อปลดบล็อกเว็บ
///
/// ACL ถูกสร้างใหม่ทุกรอบที่ยอมรับการเชื่อมต่อ และมีการต่ออายุทุก 5 วินาทีแม้ไม่มีคนต่อ
/// เพื่อเปลี่ยนผู้ใช้ที่ล็อกอินแล้วสิทธิ์ตามทัน
/// </summary>
internal sealed class ControlPipeServer
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly PasswordGate _gate;
    private readonly Action<string> _log;
    private readonly Action _onAuthorizedStop;
    private readonly Func<int> _agentPid;
    private readonly Action _syncHosts;
    private readonly AuditLog _audit;

    public ControlPipeServer(PasswordGate gate, Action<string> log, Action onAuthorizedStop,
        Func<int> agentPid, Action syncHosts, AuditLog audit)
    {
        _gate = gate;
        _log = log;
        _onAuthorizedStop = onAuthorizedStop;
        _agentPid = agentPid;
        _syncHosts = syncHosts;
        _audit = audit;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();

                using var accept = CancellationTokenSource.CreateLinkedTokenSource(ct);
                accept.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await pipe.WaitForConnectionAsync(accept.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // ไม่มีใครต่อใน 5 วินาที: ปิดแล้วสร้างใหม่เพื่อ รีเฟรช SID ผู้ใช้ที่ล็อกอิน
                    continue;
                }

                await ServeOneAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // Service กำลังหยุด
            }
            catch (Exception ex)
            {
                _log("ท่อควบคุมมีปัญหา รอบถัดไปจะสร้างใหม่: " + ex.Message);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(SystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(AdminsSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        uint session = SessionLauncher.ActiveConsoleSession;
        if (session != SessionLauncher.InvalidSession)
        {
            SecurityIdentifier? user = SessionLauncher.SessionUserSid(session, out string? sidError);
            if (user is not null)
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            else if (sidError is not null)
                _log($"ยังให้สิทธิ์ผู้ใช้ที่ล็อกอินไม่ได้ (ท่อจะรับเฉพาะ SYSTEM/ผู้ดูแล): {sidError}");
        }

        return NamedPipeServerStreamAcl.Create(
            ControlChannel.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? line = await ReadLineBoundedAsync(pipe, ct).ConfigureAwait(false);
        if (line is null)
        {
            await ReplyAsync(pipe, new ControlResponse(false, "ข้อความเกินขนาดหรือไม่ครบรูปแบบ"), ct).ConfigureAwait(false);
            return;
        }

        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(line);
        }
        catch (JsonException ex)
        {
            _log("คำขอผ่านท่อควบคุมไม่ใช่ JSON ที่ถูกต้อง: " + ex.Message);
            await ReplyAsync(pipe, new ControlResponse(false, "รูปแบบคำขอไม่ถูกต้อง"), ct).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await ReplyAsync(pipe, new ControlResponse(false, "คำขอว่าง"), ct).ConfigureAwait(false);
            return;
        }

        uint clientPid = ClientPid(pipe);
        ControlResponse response = Dispatch(request, clientPid);
        await ReplyAsync(pipe, response, ct).ConfigureAwait(false);
    }

    private ControlResponse Dispatch(ControlRequest request, uint clientPid)
    {
        switch (request.Op)
        {
            case ControlChannel.OpStop:
            {
                GateResult result = _gate.Verify(request.Password);
                switch (result.Status)
                {
                    case GateStatus.Allowed:
                        _log($"อนุญาตให้ออกจากโปรแกรม (คำขอจาก PID {clientPid})");
                        _onAuthorizedStop();
                        return new ControlResponse(true, "อนุญาตให้ออกจากโปรแกรมได้");

                    case GateStatus.LockedOut:
                        _log($"คำขอหยุดถูกล็อกชั่วคราว เหลือ {result.RetryAfterSeconds} วินาที (PID {clientPid})");
                        return new ControlResponse(false,
                            $"ลองผิดหลายครั้ง ถูกล็อกชั่วคราว {result.RetryAfterSeconds} วินาที", result.RetryAfterSeconds);

                    case GateStatus.NotConfigured:
                        _log("ยังไม่มีรหัสผ่านผู้ดูแลในเครื่องนี้ ปฏิเสธคำขอหยุด");
                        return new ControlResponse(false, "ยังไม่ได้ตั้งรหัสผ่านผู้ดูแลบนเครื่องนี้ ติดต่อผู้ดูแลระบบ");

                    default:
                        _log($"รหัสผ่านไม่ถูกต้องในการขอหยุด (PID {clientPid})");
                        return new ControlResponse(false, "รหัสผ่านไม่ถูกต้อง");
                }
            }

            case ControlChannel.OpSetBlocklist:
            {
                if (!IsTrustedAgent(clientPid))
                    return new ControlResponse(false, "คำขอนี้รับเฉพาะจากโปรแกรม Agent ที่บริการเปิดให้เท่านั้น");

                var incoming = new BlockRules(request.Programs ?? new List<string>(), request.Urls ?? new List<string>());
                if (!BlocklistValidator.TryNormalize(incoming, out BlockRules normalized, out string error))
                {
                    _log("ปฏิเสธรายการบล็อกเพราะผิดรูปแบบ: " + error);
                    return new ControlResponse(false, error);
                }

                try
                {
                    ServiceStore.SaveBlocklist(normalized);
                    _syncHosts();
                    return new ControlResponse(true,
                        $"บันทึกรายการบล็อกแล้ว (โปรแกรม {normalized.Programs.Count}, เว็บ {normalized.Urls.Count})");
                }
                catch (Exception ex)
                {
                    _log("บันทึกรายการบล็อกไม่สำเร็จ: " + ex.Message);
                    return new ControlResponse(false, "บันทึกรายการบล็อกไม่สำเร็จ: " + ex.Message);
                }
            }

            case ControlChannel.OpGetBlocklist:
            {
                if (!IsTrustedAgent(clientPid))
                    return new ControlResponse(false, "คำขอนี้รับเฉพาะจากโปรแกรม Agent ที่บริการเปิดให้เท่านั้น");

                BlockRules rules = ServiceStore.LoadBlocklist(_log);
                return new ControlResponse(true, "ok", 0, rules.Programs, rules.Urls);
            }

            case ControlChannel.OpGetIdentity:
            {
                if (!IsTrustedAgent(clientPid)) return DeniedIdentity();
                var identity = ServiceStore.LoadIdentity(_log);
                if (identity is null) return DeniedIdentity();
                var (cert, ca, revocation, allowlist) = identity.Value;
                using (cert) using (ca)
                    return new ControlResponse(true, "ok", 0, null, null,
                        Convert.ToBase64String(cert.RawData),
                        Convert.ToBase64String(cert.GetECDsaPrivateKey()!.ExportPkcs8PrivateKey()),
                        Convert.ToBase64String(ca.RawData),
                        JsonSerializer.Serialize(revocation), JsonSerializer.Serialize(allowlist));
            }
            case ControlChannel.OpAudit:
            {
                if (!IsTrustedAgent(clientPid)) return new ControlResponse(false, "คำขอนี้รับเฉพาะ Agent ของบริการ");
                if (request.Event is null || request.Ip is null || request.Result is null)
                    return new ControlResponse(false, "ข้อมูล audit ไม่ครบ");
                try
                {
                    _audit.Append(new AuditEvent(request.Event, request.Ip, request.Result, request.Detail ?? ""));
                    return new ControlResponse(true, "ok");
                }
                catch (Exception ex) when (ex is IOException or ArgumentException)
                {
                    _log("เขียน audit ไม่สำเร็จ คำสั่งจากเครือข่ายจะถูกปฏิเสธ");
                    return new ControlResponse(false, "audit-unavailable");
                }
            }
            case ControlChannel.OpSaveRevocation:
            {
                if (!IsTrustedAgent(clientPid)) return new ControlResponse(false, "คำขอนี้รับเฉพาะ Agent ของบริการ");
                try
                {
                    var list = JsonSerializer.Deserialize<RevocationList>(request.RevocationJson ?? "")
                        ?? throw new InvalidDataException("Empty revocation snapshot");
                    ServiceStore.SaveRevocation(list);
                    return new ControlResponse(true, "ok");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _log("บันทึกรายการเพิกถอนไม่สำเร็จ: " + ex.GetType().Name);
                    return new ControlResponse(false, "revocation-rejected");
                }
            }
            case ControlChannel.OpVerifyAudit:
            {
                if (!IsTrustedAgent(clientPid)) return new ControlResponse(false, "คำขอนี้รับเฉพาะ Agent ของบริการ");
                var result = AuditLog.Verify(ServiceStore.AuditFolder);
                return new ControlResponse(result.Valid, result.Valid ? "ok" : "audit-invalid", 0, null, null,
                    null, null, null, null, null, result.Valid, result.Error);
            }
            default:
                return new ControlResponse(false, "ไม่รู้จักคำสั่ง");
        }
    }

    private static ControlResponse DeniedIdentity() => new(false, "ยังไม่มี identity ที่ถูกต้องหรือคำขอไม่ได้มาจาก Agent ของบริการ");

    private bool IsTrustedAgent(uint clientPid)
    {
        int expected = _agentPid();
        return expected > 0 && clientPid == (uint)expected;
    }

    private static uint ClientPid(NamedPipeServerStream pipe)
    {
        if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return pid;
        // อ่าน PID ไม่ได้ถือว่าไม่ผ่านตัวตรวจ (fail-closed) ผู้เรียกจะปฏิเสธคำสั่งที่ต้องเชื่อถือ
        return 0;
    }

    private static async Task ReplyAsync(Stream pipe, ControlResponse response, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");
        await pipe.WriteAsync(payload, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>อ่านหนึ่งบรรทัดโดยจำกัดความยาว กันผู้ใช้ส่งข้อมูลไม่สิ้นสุดมากินหน่วยความจำ</summary>
    private static async Task<string?> ReadLineBoundedAsync(Stream pipe, CancellationToken ct)
    {
        using var accumulated = new MemoryStream();
        var chunk = new byte[4096];
        while (accumulated.Length <= ControlChannel.MaxMessageBytes)
        {
            int read = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                accumulated.Write(chunk, 0, newline);
                return Encoding.UTF8.GetString(accumulated.ToArray());
            }
            accumulated.Write(chunk, 0, read);
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle Pipe, out uint ClientProcessId);
}
