using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using RainDrop.Agent;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์การเขียนค่าตั้งต้นของ Agent (เฟส C1)
/// AgentConfig เป็น internal จึงเข้าถึงผ่าน InternalsVisibleTo("RainDrop.Tests") ใน Agent.csproj
/// รหัสผ่านไม่อยู่ใน agent.json อีกต่อไป แต่แยกไป private\credentials.json และตรวจด้วย PasswordHashing
/// </summary>
public class AgentConfigTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "raindrop-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>คืนสิทธิ์ให้ผู้ใช้ปัจจุบันทั่วทั้งต้นไม้โฟลเดอร์แล้วจึงลบ
    /// ใช้กับเทสต์ที่แตะ HardenFolder ซึ่งตั้ง ACL ปิดกั้น Users ไม่ให้ลบไฟล์ใน private\\
    /// ระดับ production ไม่ได้แตะ — ACL เดิมยังคงเข้มงวดเหมือนเดิม</summary>
    private static void DeleteDirectoryWithPermissions(string path)
    {
        if (!Directory.Exists(path)) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var security = File.GetAccessControl(file);
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(file, security);
            }
            catch { }
        }
        foreach (var dir in Directory.GetDirectories(path, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var security = Directory.GetAccessControl(dir);
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                Directory.SetAccessControl(dir, security);
            }
            catch { }
        }
        try
        {
            var security = Directory.GetAccessControl(path);
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            Directory.SetAccessControl(path, security);
        }
        catch { }

        Directory.Delete(path, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void WriteInitial_RejectsEmptyOrShortPassword(string password)
    {
        string dir = TempDir();
        try
        {
            Assert.False(AgentConfig.WriteInitial(dir, password, 11400, "", out string error));
            Assert.NotEqual("", error);
            Assert.False(File.Exists(Path.Combine(dir, "agent.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) DeleteDirectoryWithPermissions(dir);
        }
    }

    [Fact]
    public void WriteInitial_RejectsControlCharacters()
    {
        string dir = TempDir();
        try
        {
            Assert.False(AgentConfig.WriteInitial(dir, "pass\nword123", 11400, "", out _));
        }
        finally
        {
            if (Directory.Exists(dir)) DeleteDirectoryWithPermissions(dir);
        }
    }

    [Fact]
    public void WriteInitial_AgentJsonHasNoPasswordFields()
    {
        string dir = TempDir();
        try
        {
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11500, "192.168.1.10", out _));

            string json = File.ReadAllText(Path.Combine(dir, "agent.json"));
            var config = JsonSerializer.Deserialize<AgentConfig>(json)
                ?? throw new InvalidDataException("อ่าน agent.json ไม่ได้");

            Assert.Equal(11500, config.Port);
            Assert.Equal("192.168.1.10", config.HubAddress);
            Assert.DoesNotContain("Password", json);
            Assert.DoesNotContain("installer-pass", json);
        }
        finally
        {
            if (Directory.Exists(dir)) DeleteDirectoryWithPermissions(dir);
        }
    }

    [Fact]
    public void WriteInitial_CredentialsFileVerifiesWithSharedHashing()
    {
        string dir = TempDir();
        try
        {
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11400, "", out _));

            string credPath = Path.Combine(dir, "private", "credentials.json");
            Assert.True(File.Exists(credPath));

            var creds = JsonSerializer.Deserialize<AgentConfig.StoredCredentials>(File.ReadAllText(credPath))
                ?? throw new InvalidDataException("อ่าน credentials.json ไม่ได้");
            Assert.NotNull(creds.PasswordSalt);
            Assert.NotNull(creds.PasswordHash);

            byte[] salt = Convert.FromBase64String(creds.PasswordSalt!);
            byte[] hash = Convert.FromBase64String(creds.PasswordHash!);
            Assert.True(PasswordHashing.Verify("installer-pass", salt, hash));
            Assert.False(PasswordHashing.Verify("installer-Pass", salt, hash));
            Assert.False(PasswordHashing.Verify("", salt, hash));

            // ต้องไม่เก็บรหัสจริงลงไฟล์
            Assert.DoesNotContain("installer-pass", File.ReadAllText(credPath));
        }
        finally
        {
            if (Directory.Exists(dir)) DeleteDirectoryWithPermissions(dir);
        }
    }
}
