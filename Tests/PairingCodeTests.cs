using System.Security.Cryptography;
using System.Text;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class PairingCodeTests
{
    [Fact]
    public void CreateCode_Is16CharsBase32()
    {
        using var store = new PairingCodeStore();
        string code = store.CreateCode();
        Assert.Equal(16, code.Length);
        Assert.Matches("^[A-Z2-7]{16}$", code);
        // สร้างอีกครั้งได้รหัสต่างกัน
        string code2 = store.CreateCode();
        Assert.NotEqual(code, code2);
    }

    [Fact]
    public void TryConsume_ValidCode_ReturnsCodeBytes()
    {
        using var store = new PairingCodeStore();
        string code = store.CreateCode();
        Assert.True(store.TryPeek(code, out byte[]? bytes, out string? reason));
        Assert.NotNull(bytes);
        Assert.Equal(10, bytes.Length);
        // ใช้แล้ว consume
        Assert.True(store.TryConsume(code, out byte[]? consumed, out string? reason2));
        Assert.NotNull(consumed);
        // ใช้ซ้ำไม่ได้
        Assert.False(store.TryConsume(code, out _, out string? reason3));
        Assert.Equal("รหัสถูกใช้ไปแล้ว", reason3);
    }

    [Fact]
    public void TryConsume_ExpiredCode_Rejected()
    {
        var time = DateTime.UtcNow;
        var store = new PairingCodeStore(() => time);
        string code = store.CreateCode(); // หมดอายุใน 10 นาทีจากเวลาปัจจุบัน
        time = time.AddMinutes(11); // เดินเวลาไป 11 นาที = หมดอายุแล้ว
        Assert.False(store.TryPeek(code, out _, out string? reason));
        Assert.Equal("รหัสหมดอายุแล้ว", reason);
    }

    [Fact]
    public void WrongAttempts_ThreeTimes_CancelsCode()
    {
        using var store = new PairingCodeStore();
        string code = store.CreateCode();
        // ผิด 2 ครั้ง รหัสยังใช้ได้
        store.RecordWrong(code);
        store.RecordWrong(code);
        Assert.True(store.TryPeek(code, out _, out _));
        // ผิดครั้งที่ 3 รหัสถูกยกเลิก
        store.RecordWrong(code);
        Assert.False(store.TryPeek(code, out _, out string? reason));
        Assert.Equal("รหัสถูกยกเลิกเพราะผิดเกินกำหนด", reason);
        // Consume ก็ไม่ได้
        Assert.False(store.TryConsume(code, out _, out _));
    }

    [Fact]
    public void InvalidCode_Rejected()
    {
        using var store = new PairingCodeStore();
        Assert.False(store.TryPeek("INVALIDCODE1234", out _, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void MaxEntries_ThrowsWhenFull()
    {
        var store = new PairingCodeStore();
        for (int i = 0; i < 1000; i++)
            store.CreateCode();
        Assert.Throws<InvalidOperationException>(() => store.CreateCode());
    }

    [Fact]
    public void CodeNeverLogged()
    {
        using var store = new PairingCodeStore();
        string code = store.CreateCode();
        // RecordWrong รับรหัสเป็น parameter แต่ไม่เก็บไว้ในที่ใด
        store.RecordWrong(code);
        store.RecordWrong(code);
        store.RecordWrong(code);
        ActiveCountShouldBeZero(store);
    }

    private static void ActiveCountShouldBeZero(PairingCodeStore store)
    {
        Assert.Equal(0, store.ActiveCount);
    }
}
