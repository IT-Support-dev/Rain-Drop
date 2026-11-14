namespace RainDrop.Shared;

/// <summary>
/// สัญญาการคุยระหว่าง Agent (สิทธิ์ผู้ใช้) กับ AgentService (สิทธิ์ SYSTEM) ผ่าน named pipe
/// เป็น IPC ภายในเครื่องเท่านั้น ไม่เกี่ยวกับรูปแบบแพ็กเก็ตบนเครือข่ายของ Protocol/HubProtocol
/// ใช้ JSON บรรทัดเดียวต่อข้อความ (newline-delimited) และจำกัดขนาดกันการใช้หน่วยความจำเกิน
/// </summary>
public static class ControlChannel
{
    public const string PipeName = "RainDropAgentCtrl";
    public const int MaxMessageBytes = 8 * 1024 * 1024; // full revocation snapshot can exceed 64 KiB
    public const int ConnectTimeoutMs = 3000;

    public const string OpStop = "stop";
    public const string OpSetBlocklist = "setBlocklist";
    public const string OpGetBlocklist = "getBlocklist";
    public const string OpGetIdentity = "getIdentity";
    public const string OpAudit = "audit";
    public const string OpSaveRevocation = "saveRevocation";
    public const string OpVerifyAudit = "verifyAudit";
}

public sealed record ControlRequest(
    string Op,
    string? Password = null,
    List<string>? Programs = null,
    List<string>? Urls = null,
    string? Event = null,
    string? Ip = null,
    string? Result = null,
    string? Detail = null,
    string? RevocationJson = null);

public sealed record ControlResponse(
    bool Ok,
    string Message,
    int RetryAfterSeconds = 0,
    List<string>? Programs = null,
    List<string>? Urls = null,
    string? CertificateBase64 = null,
    string? PrivateKeyBase64 = null,
    string? CaBase64 = null,
    string? RevocationJson = null,
    string? AllowlistJson = null,
    bool? AuditValid = null,
    string? AuditError = null);
