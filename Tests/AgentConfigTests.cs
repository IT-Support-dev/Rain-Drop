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

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void WriteInitial_RejectsEmptyOrShortPassword(string password)
    {
        string dir = TempDir();
        try
        {
            Assert.False(AgentConfig.WriteInitial(dir, password, 11400, "", out string error, keepCurrentUser: true));
            Assert.NotEqual("", error);
            Assert.False(File.Exists(Path.Combine(dir, "agent.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteInitial_RejectsControlCharacters()
    {
        string dir = TempDir();
        try
        {
            Assert.False(AgentConfig.WriteInitial(dir, "pass\nword123", 11400, "", out _, keepCurrentUser: true));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteInitial_AgentJsonHasNoPasswordFields()
    {
        string dir = TempDir();
        try
        {
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11500, "192.168.1.10", out _, keepCurrentUser: true));

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
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteInitial_CredentialsFileVerifiesWithSharedHashing()
    {
        string dir = TempDir();
        try
        {
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11400, "", out _, keepCurrentUser: true));

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
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>ยืนยันว่า keepCurrentUser=false (ค่าเริ่มต้น production) ตัดสิทธิ์ FullControl
    /// ของผู้ใช้ปัจจุบันออกจาก private\ จริง — ไม่ให้เทสต์หรือมัลแวร์ที่รันฐานะผู้ใช้
    /// ทั่วไปอ่าน/เขียนไฟล์ลับใน private\ ได้</summary>
    [Fact]
    public void HardenFolder_KeepCurrentUserFalse_CutsUserFullControl()
    {
        string dir = TempDir();
        try
        {
            // keepCurrentUser ค่าเริ่มต้นคือ false — ต้องตัดสิทธิ์ผู้ใช้ทั่วไป
            Assert.True(AgentConfig.WriteInitial(dir, "installer-pass", 11400, "", out _));

            string privateDir = Path.Combine(dir, "private");
            var security = new DirectoryInfo(privateDir).GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;

            var userHasFullControl = security.GetAccessRules(true, true, typeof(NTAccount))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Value.Equals(currentUser.Value, StringComparison.OrdinalIgnoreCase)
                    && r.AccessControlType == AccessControlType.Allow
                    && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

            Assert.False(userHasFullControl,
                "keepCurrentUser=false ต้องไม่ให้ FullControl แก่ผู้ใช้ปัจจุบันบน private\\");
        }
        finally
        {
            // เปิดสิทธิ์คืนด้วย keepCurrentUser: true แล้วค่อยลบ (กันโฟลเดอร์ถูกล็อกจาก HardenFolder)
            if (Directory.Exists(dir))
                AgentConfig.WriteInitial(dir, "cleanup-pass", 11400, "", out _, keepCurrentUser: true);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
