using System.Reflection;

namespace RainDrop.Shared;

public static class AppIcon
{
    private static Icon? _cached;

    /// <summary>
    /// โหลดไอคอนหยดน้ำที่ฝังไว้ในโปรแกรม (resource ชื่อลงท้าย raindrop.ico)
    /// ถ้าไม่พบจะคืนไอคอนของโปรแกรมเองแทน เพื่อไม่ให้แอปล้ม
    /// </summary>
    public static Icon Load()
    {
        if (_cached is not null) return _cached;

        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("raindrop.ico", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is not null)
            {
                _cached = new Icon(stream);
                return _cached;
            }
        }

        _cached = SystemIcons.Application;
        return _cached;
    }

    /// <summary>ไอคอนขนาดเล็กสำหรับถาดระบบ (tray) ให้คมชัดที่ 16x16</summary>
    public static Icon LoadTray() => new(Load(), SystemInformation.SmallIconSize);
}
