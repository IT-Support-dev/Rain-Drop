using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์ข้อ 6: ตัวตรวจชื่อโฮสต์ที่กันการแทรกบรรทัดลงไฟล์ hosts
/// </summary>
public class HostnameTests
{
    [Theory]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData("a.co")]
    [InlineData("sub.domain.example.com")]
    [InlineData("xn--bcher-kva.com")]   // punycode ใช้ได้
    [InlineData("a-b.com")]
    [InlineData("123.example.com")]     // label ขึ้นต้นด้วยเลขได้ ถ้าไม่ใช่ IPv4 ล้วน
    public void ValidHostnames_Accepted(string host) => Assert.True(Hostname.IsValid(host));

    [Theory]
    [InlineData("")]
    [InlineData("example.com\n")]
    [InlineData("example.com\r\n0.0.0.0 bank.com")]
    [InlineData("example.com#x")]
    [InlineData("example.com;x")]
    [InlineData("example.com x")]
    [InlineData("example.com\t")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("example..com")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("192.168.1.1")]
    [InlineData("1.2.3.4")]
    [InlineData("EXAMPLE.COM")]          // ต้อง lowercase มาก่อน (IsValid ไม่แปลงให้)
    [InlineData("例え.テスト")]            // ยูนิโค้ดไม่อนุญาต
    [InlineData("example.com/")]
    [InlineData("example.com:80")]
    public void InvalidHostnames_Rejected(string host) => Assert.False(Hostname.IsValid(host));

    [Fact]
    public void LabelLongerThan63_Rejected()
    {
        string host = new string('a', 64) + ".com";
        Assert.False(Hostname.IsValid(host));
    }

    [Fact]
    public void TotalLongerThan253_Rejected()
    {
        string label = new string('a', 63);
        string host = string.Join(".", new[] { label, label, label, label, "com" });
        Assert.True(host.Length > Hostname.MaxLength);
        Assert.False(Hostname.IsValid(host));
    }

    [Theory]
    [InlineData("https://www.facebook.com/x", "www.facebook.com")]
    [InlineData("http://example.com:8080/path", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("example.com/path?q=1", "example.com")]
    [InlineData("  HTTPS://Example.COM/Path  ", "example.com")]
    [InlineData("ftp://files.example.com/pub", "files.example.com")]
    [InlineData("https://user:pass@example.com/x", "example.com")]
    [InlineData("example.com/foo@bar", "example.com")]   // @ ใน path ไม่ใช่ userinfo
    public void TryFromUrl_ExtractsHostname(string raw, string expected)
    {
        Assert.True(Hostname.TryFromUrl(raw, out string host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("https://evil.com\n0.0.0.0 bank.com")]
    [InlineData("evil.com\r\n injection")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://-evil.com")]
    public void TryFromUrl_DangerousInput_ReturnsFalse(string raw)
    {
        Assert.False(Hostname.TryFromUrl(raw, out string host));
        Assert.Equal("", host);
    }

    [Fact]
    public void TryFromUrl_Null_ReturnsFalse()
    {
        Assert.False(Hostname.TryFromUrl(null, out string host));
        Assert.Equal("", host);
    }
}
