using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>
/// ฝั่ง Agent สำหรับคุยกับ AgentService ผ่าน named pipe
/// แทนกลไกเดิมที่อ่าน token จาก environment แล้วเขียนไฟล์สัญญาณหยุด
/// Agent ไม่เก็บ hash รหัสผ่านอีกต่อไป ส่งรหัสที่ผู้ใช้กรอกไปให้ Service ตรวจเอง
/// </summary>
internal static class ServiceClient
{
    /// <summary>ส่งคำขอหนึ่งครั้งแล้วรอคำตอบ คืน null เมื่อต่อท่อไม่ได้พร้อมเหตุผลใน error</summary>
    public static ControlResponse? Send(ControlRequest request, out string? error)
    {
        error = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", ControlChannel.PipeName, PipeDirection.InOut);
            pipe.Connect(ControlChannel.ConnectTimeoutMs);
            if (!pipe.IsConnected)
            {
                error = "ต่อบริการไม่ได้";
                return null;
            }

            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            pipe.ReadTimeout = ControlChannel.ConnectTimeoutMs;
            string? line = ReadLineBounded(pipe);
            if (line is null)
            {
                error = "คำตอบจากบริการเกินขนาดหรือไม่ครบรูปแบบ";
                return null;
            }

            return JsonSerializer.Deserialize<ControlResponse>(line);
        }
        catch (TimeoutException)
        {
            // ท่อไม่มีอยู่/บริการยังไม่พร้อม เป็นเงื่อนไขที่ผู้ใช้ต้องเห็น ไม่ใช่ error ที่กลืนได้
            error = "บริการ Rain Drop ยังไม่พร้อม (อาจกำลังเริ่มทำงาน) ลองอีกครั้งในไม่ช้า";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "ผู้ใช้คนนี้ไม่มีสิทธิ์ต่อบริการ Rain Drop";
            return null;
        }
        catch (IOException ex)
        {
            error = "การสื่อกับบริการขัดข้อง: " + ex.Message;
            return null;
        }
        catch (JsonException ex)
        {
            error = "คำตอบจากบริการไม่ถูกต้อง: " + ex.Message;
            return null;
        }
    }

    /// <summary>ขออนุญาตออกจากโปรแกรม Service เป็นผู้ตรวจรหัสผ่านและนับครั้งลองผิด</summary>
    public static ControlResponse? RequestStop(string password, out string? error) =>
        Send(new ControlRequest(ControlChannel.OpStop, password), out error);

    /// <summary>ส่งรายการบล็อกให้ Service ตรวจรูปแบบและเขียนลงที่ที่ผู้ใช้ทั่วไปแก้ไม่ได้</summary>
    public static ControlResponse? SetBlocklist(BlockRules rules, out string? error) =>
        Send(new ControlRequest(ControlChannel.OpSetBlocklist, null, rules.Programs, rules.Urls), out error);

    /// <summary>ดึงรายการบล็อกที่ Service เก็บไว้กลับมาใช้ตอน Agent เริ่มใหม่</summary>
    public static BlockRules? GetBlocklist(out string? error)
    {
        ControlResponse? response = Send(new ControlRequest(ControlChannel.OpGetBlocklist), out error);
        if (response is null || !response.Ok) return null;
        return new BlockRules(response.Programs ?? new List<string>(), response.Urls ?? new List<string>());
    }


    /// <summary>ขอ identity จาก Service เฉพาะโปรเซส Agent ที่ Service เปิด (ตรวจ PID ที่ปลายท่อ)</summary>
    public static ControlResponse? GetIdentity(out string? error) =>
        Send(new ControlRequest(ControlChannel.OpGetIdentity), out error);

    public static bool Audit(string eventName, string ip, string result, string detail, out string? error)
    {
        var response = Send(new ControlRequest(ControlChannel.OpAudit, null, null, null,
            eventName, ip, result, detail), out error);
        return response is { Ok: true };
    }

    public static bool SaveRevocation(string json, out string? error)
    {
        var response = Send(new ControlRequest(ControlChannel.OpSaveRevocation, RevocationJson: json), out error);
        return response is { Ok: true };
    }

    public static ControlResponse? VerifyAudit(out string? error) =>
        Send(new ControlRequest(ControlChannel.OpVerifyAudit), out error);

    private static string? ReadLineBounded(Stream pipe)
    {
        using var accumulated = new MemoryStream();
        var chunk = new byte[4096];
        while (accumulated.Length <= ControlChannel.MaxMessageBytes)
        {
            int read = pipe.Read(chunk, 0, chunk.Length);
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
}
