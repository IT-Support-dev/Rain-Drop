using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class ThemeTests
{
    public static TheoryData<string, Color, Color> TextPairs => new()
    {
        // Light theme: TextPrimary #000000 on all light surfaces passes easily
        { "primary/background", Theme.TextPrimary, Theme.Background },
        { "primary/surface", Theme.TextPrimary, Theme.Surface },
        { "primary/surface-alt", Theme.TextPrimary, Theme.SurfaceAlt },
        // TextSecondary #4B5563 on white and surface
        { "secondary/background", Theme.TextSecondary, Theme.Background },
        { "secondary/surface", Theme.TextSecondary, Theme.Surface },
        { "secondary/surface-alt", Theme.TextSecondary, Theme.SurfaceAlt },
        // TextMuted (darkened for light theme) on surfaces
        { "muted/surface", Theme.TextMuted, Theme.Surface },
        { "muted/background", Theme.TextMuted, Theme.Background },
        // OnAccent (white) on Accent (black)
        { "on-accent/accent", Theme.OnAccent, Theme.Accent },
        { "on-accent/accent-hover", Theme.OnAccent, Theme.AccentHover },
        { "on-accent/accent-pressed", Theme.OnAccent, Theme.AccentPressed },
        // Status dot colors on Surface (dots are decorative, not text)
        { "online/surface", Theme.Online, Theme.Surface },
        { "waiting/surface", Theme.Waiting, Theme.Surface },
        { "danger/surface", Theme.Danger, Theme.Surface },
        { "offline/surface", Theme.Offline, Theme.Surface },
        // Link (black) on surfaces
        { "link/background", Theme.Link, Theme.Background },
        { "link/surface", Theme.Link, Theme.Surface },
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
        Assert.Equal(Color.Black.ToArgb(), Theme.Mix(Color.Black, Color.White, -1).ToArgb());
        Assert.Equal(Color.White.ToArgb(), Theme.Mix(Color.Black, Color.White, 2).ToArgb());
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
        Assert.Equal("Segoe UI Light", Theme.FontHeading.FontFamily.Name);
        Assert.Same(Theme.FontBody, Theme.FontBody);
    }

    [Fact]
    public void LightTheme_Colors_AreLight()
    {
        int bgSum = Theme.Background.R + Theme.Background.G + Theme.Background.B;
        Assert.True(bgSum > 700, $"Background ไม่สว่างพอ: RGB=({Theme.Background.R},{Theme.Background.G},{Theme.Background.B})");

        int tpSum = Theme.TextPrimary.R + Theme.TextPrimary.G + Theme.TextPrimary.B;
        Assert.True(tpSum < 100, $"TextPrimary ไม่มืดพอ: RGB=({Theme.TextPrimary.R},{Theme.TextPrimary.G},{Theme.TextPrimary.B})");

        Assert.Equal(Color.FromArgb(245, 241, 232).ToArgb(), Theme.ZoneSidebar.ToArgb());
    }
}
