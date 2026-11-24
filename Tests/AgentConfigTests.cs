using System.Linq;
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

    private static string DumpAces(DirectorySecurity security)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        return string.Join("; ", rules.Select(r =>
            $"{r.IdentityReference.Value}={r.AccessControlType}:{r.FileSystemRights} Inherited={r.IsInherited}"));
    }

    /// <summary>ยืนยันว่า keepCurrentUser=false (ค่าเริ่มต้น production) ตัดสิทธิ์ FullControl
    /// ของผู้ใช้ปัจจุบันและ well-known SIDs อันตรายออกจาก private\ จริง — ไม่ให้เทสต์หรือ
    /// มัลแวร์ที่รันฐานะผู้ใช้ทั่วไปอ่าน/เขียนไฟล์ลับใน private\ ได้</summary>
    [Fact]
    public void HardenFolder_KeepCurrentUserFalse_CutsUserFullControl()
    {
        string dir = TempDir();
        try
        {
            // สร้างโฟลเดอร์เองแล้วเรียก HardenFolder ตรงๆ (ไม่ผ่าน WriteInitial
            // เพราะ WriteInitial อาจล้มบนบัญชีไม่ elevated จากข้อจำกัดการตั้ง ACL)
            string privateDir = Path.Combine(dir, "private");
            Directory.CreateDirectory(privateDir);

            Program.HardenFolder(privateDir, usersRead: false, keepCurrentUser: false);

            var security = new DirectoryInfo(privateDir).GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var aces = DumpAces(security);

            // ตรวจว่า inheritance ถูกปิด
            Assert.True(security.AreAccessRulesProtected,
                $"Inheritance ต้องถูกปิดบน private\\. ACEs ที่พบ: {aces}");

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>();

            // ตรวจว่าไม่มี ACE Allow ให้ผู้ใช้สร้างโฟลเดอร์ (เทียบ SID กับ SID)
            var userSid = currentUser.Value;
            bool userHasAllow = rules.Any(r =>
                r.IdentityReference is SecurityIdentifier sid && sid.Value.Equals(userSid, StringComparison.OrdinalIgnoreCase)
                && r.AccessControlType == AccessControlType.Allow);

            Assert.False(userHasAllow,
                $"keepCurrentUser=false ต้องไม่มี ACE Allow ให้ผู้ใช้สร้างโฟลเดอร์ (SID={userSid}). ACEs ที่พบ: {aces}");

            // ตรวจว่าไม่มี ACE Allow ให้ well-known SIDs ที่อันตราย
            foreach (var wellKnownSid in new[] { "S-1-5-32-545", "S-1-5-11", "S-1-1-0" })
            {
                string wellKnownName = wellKnownSid switch
                {
                    "S-1-5-32-545" => "Users",
                    "S-1-5-11" => "Authenticated Users",
                    "S-1-1-0" => "Everyone",
                    _ => wellKnownSid
                };
                bool wellKnownHasAllow = rules.Any(r =>
                    r.IdentityReference is SecurityIdentifier sid && sid.Value.Equals(wellKnownSid, StringComparison.OrdinalIgnoreCase)
                    && r.AccessControlType == AccessControlType.Allow);

                Assert.False(wellKnownHasAllow,
                    $"keepCurrentUser=false ต้องไม่มี ACE Allow ให้ {wellKnownName} ({wellKnownSid}). ACEs ที่พบ: {aces}");
            }
        }
        finally
        {
            // เปิดสิทธิ์คืนด้วย keepCurrentUser: true แล้วค่อยลบ (เจ้าของโฟลเดอร์เรียก SetAccessControl ได้เสมอ)
            if (Directory.Exists(dir))
            {
                string cleanupPrivate = Path.Combine(dir, "private");
                if (Directory.Exists(cleanupPrivate))
                    Program.HardenFolder(cleanupPrivate, usersRead: false, keepCurrentUser: true);
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>ยืนยันว่า keepCurrentUser=true ให้ FullControl แก่ผู้ใช้ปัจจุบันจริง
    /// เพื่อพิสูจน์ว่าเทสต์จับการให้สิทธิ์ได้จริง (เทสต์เชิงบวก)</summary>
    [Fact]
    public void HardenFolder_KeepCurrentUserTrue_GrantsUserFullControl()
    {
        string dir = TempDir();
        try
        {
            string privateDir = Path.Combine(dir, "private");
            Directory.CreateDirectory(privateDir);

            Program.HardenFolder(privateDir, usersRead: false, keepCurrentUser: true);

            var security = new DirectoryInfo(privateDir).GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var aces = DumpAces(security);

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>();

            bool userHasFullControl = rules.Any(r =>
                r.IdentityReference is SecurityIdentifier sid && sid.Value.Equals(currentUser.Value, StringComparison.OrdinalIgnoreCase)
                && r.AccessControlType == AccessControlType.Allow
                && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

            Assert.True(userHasFullControl,
                $"keepCurrentUser=true ต้องให้ FullControl แก่ผู้ใช้ปัจจุบัน (SID={currentUser.Value}). ACEs ที่พบ: {aces}");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
