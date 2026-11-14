using System.Runtime.InteropServices;
using static RainDrop.Agent.NativeMethods;

namespace RainDrop.Agent;

/// <summary>
/// หน้าต่างเต็มจอทับทุกจอ ใช้ล็อกเครื่อง
/// - ปิดเองไม่ได้ (Alt+F4 ถูกยกเลิก)
/// - ดักคีย์บอร์ดทั้งหมดด้วย low-level hook (ยกเว้นอินพุตที่ Agent ฉีดเอง)
/// - Ctrl+Alt+Del บล็อกไม่ได้ (Windows ออกแบบไว้เช่นนั้น)
/// </summary>
internal sealed class LockForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _keepOnTop;
    private LowLevelKeyboardProc? _hookProc; // เก็บอ้างอิงไว้กัน GC เก็บ delegate
    private IntPtr _hook;
    private bool _allowClose;

    public LockForm(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(18, 24, 38);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Tahoma", 32, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = text,
        };
        Controls.Add(_label);

        _keepOnTop = new System.Windows.Forms.Timer { Interval = 500 };
        _keepOnTop.Tick += (_, _) =>
        {
            TopMost = true;
            Activate();
        };
    }

    public void SetText(string text) => _label.Text = text;

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        _keepOnTop.Start();
        Activate();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected) return (IntPtr)1; // กลืนปุ่มที่ผู้ใช้กดจริงทั้งหมด
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // ปล่อยให้ปิดได้เฉพาะเมื่อ Master สั่งปลดล็อก หรือ Windows กำลังปิดเครื่อง
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _keepOnTop.Stop();
        _keepOnTop.Dispose();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }
}
