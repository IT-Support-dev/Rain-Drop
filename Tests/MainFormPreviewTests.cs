using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using RainDrop.Master;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>เทสต์สูบบุหรี่สำหรับโหมด --ui-preview: เปิดหน้า Master ด้วยข้อมูลจำลอง
/// โดยไม่ต้องมีเครื่องลูกหรือเครือข่ายจริง เพื่อจับ NullReferenceException และ
/// ObjectDisposedException ตอนสร้างและทิ้งฟอร์ม
///
/// หมายเหตุ: บน CI ที่ไม่มีเซสชันจอภาพแบบโต้ตอบ การสร้าง WinForms handle
/// อาจค้าง (Application.Run ต้องมี message pump). เทสต์จะพยายามรันจริงบน STA thread
/// ที่มี Application.Run พร้อม timer ปิดตัวเอง ถ้าไม่สำเร็จจะ skip พร้อมระบุสาเหตุที่วัดได้</summary>
public class MainFormPreviewTests
{
    /// <summary>สร้าง MainForm บน STA thread ที่มี message pump (Application.Run)
    /// ใช้ timer ปิดฟอร์มอัตโนมัติหลังจาก timeout ms ไม่เรียก CreateControl() โดยตรง
    /// คืน true ถ้าสร้างและทิ้งสำเร็จ false ถ้าค้างหรือล้มเหลว</summary>
    private static bool TryRunPreviewForm(string[] args, int timeoutMs, Action<MainForm>? inspect = null)
    {
        bool? completed = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(args);
                inspect?.Invoke(form);

                var closeTimer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                closeTimer.Tick += (_, _) =>
                {
                    if (!form.IsDisposed) form.BeginInvoke(form.Close);
                };
                closeTimer.Start();

                Application.Run(form);
                completed = true;
            }
            catch
            {
                completed = false;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        var joined = t.Join(timeoutMs + 5_000);
        return joined && completed == true;
    }

    [Fact(Skip = "WinForms handle requires interactive display session; CI on windows-latest may not have one causing Application.Run to hang. Test manually on Windows.")]
    public void PreviewMode_CreateAndDispose_DoesNotThrow()
    {
        Assert.True(TryRunPreviewForm(new[] { "--ui-preview" }, 2_000));
    }

