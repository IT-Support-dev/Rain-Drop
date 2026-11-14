using System.Security.Cryptography;

namespace RainDrop.Shared;

/// <summary>
/// สูตร hash รหัสผ่านผู้ดูแลที่ใช้ร่วมกันทั้งตัวติดตั้ง (คนเขียน) และ Service (คนตรวจ)
/// ต้องตรงกันทุกค่า ไม่งั้นรหัสที่ตั้งตอนติดตั้งจะตรวจไม่ผ่าน
/// </summary>
public static class PasswordHashing
{
    public const int Iterations = 100_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int MinPasswordLength = 8;

    public static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>รหัสผ่านใช้ได้หรือไม่ (ตัวติดตั้งบังคับขั้นต่ำ 8 และห้ามว่าง)</summary>
    public static bool IsAcceptable(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinPasswordLength &&
        !password.Any(char.IsControl);

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);

    /// <summary>เทียบแบบเวลาเท่ากัน กันการเดาจากเวลาที่ใช้ตอบ</summary>
    public static bool Verify(string? password, byte[]? salt, byte[]? hash)
    {
        if (password is null || salt is not { Length: SaltBytes } || hash is not { Length: > 0 })
            return false;

        byte[] actual = Hash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, hash);
    }
}
