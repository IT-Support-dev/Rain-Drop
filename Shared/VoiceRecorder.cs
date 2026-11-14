using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RainDrop.Shared;

/// <summary>
/// อัดเสียงจากไมโครโฟนเป็นไฟล์ WAV ด้วย MCI (winmm.dll) ที่มีอยู่แล้วใน Windows
/// จึงไม่ต้องพึ่งไลบรารีภายนอก ต้องเรียกจาก UI thread เดียวกันตลอด
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    private const string Alias = "raindrop_rec";
    private readonly Stopwatch _stopwatch = new();

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Start()
    {
        if (IsRecording) return;

        Mci($"close {Alias}", throwOnError: false); // เผื่อค้างจากครั้งก่อน
        Mci($"open new type waveaudio alias {Alias}");
        // 16 kHz / 16-bit / mono: เสียงพูดชัดและไฟล์เล็ก (ถ้าไดรเวอร์ไม่รองรับจะใช้ค่าเริ่มต้นของ Windows แทน)
        Mci($"set {Alias} time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2",
            throwOnError: false);
        Mci($"record {Alias}");

        IsRecording = true;
        _stopwatch.Restart();
    }

    /// <summary>หยุดอัดและคืนค่าไฟล์ WAV ที่ได้</summary>
    public (byte[] Wav, double Seconds) Stop()
    {
        if (!IsRecording) throw new InvalidOperationException("ยังไม่ได้เริ่มอัดเสียง");

        _stopwatch.Stop();
        string path = Path.Combine(Path.GetTempPath(), $"raindrop_{Guid.NewGuid():N}.wav");
        try
        {
            Mci($"stop {Alias}");
            Mci($"save {Alias} \"{path}\"");
            return (File.ReadAllBytes(path), _stopwatch.Elapsed.TotalSeconds);
        }
        finally
        {
            Mci($"close {Alias}", throwOnError: false);
            IsRecording = false;
            try { File.Delete(path); } catch { /* ไม่เป็นไร */ }
        }
    }

    public void Cancel()
    {
        if (!IsRecording) return;
        Mci($"close {Alias}", throwOnError: false);
        IsRecording = false;
        _stopwatch.Stop();
    }

    public void Dispose() => Cancel();

    private static void Mci(string command, bool throwOnError = true)
    {
        int error = mciSendString(command, null, 0, IntPtr.Zero);
        if (error != 0 && throwOnError)
        {
            var message = new StringBuilder(256);
            mciGetErrorString(error, message, message.Capacity);
            throw new InvalidOperationException($"อัดเสียงไม่ได้: {message} (ตรวจว่ามีไมโครโฟนและอนุญาตให้แอปใช้ไมโครโฟนแล้ว)");
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int error, StringBuilder buffer, int length);
}
