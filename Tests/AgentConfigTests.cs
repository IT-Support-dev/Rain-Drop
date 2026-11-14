using System.Security.Cryptography;
using System.Text.Json;
using RainDrop.Agent;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์รหัสผ่านสำหรับ "ออกจากโปรแกรม" ของ Agent (PBKDF2)
/// AgentConfig เป็น internal จึงเข้าถึงผ่าน InternalsVisibleTo("RainDrop.Tests") ใน Agent.csproj
/// </summary>
public class AgentConfigTests
{
    private const int Iterations = 100_000;   // ต้องตรงกับที่ AgentConfig ใช้จริง

    private static AgentConfig WithPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new AgentConfig
        {
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
        };
    }

    [Fact]
    public void VerifyPassword_NoPasswordConfigured_AllowsExit()
    {
        var config = new AgentConfig();
        Assert.False(config.HasPassword);
        Assert.True(config.VerifyPassword("anything"));
    }

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue() =>
        Assert.True(WithPassword("s3cret").VerifyPassword("s3cret"));

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse() =>
        Assert.False(WithPassword("s3cret").VerifyPassword("wrong"));

    [Fact]
    public void VerifyPassword_EmptyInputAgainstSetPassword_ReturnsFalse() =>
        Assert.False(WithPassword("s3cret").VerifyPassword(""));

    [Fact]
    public void VerifyPassword_IsCaseSensitive() =>
        Assert.False(WithPassword("Secret").VerifyPassword("secret"));

    [Fact]
    public void VerifyPassword_CorruptBase64_ReturnsFalseInsteadOfThrowing()
    {
        var config = new AgentConfig { PasswordSalt = "not-base64!!", PasswordHash = "also-bad!!" };
        Assert.True(config.HasPassword);
        Assert.False(config.VerifyPassword("anything"));
    }

    [Fact]
    public void VerifyPassword_HashWithoutSalt_IsTreatedAsNoPassword()
    {
        // พฤติกรรมเดิม: HasPassword ต้องมีทั้ง hash และ salt ถ้าขาดตัวใดตัวหนึ่งถือว่าไม่ได้ตั้งรหัส
        var config = new AgentConfig { PasswordHash = Convert.ToBase64String(new byte[32]) };
        Assert.False(config.HasPassword);
        Assert.True(config.VerifyPassword("anything"));
    }

    [Fact]
    public void VerifyPassword_DifferentSaltSamePassword_ReturnsFalse()
    {
        var first = WithPassword("same-password");
        var second = WithPassword("same-password");
        Assert.NotEqual(first.PasswordSalt, second.PasswordSalt);

        // เอา hash ของตัวแรกมาจับกับ salt ของตัวที่สอง ต้องตรวจไม่ผ่าน
        var mixed = new AgentConfig { PasswordSalt = second.PasswordSalt, PasswordHash = first.PasswordHash };
        Assert.False(mixed.VerifyPassword("same-password"));
    }

    [Fact]
    public void WriteInitial_ProducesConfigThatVerifiesPasswordAndStoresNoPlaintext()
    {
        string dir = Path.Combine(Path.GetTempPath(), "raindrop-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11500, "192.168.1.10"));

            string json = File.ReadAllText(Path.Combine(dir, "agent.json"));
            var config = JsonSerializer.Deserialize<AgentConfig>(json)
                ?? throw new InvalidDataException("อ่าน agent.json ที่เพิ่งเขียนไม่ได้");

            Assert.Equal(11500, config.Port);
            Assert.Equal("192.168.1.10", config.HubAddress);
            Assert.True(config.HasPassword);
            Assert.True(config.VerifyPassword("installer-pass"));
            Assert.False(config.VerifyPassword("installer-Pass"));
            Assert.DoesNotContain("installer-pass", json);   // ต้องไม่เก็บรหัสจริงลงไฟล์
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
