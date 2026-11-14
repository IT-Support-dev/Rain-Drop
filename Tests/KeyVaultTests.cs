using System.Security.Cryptography;
using RainDrop.Master;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์ข้อ 2: เข้ารหัสกุญแจลับด้วย DPAPI (CurrentUser), round-trip, ย้ายจาก PEM เดิมแล้วลบ plaintext
/// KeyVault เป็น internal เข้าถึงผ่าน InternalsVisibleTo("RainDrop.Tests") ใน Master.csproj
/// รันบน windows-latest เท่านั้น (DPAPI เป็น Windows API)
/// </summary>
public class KeyVaultTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "raindrop-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProtectUnprotect_RoundTrips()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(64);
        byte[] blob = KeyVault.Protect(secret);

        Assert.NotEqual(secret, blob);             // ต้องไม่เก็บ plaintext ตรง ๆ
        Assert.True(blob.Length > secret.Length);  // DPAPI เพิ่ม header
        Assert.Equal(secret, KeyVault.Unprotect(blob));
    }

    [Fact]
    public void Protect_SamePlaintextTwice_ProducesDifferentBlobs()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] first = KeyVault.Protect(secret);
        byte[] second = KeyVault.Protect(secret);
        Assert.NotEqual(first, second);            // DPAPI ใส่ salt แบบสุ่ม
        Assert.Equal(secret, KeyVault.Unprotect(first));
        Assert.Equal(secret, KeyVault.Unprotect(second));
    }

    [Fact]
    public void SaveLoadEncrypted_RoundTripsThroughFile()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "key.dpapi");
            byte[] secret = RandomNumberGenerator.GetBytes(48);
            KeyVault.SaveEncrypted(path, secret);

            Assert.True(File.Exists(path));
            Assert.NotEqual(secret, File.ReadAllBytes(path));
            Assert.Equal(secret, KeyVault.LoadEncrypted(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unprotect_TamperedBlob_Throws()
    {
        byte[] blob = KeyVault.Protect(RandomNumberGenerator.GetBytes(32));
        blob[blob.Length - 1] ^= 0xFF;   // แก้ไบต์สุดท้ายให้ MAC ไม่ตรง
        Assert.ThrowsAny<CryptographicException>(() => KeyVault.Unprotect(blob));
    }

    [Theory]
    [InlineData(false)]   // SEC1 ("EC PRIVATE KEY") — รูปแบบที่รุ่นเก่าเขียน
    [InlineData(true)]    // PKCS8 ("PRIVATE KEY")
    public void MigrateFromPem_EncryptsThenSecurelyDeletesPlaintext(bool pkcs8)
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string pemPath = Path.Combine(dir, "master-private.pem");
            string blobPath = Path.Combine(dir, "master-private.dpapi");

            using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pem = pkcs8 ? original.ExportPkcs8PrivateKeyPem() : original.ExportECPrivateKeyPem();
            File.WriteAllText(pemPath, pem);

            Assert.True(KeyVault.MigrateFromPem(pemPath, blobPath));
            Assert.False(File.Exists(pemPath));    // plaintext ต้องถูกลบ
            Assert.True(File.Exists(blobPath));

            // blob ที่ถอดแล้วต้องเป็น PKCS8 DER ของกุญแจเดิม (ลายนิ้วมือสาธารณะตรงกัน)
            byte[] pkcs8Der = KeyVault.LoadEncrypted(blobPath);
            using var restored = ECDsa.Create();
            restored.ImportPkcs8PrivateKey(pkcs8Der, out _);
            Assert.Equal(original.ExportSubjectPublicKeyInfo(), restored.ExportSubjectPublicKeyInfo());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MigrateFromPem_NoPem_ReturnsFalse()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            Assert.False(KeyVault.MigrateFromPem(
                Path.Combine(dir, "missing.pem"), Path.Combine(dir, "key.dpapi")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecureDelete_RemovesFile()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "secret.txt");
            File.WriteAllText(path, " sensitive material ");
            KeyVault.SecureDelete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecureDelete_MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(TempDir(), "missing.txt");
        KeyVault.SecureDelete(path);   // ต้องไม่โยนแม้ไฟล์ไม่มี
    }
}
