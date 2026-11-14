using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RainDrop.AgentService;

/// <summary>
/// เปิดโปรแกรมในเซสชันของผู้ใช้ที่ล็อกอินอยู่จริง (interactive) จาก Service ที่รันเป็น SYSTEM
/// จำเป็นเพราะ Service อยู่ใน session 0 ซึ่งมองไม่เห็นหน้าจอผู้ใช้
/// ใช้ WTSQueryUserToken เพื่อยืมสิทธิ์ของผู้ใช้ แล้ว CreateProcessAsUser เปิดโปรแกรมในเซสชันนั้น
/// </summary>
internal static class SessionLauncher
{
    public const uint InvalidSession = 0xFFFFFFFF;

    /// <summary>หมายเลขเซสชันของผู้ใช้ที่นั่งอยู่หน้าเครื่อง (console) ตอนนี้</summary>
    public static uint ActiveConsoleSession => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// SID ของผู้ใช้ที่ล็อกอินในเซสชันนี้ ใช้ตั้ง ACL ของ named pipe ให้เฉพาะคนนั้นต่อได้
    /// คืน null พร้อม error เมื่อไม่มีผู้ใช้ล็อกอินหรืออ่านสิทธิ์ไม่ได้ (ผู้เรียกต้องบันทึกเหตุนี้)
    /// ใช้ WTSQueryUserToken ตัวเดียวกับที่เปิด Agent จึงไม่เพิ่มพื้นผิว API ใหม่
    /// </summary>
    public static SecurityIdentifier? SessionUserSid(uint sessionId, out string? error)
    {
        error = null;
        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(userToken);
            if (identity.User is null)
            {
                error = "token ของผู้ใช้ไม่มีข้อมูล SID";
                return null;
            }
            return identity.User;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    /// <summary>
    /// เปิด exePath ในเซสชัน sessionId พร้อมตั้ง environment variable เพิ่ม (extraEnv)
    /// คืนค่า process id ถ้าสำเร็จ หรือโยน exception พร้อมสาเหตุ
    /// </summary>
    public static uint LaunchInSession(uint sessionId, string exePath, IDictionary<string, string> extraEnv)
    {
        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken ล้มเหลว (อาจยังไม่มีผู้ใช้ล็อกอิน)");

        IntPtr duplicated = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out duplicated))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx ล้มเหลว");

            // สร้าง environment ของผู้ใช้ แล้วแทรกตัวแปรเพิ่มเข้าไป
            if (!CreateEnvironmentBlock(out envBlock, duplicated, false))
                envBlock = IntPtr.Zero; // ถ้าไม่ได้ ก็ใช้ environment ว่าง
            string? envString = BuildEnvironment(envBlock, extraEnv);

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            string dir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            bool ok = CreateProcessAsUser(duplicated, exePath, $"\"{exePath}\"", ref sa, ref sa,
                false, flags, envString, dir, ref si, out PROCESS_INFORMATION pi);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser ล้มเหลว");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (duplicated != IntPtr.Zero) CloseHandle(duplicated);
            CloseHandle(userToken);
        }
    }

    /// <summary>รวม environment ของผู้ใช้ (envBlock) กับตัวแปรที่เพิ่ม ให้อยู่ในรูปสตริงคั่นด้วย \0 และปิดท้ายด้วย \0\0</summary>
    private static string BuildEnvironment(IntPtr envBlock, IDictionary<string, string> extra)
    {
        var entries = new List<string>();
        if (envBlock != IntPtr.Zero)
        {
            IntPtr ptr = envBlock;
            while (true)
            {
                string? entry = Marshal.PtrToStringUni(ptr);
                if (string.IsNullOrEmpty(entry)) break;
                entries.Add(entry);
                ptr += (entry.Length + 1) * sizeof(char);
            }
        }
        foreach (var kv in extra)
            entries.Add($"{kv.Key}={kv.Value}");

        return string.Join('\0', entries) + "\0\0";
    }

    // ---------- P/Invoke ----------
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hToken, uint access, ref SECURITY_ATTRIBUTES attrs,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine,
        ref SECURITY_ATTRIBUTES procAttrs, ref SECURITY_ATTRIBUTES threadAttrs, bool inherit, uint flags,
        string? env, string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
