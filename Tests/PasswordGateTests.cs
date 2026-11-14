using RainDrop.AgentService;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์ข้อ 4: Service ตรวจรหัสผ่านเองพร้อม lockout (ลองผิด 5 ครั้ง ล็อก 5 นาที)
/// PasswordGate เป็น internal เข้าถึงผ่าน InternalsVisibleTo("RainDrop.Tests") ใน AgentService.csproj
/// ใช้นาฬิกาปลอมเพื่อตรวจการหมดอายุล็อกโดยไม่ต้องรอจริง
/// </summary>
public class PasswordGateTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (PasswordGate Gate, FakeClock Clock) MakeGate(string password = "correct-horse")
    {
        var clock = new FakeClock();
        byte[] salt = PasswordHashing.NewSalt();
        byte[] hash = PasswordHashing.Hash(password, salt);
        return (new PasswordGate(salt, hash, clock.Read), clock);
    }

    [Fact]
    public void CorrectPassword_Allows()
    {
        var (gate, _) = MakeGate();
        Assert.Equal(GateStatus.Allowed, gate.Verify("correct-horse").Status);
    }

    [Fact]
    public void WrongPassword_ReturnsWrongPassword()
    {
        var (gate, _) = MakeGate();
        Assert.Equal(GateStatus.WrongPassword, gate.Verify("nope").Status);
    }

    [Fact]
    public void NoPasswordConfigured_ReturnsNotConfigured()
    {
        var gate = new PasswordGate(null, null);
        Assert.False(gate.HasPassword);
        Assert.Equal(GateStatus.NotConfigured, gate.Verify("anything").Status);
    }

    [Fact]
    public void FiveWrongAttempts_LocksOut()
    {
        var (gate, _) = MakeGate();
        for (int i = 0; i < PasswordGate.MaxAttempts - 1; i++)
            Assert.Equal(GateStatus.WrongPassword, gate.Verify("wrong").Status);

        GateResult fifth = gate.Verify("wrong");
        Assert.Equal(GateStatus.LockedOut, fifth.Status);
        Assert.Equal(300, fifth.RetryAfterSeconds);
    }

    [Fact]
    public void DuringLockout_EvenCorrectPasswordIsRejected()
    {
        var (gate, clock) = MakeGate();
        for (int i = 0; i < PasswordGate.MaxAttempts; i++) gate.Verify("wrong");

        clock.Advance(TimeSpan.FromMinutes(1));
        GateResult result = gate.Verify("correct-horse");
        Assert.Equal(GateStatus.LockedOut, result.Status);
        Assert.Equal(240, result.RetryAfterSeconds);
    }

    [Fact]
    public void AfterLockoutExpires_CorrectPasswordAllowsAgain()
    {
        var (gate, clock) = MakeGate();
        for (int i = 0; i < PasswordGate.MaxAttempts; i++) gate.Verify("wrong");

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(GateStatus.Allowed, gate.Verify("correct-horse").Status);
    }

    [Fact]
    public void AfterLockoutExpires_WrongAttemptsStartFreshCount()
    {
        var (gate, clock) = MakeGate();
        for (int i = 0; i < PasswordGate.MaxAttempts; i++) gate.Verify("wrong");
        clock.Advance(TimeSpan.FromMinutes(5));

        // รอบใหม่: ผิด 4 ครั้งยังไม่ล็อก ครั้งที่ 5 ถึงล็อก
        for (int i = 0; i < PasswordGate.MaxAttempts - 1; i++)
            Assert.Equal(GateStatus.WrongPassword, gate.Verify("wrong").Status);
        Assert.Equal(GateStatus.LockedOut, gate.Verify("wrong").Status);
    }

    [Fact]
    public void CorrectPasswordResetsFailureCount()
    {
        var (gate, _) = MakeGate();
        for (int i = 0; i < PasswordGate.MaxAttempts - 1; i++) gate.Verify("wrong");
        Assert.Equal(GateStatus.Allowed, gate.Verify("correct-horse").Status);

        // หลังใส่ถูก ตัวนับรีเซ็ต ผิดอีก 4 ครั้งต้องยังไม่ล็อก
        for (int i = 0; i < PasswordGate.MaxAttempts - 1; i++)
            Assert.Equal(GateStatus.WrongPassword, gate.Verify("wrong").Status);
    }
}
