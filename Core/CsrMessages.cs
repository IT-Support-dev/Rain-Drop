using System.Security.Cryptography.X509Certificates;

namespace RainDrop.Shared;

/// <summary>ข้อความสำหรับการ enroll ผ่านช่อง pairing code (แยกจาก control channel แพ็กเก็ตเดิม)
/// ส่งภายในช่อง TLS ที่พิสูจน์ตัวตนแล้วเท่านั้น</summary>

/// <summary>Agent ส่ง CSR ไปยัง Master เพื่อขอให้เซ็นใบรับรอง</summary>
public sealed record CsrRequest(
    string MachineId,           // GUID without braces
    string MachineName,         // ชื่อเครื่อง (สูงสุด 128 ตัวอักษร ไม่มีตัวควบคุม)
    string[] Addresses,         // IP/DNS ที่เครื่องใช้อาจเปลี่ยนได้
    byte[] CsrDer,              // CSR ในรูปแบบ DER (PKCS#10)
    byte[] Proof                // HMAC proof ของ Agent ที่ตรวจแล้วจาก Master
);

/// <summary>Master ตอบกลับพร้อมใบรับรองที่เซ็นแล้ว + CA public cert</summary>
public sealed record CsrResponse(
    bool Ok,
    string? Error,
    byte[]? AgentCertDer,       // ใบรับรอง Agent ที่ Master เซ็นแล้ว (DER)
    byte[]? CaCertDer,          // ใบรับรอง CA ของ Master (DER) สำหรับ Agent เก็บไว้เชื่อมต่อครั้งถัดไป
    string? Serial,             // ลำดับที่ของใบรับรอง
    DateTime? NotAfterUtc       // วันหมดอายุของใบรับรอง
);

/// <summary>ผลการตรวจ CSR ก่อนเซ็น</summary>
public sealed record CsrValidationResult(
    bool Valid,
    string? Reason
);
