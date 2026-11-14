using System.Security.Cryptography;
using System.Text;

namespace RainDrop.Master;

/// <summary>
/// เก็บความลับของฝั่ง Master ด้วย DPAPI scope CurrentUser
/// กุญแจลับจะถอดรหัสได้เฉพาะบัญชี Windows เดิมบนเครื่องเดิมเท่านั้น
/// คัดลอกไฟล์ไปเครื่องอื่นหรือให้ผู้ใช้อื่นอ่านก็ใช้ไม่ได้
///
/// ข้อจำกัดที่ต้องรู้: SecureDelete เขียนทับไฟล์ก่อนลบ ซึ่งเป็น best-effort
/// บน SSD/NTFS ที่มีการ remap หรือมี shadow copy/backup อยู่ ไม่รับประกันว่าข้อมูลต้นฉบับหายจริง
/// การป้องกันที่ครบถ้วนต้องพึ่ง BitLocker และการสำรองข้อมูลที่เข้ารหัส
/// </summary>
internal static class KeyVault
{
    // ผูก blob กับแอปอีกชั้น กันการนำ blob ไปให้โปรแกรมอื่นถอดในบริบทผู้ใช้เดียวกันโดยไม่ตั้งใจ
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RainDrop.MasterPrivateKey.v1");

    public static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);

    public static void SaveEncrypted(string path, byte[] plaintext)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Protect(plaintext));
    }

    public static byte[] LoadEncrypted(string path) => Unprotect(File.ReadAllBytes(path));

    /// <summary>เขียนทับด้วยค่าสุ่มแล้วค่าศูนย์ ก่อนลบไฟล์ (ดูข้อจำกัดด้านบน)</summary>
    public static void SecureDelete(string path)
    {
        if (!File.Exists(path)) return;

        long length = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var random = new byte[Math.Max(1, (int)Math.Min(length, 1 << 20))];
            long remaining = length;
            while (remaining > 0)
            {
                RandomNumberGenerator.Fill(random);
                int chunk = (int)Math.Min(remaining, random.Length);
                stream.Write(random, 0, chunk);
                remaining -= chunk;
            }
            stream.Position = 0;
            Array.Clear(random);
            remaining = length;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, random.Length);
                stream.Write(random, 0, chunk);
                remaining -= chunk;
            }
            stream.Flush(flushToDisk: true);
        }
        File.Delete(path);
    }

    /// <summary>
    /// ย้ายกุญแจ PEM แบบไม่เข้ารหัสเดิมไปเป็น blob DPAPI แล้วลบไฟล์ plaintext ทิ้ง
    /// คืน true เมื่อมีการย้ายจริง
    /// </summary>
    public static bool MigrateFromPem(string pemPath, string blobPath)
    {
        if (!File.Exists(pemPath)) return false;

        // PEM เดิมอาจเป็น SEC1 ("EC PRIVATE KEY") หรือ PKCS8 ("PRIVATE KEY") ก็ได้
        // ImportFromPem รับทั้งสองแบบ แล้วแปลงเป็น PKCS8 DER ก่อนเข้ารหัส เพื่อให้รูปแบบ blob คงที่เสมอ
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(pemPath));
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            SaveEncrypted(blobPath, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        // ลบไฟล์ต้นฉบับเฉพาะเมื่อเขียน blob สำเร็จแล้ว (ถ้าโยน ด้านบนจะไม่มาถึงบรรทัดนี้)
        SecureDelete(pemPath);
        return true;
    }
}
