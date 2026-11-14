using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class RunCommandPolicyTests
{
    [Theory]
    [InlineData("cmd.exe", "/c echo hi")]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "/c echo hi")]
    [InlineData("powershell", "-Command Get-Process")]
    [InlineData("file:///C:/windows/win.ini", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("https://example.com", "extra")]
    [InlineData("notepad.exe", null)]
    public void OutsideAllowlist_IsDenied(string target, string? args)
    {
        var decision = RunCommandPolicy.Evaluate(new RunCmd(target, args), new RunAllowlist(new List<string>()));
        Assert.False(decision.Allowed);
        Assert.Null(decision.StartInfo);
    }

    [Fact]
    public void HttpsUrl_WithoutArguments_IsAllowed()
    {
        var decision = RunCommandPolicy.Evaluate(new RunCmd("https://example.com/path", null), new RunAllowlist(new List<string>()));
        Assert.True(decision.Allowed);
        Assert.Equal("url", decision.Reason);
    }

    [Fact]
    public void AllowlistedProgram_UsesArgumentListNotShell()
    {
        string path = Path.Combine(Path.GetTempPath(), "raindrop-test.exe");
        var list = new RunAllowlist(new List<string> { path });
        var decision = RunCommandPolicy.Evaluate(new RunCmd(path, "\"a b\" & calc.exe"), list);
        Assert.True(decision.Allowed);
        Assert.False(decision.StartInfo!.UseShellExecute);
        Assert.Equal(new[] { "a b", "&", "calc.exe" }, decision.StartInfo.ArgumentList);
    }

    [Fact]
    public void UnmatchedQuote_IsDenied()
    {
        Assert.False(RunCommandPolicy.TrySplitArguments("\"unterminated", out _));
    }
}
