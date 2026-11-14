using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class ThemeTests
{
    public static TheoryData<string, Color, Color> TextPairs => new()
    {
        { "primary/background", Theme.TextPrimary, Theme.Background },
        { "primary/surface", Theme.TextPrimary, Theme.Surface },
        { "secondary/surface", Theme.TextSecondary, Theme.Surface },
        { "secondary/alt", Theme.TextSecondary, Theme.SurfaceAlt },
        { "muted/surface", Theme.TextMuted, Theme.Surface },
        { "muted/background", Theme.TextMuted, Theme.Background },
        { "muted/alt", Theme.TextMuted, Theme.SurfaceAlt },
        { "disabled/surface", Theme.TextDisabled, Theme.Surface },
        { "on-accent/accent", Theme.OnAccent, Theme.Accent },
        { "on-accent/danger", Theme.OnAccent, Theme.DangerBackground },
        { "online/surface", Theme.Online, Theme.Surface },
        { "waiting/surface", Theme.Waiting, Theme.Surface },
        { "danger/surface", Theme.Danger, Theme.Surface },
        { "offline/surface", Theme.Offline, Theme.Surface },
    };

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void TextContrast_IsAtLeast4Point5(string name, Color fore, Color back)
    {
        double ratio = Theme.ContrastRatio(fore, back);
        Assert.True(ratio >= 4.5, $"{name} contrast {ratio:0.00} < 4.5");
    }

    [Fact]
    public void ContrastRatio_KnownValues()
    {
        Assert.Equal(21, Theme.ContrastRatio(Color.Black, Color.White), 2);
        Assert.Equal(1, Theme.ContrastRatio(Color.White, Color.White), 2);
    }

    [Fact]
    public void Mix_ClampsAndInterpolates()
    {
        Assert.Equal(Color.Black, Theme.Mix(Color.Black, Color.White, -1));
        Assert.Equal(Color.White, Theme.Mix(Color.Black, Color.White, 2));
        var mid = Theme.Mix(Color.Black, Color.White, 0.5);
        Assert.Equal(128, mid.R);
    }

    [Theory]
    [InlineData(1.0, 340, 240)]
    [InlineData(1.25, 425, 300)]
    [InlineData(1.5, 510, 360)]
    public void ScaleSize_MatchesDpiScale(float scale, int w, int h)
    {
        var scaled = Theme.ScaleSize(new Size(340, 240), scale);
        Assert.Equal(new Size(w, h), scaled);
    }

    [Fact]
    public void SharedFonts_AreSegoeUiAndCached()
    {
        Assert.Equal("Segoe UI", Theme.FontBody.FontFamily.Name);
        Assert.Same(Theme.FontBody, Theme.FontBody);
    }
}
