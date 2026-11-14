using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์ข้อ 5–6: Service ต้องปฏิเสธรายการบล็อกที่ผิดรูป/อันตรายก่อนเขียนลง hosts
/// BlocklistValidator อยู่ใน Core (public) จึงทดสอบได้ตรง ๆ
/// </summary>
public class BlocklistValidatorTests
{
    private static BlockRules Rules(string[] programs, string[] urls) =>
        new(new List<string>(programs), new List<string>(urls));

    [Fact]
    public void NullRules_Rejected()
    {
        Assert.False(BlocklistValidator.TryNormalize(null, out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void ValidRules_NormalizedAndDeduplicated()
    {
        var input = Rules(
            new[] { "chrome.exe", "Chrome", "game" },
            new[] { "https://www.Facebook.com/login", "facebook.com", "example.com:8080/path" });

        Assert.True(BlocklistValidator.TryNormalize(input, out BlockRules normalized, out _));
        Assert.Equal(new[] { "chrome", "game" }, normalized.Programs);
        Assert.Equal(new[] { "www.facebook.com", "facebook.com", "example.com" }, normalized.Urls);
    }

    [Fact]
    public void EmptyLists_AreValid()
    {
        Assert.True(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), Array.Empty<string>()),
            out BlockRules normalized, out _));
        Assert.Empty(normalized.Programs);
        Assert.Empty(normalized.Urls);
    }

    [Theory]
    [InlineData("evil.com\n127.0.0.1 bank.com")]   // แทรกบรรทัดใหม่ลง hosts
    [InlineData("evil.com\r\n0.0.0.0 bank.com")]
    [InlineData("evil.com;drop")]
    [InlineData("evil.com 127.0.0.1")]              // เว้นวรรค = หลายคอลัมน์ใน hosts
    [InlineData("evil.com\ttab")]
    [InlineData("-evil.com")]
    [InlineData("evil-.com")]
    [InlineData("evil..com")]
    [InlineData(".evil.com")]
    [InlineData("evil.com.")]
    [InlineData("192.168.1.1")]                     // IPv4 literal ไม่ใช่ชื่อโฮสต์
    [InlineData("http://")]
    [InlineData("evil.com:99999")]                  // พอร์ตถูกตัดเหลือ evil.com -> ใช้ได้ เคสนี้ตรวจด้านล่างแทน
    public void DangerousOrInvalidHosts_Rejected(string url)
    {
        // "evil.com:99999" ถูกตัดพอร์ตเหลือ evil.com ซึ่งถูกต้อง จึงไม่ใช่เคสปฏิเสธ
        if (url == "evil.com:99999")
        {
            Assert.True(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), new[] { url }), out _, out _));
            return;
        }
        Assert.False(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), new[] { url }), out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void HostInjectionViaUrl_Rejected()
    {
        // ความพยายามแทรกบรรทัด hosts ผ่าน URL ที่ส่งมาทาง pipe
        string malicious = "https://good.com\n0.0.0.0 bank.com";
        Assert.False(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), new[] { malicious }), out _, out _));
    }

    [Theory]
    [InlineData("bad\\name.exe")]
    [InlineData("bad/name.exe")]
    [InlineData("bad:name.exe")]
    [InlineData("bad\nname.exe")]
    [InlineData("bad\"name.exe")]
    [InlineData("bad*name.exe")]
    public void DangerousProgramNames_Rejected(string program)
    {
        Assert.False(BlocklistValidator.TryNormalize(Rules(new[] { program }, Array.Empty<string>()), out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TooManyPrograms_Rejected()
    {
        string[] programs = Enumerable.Range(0, BlocklistValidator.MaxPrograms + 1)
            .Select(i => "prog" + i).ToArray();
        Assert.False(BlocklistValidator.TryNormalize(Rules(programs, Array.Empty<string>()), out _, out _));
    }

    [Fact]
    public void TooManyUrls_Rejected()
    {
        string[] urls = Enumerable.Range(0, BlocklistValidator.MaxUrls + 1)
            .Select(i => "host" + i + ".com").ToArray();
        Assert.False(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), urls), out _, out _));
    }

    [Fact]
    public void OverlongProgram_Rejected()
    {
        string program = new string('a', BlocklistValidator.MaxProgramLength + 1);
        Assert.False(BlocklistValidator.TryNormalize(Rules(new[] { program }, Array.Empty<string>()), out _, out _));
    }

    [Fact]
    public void OverlongUrl_Rejected()
    {
        string url = "https://" + new string('a', BlocklistValidator.MaxUrlLength) + ".com";
        Assert.False(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), new[] { url }), out _, out _));
    }

    [Fact]
    public void OverlongHostname_Rejected()
    {
        // โฮสต์ยาวเกิน 253 อักขระ (แต่ละ label ยาว 63)
        string label = new string('a', 63);
        string host = string.Join(".", new[] { label, label, label, label, "com" }); // 63*4+3*4+3 = 267
        Assert.False(BlocklistValidator.TryNormalize(Rules(Array.Empty<string>(), new[] { host }), out _, out _));
    }

    [Fact]
    public void OneBadEntry_RejectsWholeRequest()
    {
        // fail-closed: มีรายการดีปนรายการอันตราย ต้องปฏิเสธทั้งก้อน ไม่เขียนบางส่วน
        var input = Rules(Array.Empty<string>(), new[] { "good.com", "evil.com\n0.0.0.0 bank.com" });
        Assert.False(BlocklistValidator.TryNormalize(input, out BlockRules normalized, out _));
        Assert.Empty(normalized.Urls);
    }
}