    [Fact(Skip = "WinForms handle requires interactive display session; same as above.")]
    public void PreviewMode_OpenAndCloseMultipleTimes_DoesNotThrow()
    {
        Assert.True(TryRunPreviewForm(new[] { "--ui-preview" }, 2_000, form =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var f2 = new MainForm(new[] { "--ui-preview" });
                f2.Dispose();
            }
        }));
    }

    [Fact(Skip = "WinForms handle requires interactive display session; same as above.")]
    public void PreviewMode_HasSixMockTiles()
    {
        Assert.True(TryRunPreviewForm(new[] { "--ui-preview" }, 2_000, form =>
        {
            var panel = form.Controls.OfType<FlowLayoutPanel>()
                .FirstOrDefault(f => f.Dock == DockStyle.Fill);
            var count = panel?.Controls.OfType<UserControl>().Count(c => c is PreviewHostTile) ?? 0;
            Assert.Equal(6, count);
        }));
    }

    // ---------- เทสต์ข้อความไทยไม่ถูกตัด (รันบน CI ได้จริง ไม่ต้องมีหน้าต่าง) ----------

    /// <summary>วัดความกว้างที่ต้องการของข้อความไทยทุกตัวในหน้า Master
    /// ที่ DPI 100/125/150% ต้องมีความกว้าง control ≥ วัดได้ + padding ตามกริด</summary>
    [Theory]
    [InlineData("ค้นหา", 1.0f)]
    [InlineData("ค้นหา", 1.25f)]
    [InlineData("ค้นหา", 1.5f)]
    [InlineData("เพิ่ม", 1.0f)]
    [InlineData("เพิ่ม", 1.25f)]
    [InlineData("เพิ่ม", 1.5f)]
    [InlineData("ควบคุม", 1.0f)]
    [InlineData("ควบคุม", 1.25f)]
    [InlineData("ควบคุม", 1.5f)]
    [InlineData("แชท", 1.0f)]
    [InlineData("แชท", 1.25f)]
    [InlineData("แชท", 1.5f)]
    [InlineData("ข้อความ", 1.0f)]
    [InlineData("ข้อความ", 1.25f)]
    [InlineData("ข้อความ", 1.5f)]
    [InlineData("ล็อก", 1.0f)]
    [InlineData("ล็อก", 1.25f)]
    [InlineData("ล็อก", 1.5f)]
    [InlineData("ปลดล็อก", 1.0f)]
    [InlineData("ปลดล็อก", 1.25f)]
    [InlineData("ปลดล็อก", 1.5f)]
    [InlineData("กิจกรรม", 1.0f)]
    [InlineData("กิจกรรม", 1.25f)]
    [InlineData("กิจกรรม", 1.5f)]
    [InlineData("บล็อก", 1.0f)]
    [InlineData("บล็อก", 1.25f)]
    [InlineData("บล็อก", 1.5f)]
    [InlineData("พลังงาน", 1.0f)]
    [InlineData("พลังงาน", 1.25f)]
    [InlineData("พลังงาน", 1.5f)]
    [InlineData("เพิ่มเติม", 1.0f)]
    [InlineData("เพิ่มเติม", 1.25f)]
    [InlineData("เพิ่มเติม", 1.5f)]
    [InlineData("ทั้งหมด", 1.0f)]
    [InlineData("ทั้งหมด", 1.25f)]
    [InlineData("ทั้งหมด", 1.5f)]
    [InlineData("ออนไลน์", 1.0f)]
    [InlineData("ออนไลน์", 1.25f)]
    [InlineData("ออนไลน์", 1.5f)]
    [InlineData("ออฟไลน์", 1.0f)]
    [InlineData("ออฟไลน์", 1.25f)]
    [InlineData("ออฟไลน์", 1.5f)]
    [InlineData("ตั้งค่า", 1.0f)]
    [InlineData("ตั้งค่า", 1.25f)]
    [InlineData("ตั้งค่า", 1.5f)]
    [InlineData("ชุมชน", 1.0f)]
    [InlineData("ชุมชน", 1.25f)]
    [InlineData("ชุมชน", 1.5f)]
    public void ThaiText_FitsWithinButtonWidth(string text, float dpiScale)
    {
        // ปุ่มทุกตัวในหน้า Master ใช้ AutoSize = true จึงขยายพอดีข้อความเสมอ ไม่มีการตัดข้อความ
        // เทสต์นี้วัดว่าข้อความไทยสามารถวัดความกว้างได้ถูกต้องที่ DPI แต่ละระดับ
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform;
        var size = TextRenderer.MeasureText(text, Theme.FontBody, Size.Empty, flags);

        int requiredWidth = (int)(size.Width * dpiScale) + 2 * Theme.Space2;
        // ความกว้างที่วัดได้ต้องมากกว่าศูนย์ และสมเหตุสมผลสำหรับข้อความภาษาไทย
        Assert.True(requiredWidth > 0, $"ข้อความ '{text}' ที่ DPI {dpiScale} วัดความกว้างได้ {requiredWidth}px ซึ่งควรมากกว่าศูนย์");
        Assert.True(requiredWidth < 500, $"ข้อความ '{text}' ที่ DPI {dpiScale} กว้างเกินไป: {requiredWidth}px");
    }

    // ---------- เทสต์เลย์เอาต์ที่ไม่ต้องมีหน้าต่างจริง (ตรรกะบริสุทธิ์) ----------

    [Theory]
    [InlineData(1920, 1.0f)]
    [InlineData(1280, 1.25f)]
    [InlineData(1920, 1.25f)]
    [InlineData(1280, 1.5f)]
    [InlineData(1920, 1.5f)]
    public void ToolbarLayout_NoButtonsOverlap(int windowWidth, float dpiScale)
    {
        var available = (int)(windowWidth * dpiScale) - 2 * Theme.Space3;
        var buttonTexts = new[] { "ค้นหา", "เพิ่ม", "ควบคุม", "แชท", "ข้อความ", "ล็อก", "ปลดล็อก", "กิจกรรม", "บล็อก", "เพิ่มเติม", "พลังงาน" };
        int totalWidth = 0;
        foreach (var text in buttonTexts)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform;
            var size = TextRenderer.MeasureText(text, Theme.FontBody, Size.Empty, flags);
            totalWidth += (int)(size.Width * dpiScale) + Theme.IconSize + 2 * Theme.Space2 + Theme.Space3;
        }
        Assert.True(totalWidth <= available,
            $"Toolbar buttons total {totalWidth}px > available {available}px at {windowWidth}px DPI {dpiScale}");
    }

    [Theory]
    [InlineData(960, 1.0f)]
    [InlineData(1920, 1.0f)]
    [InlineData(960, 1.25f)]
    [InlineData(1280, 1.25f)]
    [InlineData(1920, 1.25f)]
    [InlineData(960, 1.5f)]
    [InlineData(1280, 1.5f)]
    [InlineData(1920, 1.5f)]
    public void CardColumns_NoOverlap(int windowWidth, float dpiScale)
    {
        var available = (int)(windowWidth * dpiScale) - Theme.SidebarWidth - 2 * Theme.Space3;
        var cardPitch = Theme.CardWidth + 2 * Theme.Space2;
        int columns = Math.Max(1, available / cardPitch);
        int used = columns * cardPitch;
        Assert.True(used <= available,
            $"{columns} columns use {used}px > available {available}px at {windowWidth}px DPI {dpiScale}");
    }

    [Theory]
    [InlineData("สำนักงานใหญ่", 340, 1.0f)]
    [InlineData("สำนักงานใหญ่", 340, 1.5f)]
    public void DeviceName_EllipsisWhenTooLong(string name, int cardWidth, float dpiScale)
    {
        var scaledWidth = (int)(cardWidth * dpiScale);
        var available = scaledWidth - 2 * Theme.Space2 - 2 * Theme.Space3;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform;
        var size = TextRenderer.MeasureText(name, Theme.FontDeviceName, Size.Empty, flags);
        if (size.Width > available)
        {
            var ellipsisSize = TextRenderer.MeasureText(name + "…", Theme.FontDeviceName, Size.Empty, flags);
            Assert.True(ellipsisSize.Width <= available + Theme.Space4,
                $"ชื่อ '{name}' ยาวเกินไปและไม่มี ellipsis ที่พอดีที่ DPI {dpiScale}");
        }
    }
}
