using System.Media;
using System.Runtime.InteropServices;

namespace RainDrop.Shared;

/// <summary>
/// หน้าต่างแชท ใช้ร่วมกันทั้งฝั่ง Master และ Agent
/// - ข้อความแสดงเป็น "บับเบิล" (ของเราเป็นสีฟ้า ชิดขวา, ของอีกฝั่งเป็นสีเขียว ชิดซ้าย)
/// - ปุ่ม "● อัดเสียง" กดครั้งแรกเริ่มอัด กดอีกครั้งเพื่อหยุดและส่ง (สูงสุด 2 นาที)
/// - ข้อความเสียงกด ▶ เพื่อฟัง, คลิกขวาเพื่อบันทึกเป็นไฟล์ .wav
/// - ปิดหน้าต่างแล้วจะแค่ซ่อน ประวัติการคุยจึงยังอยู่
/// </summary>
public sealed class ChatWindow : Form
{
    private const int MaxRecordSeconds = 120;

    private readonly FlowLayoutPanel _list;
    private readonly TextBox _input;
    private readonly Button _sendButton;
    private readonly Button _recordButton;
    private readonly Button _cancelRecordButton;
    private readonly Func<string, bool> _onSend;
    private readonly Func<VoiceMsg, bool>? _onSendVoice;
    private readonly string _myName;
    private readonly Font _boldFont;
    private readonly Font _italicFont;
    private readonly List<Label> _textLabels = new();

    private readonly VoiceRecorder _recorder = new();
    private readonly System.Windows.Forms.Timer _recordTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _playTimer = new();
    private SoundPlayer? _player;
    private Button? _playingButton;
    private bool _forceClose;

    private static readonly Color MyBubble = Color.FromArgb(214, 234, 255);
    private static readonly Color TheirBubble = Color.FromArgb(226, 245, 228);

    /// <param name="onSend">ส่งข้อความตัวอักษร: คืนค่า true ถ้าส่งออกไปได้</param>
    /// <param name="onSendVoice">ส่งข้อความเสียง: คืนค่า true ถ้าส่งออกไปได้ (null = ไม่แสดงปุ่มอัดเสียง)</param>
    public ChatWindow(string title, string myName, Func<string, bool> onSend, Func<VoiceMsg, bool>? onSendVoice = null)
    {
        _myName = myName;
        _onSend = onSend;
        _onSendVoice = onSendVoice;

        Text = title;
        Icon = AppIcon.Load();
        Size = new Size(460, 580);
        MinimumSize = new Size(360, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 10);
        _boldFont = new Font("Tahoma", 8.5f, FontStyle.Bold);
        _italicFont = new Font(Font, FontStyle.Italic);

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(4),
        };
        _list.Resize += (_, _) => ResizeBubbles();

