using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RainDrop.Agent;

internal static class ScreenCapture
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>จับภาพทุกจอ (virtual screen) ย่อให้กว้างไม่เกิน maxWidth แล้วบีบอัดเป็น JPEG</summary>
    public static byte[] CaptureJpeg(int maxWidth, int quality)
    {
        var vs = SystemInformation.VirtualScreen;
        using var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb);

        using (var g = Graphics.FromImage(full))
        {
            try
            {
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g, vs);
            }
            catch
            {
                // เกิดได้ตอนหน้าจอ UAC / Ctrl+Alt+Del / Lock screen (secure desktop)
                g.Clear(Color.Black);
                using var font = new Font("Tahoma", 28);
                g.DrawString("Secure desktop / หน้าจอถูกล็อกโดย Windows", font, Brushes.White, 40, 40);
            }
        }

        Bitmap output = full;
        Bitmap? scaled = null;
        if (maxWidth > 0 && full.Width > maxWidth)
        {
            int height = (int)((long)full.Height * maxWidth / full.Width);
            scaled = new Bitmap(maxWidth, height, PixelFormat.Format24bppRgb);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g2.DrawImage(full, 0, 0, maxWidth, height);
            output = scaled;
        }

        try
        {
            using var ms = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                (long)Math.Clamp(quality, 10, 95));
            output.Save(ms, JpegCodec, parameters);
            return ms.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>CopyFromScreen ไม่ติดเคอร์เซอร์มาด้วย จึงต้องวาดเพิ่มเอง เพื่อให้ Master เห็นตำแหน่งเมาส์</summary>
    private static void DrawCursor(Graphics g, Rectangle vs)
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0)
            return;

        IntPtr hdc = g.GetHdc();
        try
        {
            NativeMethods.DrawIcon(hdc, info.ptScreenPos.X - vs.Left, info.ptScreenPos.Y - vs.Top, info.hCursor);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }
}
