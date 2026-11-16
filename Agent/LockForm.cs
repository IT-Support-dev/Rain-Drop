using System.Runtime.InteropServices;
using RainDrop.Shared;
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
    private readonly Label _clock;
    private readonly PictureBox _logo;
    private readonly System.Windows.Forms.Timer _keepOnTop;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;
    private bool _allowClose;

    public LockForm(string text)
    {
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        Font = Theme.FontBody;

        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Background,
            Padding = new Padding(Theme.Space6),
        };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _logo = new PictureBox
        {
            Image = AppIcon.Load().ToBitmap(),
            Size = new Size(Theme.Space6 * 2, Theme.Space6 * 2),
            SizeMode = PictureBoxSizeMode.Zoom,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, Theme.Space5),
        };
        _clock = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontClock,
            Text = DateTime.Now.ToString("HH:mm"),
            Margin = new Padding(0, 0, 0, Theme.Space3),
        };
        _label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Theme.TextSecondary,
            Font = Theme.FontLockMessage,
            Text = text,
            MaximumSize = new Size(SystemInformation.VirtualScreen.Width - Theme.Space6 * 2, 0),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        center.Controls.Add(_logo, 0, 1);
        center.Controls.Add(_clock, 0, 2);
        center.Controls.Add(_label, 0, 3);
        Controls.Add(center);

        _keepOnTop = new System.Windows.Forms.Timer { Interval = 500 };
        _keepOnTop.Tick += (_, _) =>
        {
            TopMost = true;
            Activate();
        };
        _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _clockTimer.Tick += (_, _) => _clock.Text = DateTime.Now.ToString("HH:mm");
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
        _clockTimer.Start();
        Activate();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
        _clockTimer.Stop();
        _clockTimer.Dispose();
        _logo.Image?.Dispose();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }
}