        _input = new TextBox { Dock = DockStyle.Fill };
        _sendButton = new Button { Text = "ส่ง", Dock = DockStyle.Right, Width = 64 };
        _recordButton = new Button
        {
            Text = "● อัดเสียง",
            Dock = DockStyle.Left,
            Width = 96,
            Visible = onSendVoice is not null,
        };
        _cancelRecordButton = new Button { Text = "ยกเลิก", Dock = DockStyle.Right, Width = 64, Visible = false };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6, 5, 6, 5) };
        bottom.Controls.Add(_input);          // Fill: ต้องเพิ่มก่อน
        bottom.Controls.Add(_cancelRecordButton);
        bottom.Controls.Add(_sendButton);
        bottom.Controls.Add(_recordButton);

        Controls.Add(_list);
        Controls.Add(bottom);

        _sendButton.Click += (_, _) => SendCurrentText();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendCurrentText();
            }
        };
        _recordButton.Click += (_, _) => ToggleRecording();
        _cancelRecordButton.Click += (_, _) => FinishRecording(send: false);
        _recordTimer.Tick += (_, _) => OnRecordTick();
        _playTimer.Tick += (_, _) => StopPlayback();
    }

    // ===================== ข้อความตัวอักษร =====================

    private void SendCurrentText()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;

        if (_onSend(text))
        {
            Append(new ChatMsg(_myName, text, DateTime.Now), mine: true);
            _input.Clear();
        }
        else
        {
            AppendSystem("ส่งไม่สำเร็จ: ไม่ได้เชื่อมต่อกับอีกฝั่ง");
        }
        _input.Focus();
    }

    public void Append(ChatMsg msg, bool mine)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = msg.Text,
            UseMnemonic = false,
            MaximumSize = new Size(BubbleTextWidth, 0),
            Margin = Padding.Empty,
        };
        _textLabels.Add(label);
        AddBubble(mine ? "คุณ" : msg.Sender, msg.Time, mine, label);
    }

    public void AppendSystem(string text)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = Color.Gray,
            Font = _italicFont,
            UseMnemonic = false,
            MaximumSize = new Size(BubbleTextWidth, 0),
            Margin = new Padding(8, 4, 8, 4),
        };
        _textLabels.Add(label);
        _list.Controls.Add(label);
        _list.ScrollControlIntoView(label);
    }

    // ===================== ข้อความเสียง: แสดงผลและเล่น =====================

    public void AppendVoice(VoiceMsg msg, bool mine)
    {
        var button = new Button
        {
            Text = PlayCaption(msg.Seconds),
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
            Padding = new Padding(4, 2, 4, 2),
        };
        button.FlatAppearance.BorderColor = mine ? Color.SteelBlue : Color.SeaGreen;
        button.Click += (_, _) => TogglePlayback(button, msg);

        var menu = new ContextMenuStrip();
        menu.Items.Add("บันทึกเป็นไฟล์ .wav...", null, (_, _) => SaveVoice(msg));
        button.ContextMenuStrip = menu;

        AddBubble(mine ? "คุณ" : msg.Sender, msg.Time, mine, button);
    }

    private static string PlayCaption(double seconds) =>
        $"▶  ข้อความเสียง  {TimeSpan.FromSeconds(Math.Max(1, Math.Round(seconds))):m\\:ss}";

    private void TogglePlayback(Button button, VoiceMsg msg)
    {
        bool sameButton = _playingButton == button;
        StopPlayback();
        if (sameButton) return; // กดซ้ำปุ่มที่กำลังเล่น = หยุด

        try
        {
            _player = new SoundPlayer(new MemoryStream(msg.Wav));
            _player.Play(); // เล่นแบบ async ไม่บล็อก UI
        }
        catch (Exception ex)
        {
            StopPlayback();
            AppendSystem("เล่นเสียงไม่ได้: " + ex.Message);
            return;
        }

        _playingButton = button;
        button.Tag = button.Text;
        button.Text = "■  หยุด";
        // SoundPlayer ไม่มี event ตอนเล่นจบ จึงตั้งเวลาคืนปุ่มตามความยาวเสียง
        _playTimer.Interval = Math.Max(500, (int)(msg.Seconds * 1000) + 300);
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        if (_player is not null)
        {
            _player.Stop();
            _player.Stream?.Dispose();
            _player.Dispose();
            _player = null;
        }
        if (_playingButton is not null)
        {
            _playingButton.Text = (string)_playingButton.Tag!;
            _playingButton = null;
        }
    }

    private void SaveVoice(VoiceMsg msg)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "ไฟล์เสียง WAV (*.wav)|*.wav",
            FileName = $"voice_{msg.Time:yyyyMMdd_HHmmss}.wav",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllBytes(dialog.FileName, msg.Wav);
    }

    // ===================== ข้อความเสียง: อัดและส่ง =====================

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            FinishRecording(send: true);
            return;
        }

        StopPlayback();
        try
        {
            _recorder.Start();
        }
        catch (Exception ex)
        {
            AppendSystem(ex.Message);
            return;
        }

        _recordButton.BackColor = Color.IndianRed;
        _recordButton.ForeColor = Color.White;
        _recordButton.Width = 130;
        _cancelRecordButton.Visible = true;
        _sendButton.Visible = false;
        _input.Enabled = false;
        OnRecordTick();
        _recordTimer.Start();
    }

    private void OnRecordTick()
    {
        var elapsed = _recorder.Elapsed;
        _recordButton.Text = $"■ หยุดและส่ง {elapsed:m\\:ss}";
        if (elapsed.TotalSeconds >= MaxRecordSeconds)
            FinishRecording(send: true); // ครบเวลาสูงสุด: ส่งอัตโนมัติ
    }

    private void FinishRecording(bool send)
    {
        if (!_recorder.IsRecording) return;
        _recordTimer.Stop();

        try
        {
            if (!send)
            {
                _recorder.Cancel();
            }
            else
            {
                var (wav, seconds) = _recorder.Stop();
                var msg = new VoiceMsg(_myName, DateTime.Now, seconds, wav);
                if (seconds < 0.7)
                    AppendSystem("ข้อความเสียงสั้นเกินไป (ควรอัดอย่างน้อย 1 วินาที)");
                else if (_onSendVoice?.Invoke(msg) == true)
                    AppendVoice(msg, mine: true);
                else
                    AppendSystem("ส่งข้อความเสียงไม่สำเร็จ: ไม่ได้เชื่อมต่อกับอีกฝั่ง");
            }
        }
        catch (Exception ex)
        {
            _recorder.Cancel();
            AppendSystem(ex.Message);
        }
        finally
        {
            _recordButton.Text = "● อัดเสียง";
            _recordButton.BackColor = SystemColors.Control;
            _recordButton.ForeColor = SystemColors.ControlText;
            _recordButton.UseVisualStyleBackColor = true;
            _recordButton.Width = 96;
            _cancelRecordButton.Visible = false;
            _sendButton.Visible = true;
            _input.Enabled = true;
            _input.Focus();
        }
    }

    // ===================== บับเบิล =====================

    private int BubbleTextWidth => Math.Max(160, _list.ClientSize.Width - 90);

    private void AddBubble(string sender, DateTime time, bool mine, Control content)
    {
        var bubble = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = mine ? MyBubble : TheirBubble,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(mine ? 50 : 6, 4, mine ? 6 : 50, 4),
        };
        bubble.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"{sender}   {time:HH:mm}",
            Font = _boldFont,
            ForeColor = mine ? Color.SteelBlue : Color.SeaGreen,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 4),
        });
        bubble.Controls.Add(content);

        _list.Controls.Add(bubble);
        _list.ScrollControlIntoView(bubble);
    }

    private void ResizeBubbles()
    {
        var max = new Size(BubbleTextWidth, 0);
        _list.SuspendLayout();
        foreach (var label in _textLabels) label.MaximumSize = max;
        _list.ResumeLayout();
    }

    // ===================== การแสดง/ซ่อนหน้าต่าง =====================

    /// <summary>แสดงหน้าต่าง: activate = true จะดึงขึ้นมาข้างหน้า, false จะแค่กะพริบที่ Taskbar</summary>
    public void ShowAndNotify(bool activate)
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        if (activate)
        {
            Activate();
            if (_input.Enabled) _input.Focus();
        }
        else if (!ContainsFocus)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = Handle,
                dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG: กะพริบจนกว่าผู้ใช้จะคลิก
                uCount = uint.MaxValue,
            };
            FlashWindowEx(ref info);
        }
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        FinishRecording(send: false);
        StopPlayback();

        if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recordTimer.Dispose();
            _playTimer.Dispose();
            _recorder.Dispose();
            _boldFont.Dispose();
            _italicFont.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
