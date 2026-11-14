using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class UiHelpersTests
{
    [Theory]
    [InlineData("", 0, true, "PC-01", "10.0.0.1", true)]
    [InlineData("pc", 0, false, "PC-01", "10.0.0.1", true)]
    [InlineData("10.0.0", 0, true, "PC-01", "10.0.0.1", true)]
    [InlineData("missing", 0, true, "PC-01", "10.0.0.1", false)]
    [InlineData("", 1, false, "PC-01", "10.0.0.1", false)]
    [InlineData("", 2, true, "PC-01", "10.0.0.1", false)]
    public void Filter_CombinesQueryAndStatus(string query, int status, bool online, string name, string address, bool expected) =>
        Assert.Equal(expected, UiFilter.Matches(query, (MachineStatusFilter)status, online, name, address));

    [Fact]
    public void CountText_ShowsVisibleAndTotal()
    {
        Assert.Equal("5 เครื่อง", UiFilter.CountText(5, 5));
        Assert.Equal("2/5 เครื่อง", UiFilter.CountText(2, 5));
    }

    [Fact]
    public void Clamp_KeepsWindowOnScreenAndMinimumSize()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var min = new Size(800, 600);
        var result = UiWindowPlacement.Clamp(new Rectangle(2500, -50, 400, 300), screen, min);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
        Assert.True(result.Right <= screen.Right);
        Assert.True(result.Top >= screen.Top);
    }
}
