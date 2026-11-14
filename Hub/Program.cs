using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Hub;

/// <summary>
/// เซิร์ฟเวอร์กลางของ Rain Drop (รันบนเครื่องที่เปิดตลอด เช่น เครื่องผู้ดูแลหรือเซิร์ฟเวอร์)
/// อ่านลายนิ้วมือกุญแจชุมชนจาก hub-key.pem (กุญแจสาธารณะชุดเดียวกับที่ฝังในตัวติดตั้ง)
/// ไคลเอนต์ที่จะเข้าร่วมได้ต้องมีกุญแจชุดเดียวกัน
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int port = HubProtocol.DefaultPort;
        foreach (var a in args)
            if (a.StartsWith("--port=") && int.TryParse(a["--port=".Length..], out int p)) port = p;

        string? fingerprint = LoadCommunityFingerprint();
        if (fingerprint is null)
        {
            Console.Error.WriteLine("ไม่พบ hub-key.pem ข้าง ๆ โปรแกรม — ไม่ทราบว่าชุมชนไหนเข้าได้");
            Console.Error.WriteLine("ให้คัดลอก master-public.pem มาวางในชื่อ hub-key.pem");
            return 1;
        }

        Console.Title = "Rain Drop Hub";
        void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
        Log($"ลายนิ้วมือชุมชน: {fingerprint}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            new HubServer(port, fingerprint, Log).RunAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log("หยุดทำงาน: " + ex.Message);
        }
        return 0;
    }

    private static string? LoadCommunityFingerprint()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "hub-key.pem");
        if (!File.Exists(path)) return null;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(path));
            return Protocol.Fingerprint(key);
        }
        catch
        {
            return null;
        }
    }
}
