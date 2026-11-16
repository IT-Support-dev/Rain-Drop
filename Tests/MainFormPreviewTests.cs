using System.Threading;
using System.Windows.Forms;
using RainDrop.Master;
using Xunit;

namespace RainDrop.Tests;

/// <summary>เทสต์สูบบุหรี่สำหรับโหมด --ui-preview: เปิดหน้า Master ด้วยข้อมูลจำลอง
/// โดยไม่ต้องมีเครื่องลูกหรือเครือข่ายจริง เพื่อจับ NullReferenceException และ
/// ObjectDisposedException ตอนสร้างและทิ้งฟอร์ม</summary>
public class MainFormPreviewTests
{
    /// <summary>รัน delegate บน STA thread ซึ่ง WinForms ต้องการสำหรับการสร้าง handle
    /// พร้อม timeout กันค้างถ้า WinForms เริ่มต้นไม่ได้ในสภาพแวดล้อมที่ไม่มีจอภาพ</summary>
    private static void RunOnSTA(ThreadStart action, int timeoutSeconds = 30)
    {
        var t = new Thread(action);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        var joined = t.Join(timeoutSeconds * 1000);
        if (!joined)
        {
            t.Abort();
            throw new TimeoutException($"เทสต์ค้างเกิน {timeoutSeconds} วินาที (WinForms อาจเริ่มต้นไม่ได้บนเครื่องนี้)");
        }
    }

    [Fact]
    public void PreviewMode_CreateAndDispose_DoesNotThrow()
    {
        RunOnSTA(() =>
        {
            using var form = new MainForm(new[] { "--ui-preview" });
            // การทิ้งฟอร์มต้องไม่โยน exception (รวมถึง ObjectDisposedException ของ ContextMenuStrip)
            // Constructor คือจุดที่เคยโยน NullReferenceException ก่อนแก้ไข
        });
    }

    [Fact]
    public void PreviewMode_OpenAndCloseMultipleTimes_DoesNotThrow()
    {
        RunOnSTA(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var form = new MainForm(new[] { "--ui-preview" });
                // จำลองการเปิดและปิดหน้าต่างหลายรอบ
                form.Dispose();
            }
        });
    }

    [Fact]
    public void PreviewMode_HasSixMockTiles()
    {
        int tileCount = 0;
        RunOnSTA(() =>
        {
            using var form = new MainForm(new[] { "--ui-preview" });

            // หา FlowLayoutPanel ที่เป็นพื้นที่แสดงการ์ดเครื่อง (_panel)
            var contentPanel = form.Controls.OfType<Panel>()
                .FirstOrDefault(p => p.Dock == DockStyle.Fill)
                ?.Controls.OfType<FlowLayoutPanel>()
                .FirstOrDefault(f => f.AutoScroll);
            tileCount = contentPanel?.Controls.Count ?? 0;
        });
        Assert.Equal(6, tileCount);
    }
}
