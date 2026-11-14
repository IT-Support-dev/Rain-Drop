using RainDrop.AgentService;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์ตัวช่วยบล็อกเว็บผ่าน hosts
/// เข้าถึงเมธอด internal ได้ผ่าน InternalsVisibleTo("RainDrop.Tests") ใน AgentService.csproj
/// (ไม่ได้เปลี่ยนเมธอดเป็น public)
/// </summary>
public class HostsBlockerTests
{
    // ---------- ToHost ----------

    [Theory]
    [InlineData("https://www.facebook.com/x", "www.facebook.com")]
    [InlineData("http://example.com:8080/path", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("example.com/path?q=1", "example.com")]
    [InlineData("  HTTPS://Example.COM/Path  ", "example.com")]
    [InlineData("ftp://files.example.com/pub", "files.example.com")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("https://", "")]
    public void ToHost_StripsSchemePathAndPort(string raw, string expected) =>
        Assert.Equal(expected, HostsBlocker.ToHost(raw));

    [Fact]
    public void ToHost_KeepsWwwPrefixSoCallerCanAddTheVariant() =>
        Assert.Equal("www.example.com", HostsBlocker.ToHost("www.example.com"));

    // ---------- BuildBlock ----------

    [Fact]
    public void BuildBlock_NoDomains_ReturnsEmptyStringSoHostsBlockIsRemoved() =>
        Assert.Equal("", HostsBlocker.BuildBlock(new List<string>()));

    [Fact]
    public void BuildBlock_SingleDomain_WrapsInMarkersAndBlocksIPv4AndIPv6()
    {
        string block = HostsBlocker.BuildBlock(new List<string> { "example.com" });

        Assert.StartsWith("# RainDrop-BEGIN", block);
        Assert.EndsWith("# RainDrop-END", block);
        Assert.Contains("0.0.0.0 example.com" + Environment.NewLine, block);
        Assert.Contains(":: example.com" + Environment.NewLine, block);
    }

    [Fact]
    public void BuildBlock_PreservesOrderAndEmitsTwoLinesPerDomain()
    {
        string block = HostsBlocker.BuildBlock(new List<string> { "a.com", "b.com" });
        string nl = Environment.NewLine;

        Assert.StartsWith("# RainDrop-BEGIN", block);
        // ตรวจส่วนท้ายทั้งก้อนทีเดียว เพื่อยืนยันลำดับ + ทั้งสองตระกูล IP + END ต้องเป็นบรรทัดสุดท้าย
        Assert.EndsWith(
            "0.0.0.0 a.com" + nl + ":: a.com" + nl +
            "0.0.0.0 b.com" + nl + ":: b.com" + nl + "# RainDrop-END",
            block);
    }

    [Fact]
    public void ToHostThenBuildBlock_ProducesBlockedLineForUrlInput()
    {
        // ประกอบแบบที่ LoadDomains ใช้จริง: URL -> host -> บรรทัดใน hosts
        string host = HostsBlocker.ToHost("https://facebook.com/login");
        string block = HostsBlocker.BuildBlock(new List<string> { host });

        Assert.Contains("0.0.0.0 facebook.com", block);
    }
}
