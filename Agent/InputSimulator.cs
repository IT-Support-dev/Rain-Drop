using System.Runtime.InteropServices;
using RainDrop.Shared;
using static RainDrop.Agent.NativeMethods;

namespace RainDrop.Agent;

/// <summary>จำลองเมาส์/คีย์บอร์ดด้วย SendInput</summary>
internal static class InputSimulator
{
    public static void Mouse(MouseCmd m)
    {
        // พิกัด 0..65535 ครอบคลุมทุกจอเมื่อใช้ MOUSEEVENTF_VIRTUALDESK
        // ตรงกับภาพที่ส่งไปซึ่งจับจาก virtual screen ทั้งหมด
        int dx = (int)Math.Round(Math.Clamp(m.X, 0, 1) * 65535);
        int dy = (int)Math.Round(Math.Clamp(m.Y, 0, 1) * 65535);

        uint flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint data = 0;

        switch (m.Action)
        {
            case MouseAction.Down:
                flags |= m.Button switch { 1 => MOUSEEVENTF_RIGHTDOWN, 2 => MOUSEEVENTF_MIDDLEDOWN, _ => MOUSEEVENTF_LEFTDOWN };
                break;
            case MouseAction.Up:
                flags |= m.Button switch { 1 => MOUSEEVENTF_RIGHTUP, 2 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP };
                break;
            case MouseAction.Wheel:
                flags |= MOUSEEVENTF_WHEEL;
                data = unchecked((uint)m.Delta);
                break;
        }

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
        });
    }

    public static void Key(KeyCmd k)
    {
        uint flags = k.Down ? 0 : KEYEVENTF_KEYUP;
        if (IsExtendedKey(k.VirtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)k.VirtualKey,
                    wScan = (ushort)MapVirtualKey((uint)k.VirtualKey, 0),
                    dwFlags = flags,
                },
            },
        });
    }

    // ปุ่มลูกศร, Home/End, PgUp/PgDn, Insert/Delete, Win, Numpad '/', NumLock, Ctrl/Alt ขวา
    private static bool IsExtendedKey(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0xA3 or 0xA5;

    private static void Send(INPUT input) =>
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}
