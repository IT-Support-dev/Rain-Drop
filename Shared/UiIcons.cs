using System.Collections.Concurrent;
using System.Drawing.Drawing2D;

namespace RainDrop.Shared;

internal enum UiIcon
{
    None, Search, Plus, Monitor, Lock, Unlock, Activity, Block, Chat, Message, Power, More,
    Settings, Refresh, Computer, User, Send, Attach, Mic, Stop, Play, Close, Check, Warning, Info, Bell, Folder
}

/// <summary>One stroke family, cached bitmaps. Icons are generated once per icon/color/size.</summary>
internal static class UiIcons
{
    private static readonly ConcurrentDictionary<(UiIcon, int, int), Bitmap> Cache = new();

    internal static Bitmap Get(UiIcon icon, Color color, int size = Theme.IconSize) =>
        Cache.GetOrAdd((icon, color.ToArgb(), size), _ => Render(icon, color, size));

    private static Bitmap Render(UiIcon icon, Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var pen = new Pen(color, Math.Max(1.4F, size / 12F)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float s = size / 16F;
        PointF P(float x, float y) => new(x * s, y * s);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Rect(float x, float y, float w, float h) => g.DrawRectangle(pen, x * s, y * s, w * s, h * s);
        void Circle(float x, float y, float d) => g.DrawEllipse(pen, x * s, y * s, d * s, d * s);
        void Poly(params PointF[] pts) => g.DrawLines(pen, pts);
        switch (icon)
        {
            case UiIcon.Search: Circle(2, 2, 8); Line(8, 8, 14, 14); break;
            case UiIcon.Plus: Line(8, 2, 8, 14); Line(2, 8, 14, 8); break;
            case UiIcon.Monitor: Rect(2, 3, 12, 8); Line(6, 14, 10, 14); Line(8, 11, 8, 14); break;
            case UiIcon.Lock: Rect(3, 7, 10, 7); g.DrawArc(pen, 5 * s, 2 * s, 6 * s, 6 * s, 180, 180); break;
            case UiIcon.Unlock: Rect(3, 7, 10, 7); g.DrawArc(pen, 5 * s, 2 * s, 6 * s, 6 * s, 180, 120); break;
            case UiIcon.Activity: Poly(P(1, 9), P(5, 9), P(7, 3), P(9, 13), P(11, 9), P(15, 9)); break;
            case UiIcon.Block: Circle(2, 2, 12); Line(4, 4, 12, 12); break;
            case UiIcon.Chat: Poly(P(2, 3), P(14, 3), P(14, 11), P(7, 11), P(4, 14), P(4, 11), P(2, 11), P(2, 3)); break;
            case UiIcon.Message: Rect(2, 4, 12, 9); Poly(P(2, 5), P(8, 9), P(14, 5)); break;
            case UiIcon.Power: g.DrawArc(pen, 3 * s, 3 * s, 10 * s, 10 * s, -60, 300); Line(8, 1, 8, 8); break;
            case UiIcon.More: Circle(2, 7, 1.5F); Circle(7, 7, 1.5F); Circle(12, 7, 1.5F); break;
            case UiIcon.Settings: Circle(5, 5, 6); Circle(7, 7, 2); Line(8, 1, 8, 3); Line(8, 13, 8, 15); Line(1, 8, 3, 8); Line(13, 8, 15, 8); break;
            case UiIcon.Refresh: g.DrawArc(pen, 3 * s, 3 * s, 10 * s, 10 * s, 30, 270); Poly(P(12, 2), P(13, 6), P(9, 6)); break;
            case UiIcon.Computer: Rect(2, 3, 12, 8); Line(6, 14, 10, 14); Line(8, 11, 8, 14); break;
            case UiIcon.User: Circle(5, 2, 6); g.DrawArc(pen, 2 * s, 9 * s, 12 * s, 8 * s, 180, 180); break;
            case UiIcon.Send: Poly(P(2, 8), P(14, 2), P(10, 14), P(8, 9), P(2, 8)); Line(8, 9, 14, 2); break;
            case UiIcon.Attach: g.DrawArc(pen, 4 * s, 3 * s, 8 * s, 10 * s, 90, 270); Line(12, 8, 12, 12); break;
            case UiIcon.Mic: Rect(6, 2, 4, 8); g.DrawArc(pen, 3 * s, 7 * s, 10 * s, 7 * s, 0, 180); Line(8, 14, 8, 16); break;
            case UiIcon.Stop: Rect(4, 4, 8, 8); break;
            case UiIcon.Play: Poly(P(5, 3), P(12, 8), P(5, 13), P(5, 3)); break;
            case UiIcon.Close: Line(3, 3, 13, 13); Line(13, 3, 3, 13); break;
            case UiIcon.Check: Poly(P(2, 8), P(6, 12), P(14, 3)); break;
            case UiIcon.Warning: Poly(P(8, 2), P(15, 14), P(1, 14), P(8, 2)); Line(8, 6, 8, 10); Line(8, 12, 8, 12.5F); break;
            case UiIcon.Info: Circle(2, 2, 12); Line(8, 7, 8, 12); Line(8, 4, 8, 4.5F); break;
            case UiIcon.Bell: g.DrawArc(pen, 4 * s, 2 * s, 8 * s, 9 * s, 180, 180); Line(4, 11, 12, 11); Line(6, 14, 10, 14); break;
            case UiIcon.Folder: Poly(P(2, 4), P(6, 4), P(8, 6), P(14, 6), P(14, 13), P(2, 13), P(2, 4)); break;
        }
        return bmp;
    }
}
