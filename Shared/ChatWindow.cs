using System.Media;
using System.Runtime.InteropServices;

namespace RainDrop.Shared;

/// <summary>
/// หน้าต่างแชท ใช้ร่วมกันทั้งฝั่ง Master และ Agent
/// - ข้อความแสดงเป็นบับเบิลธีมมืด (ของเราเน้นสี accent, ของอีกฝั่งพื้นผิวรอง)
/// - ปุ่มอัดเสียง กดครั้งแรกเริ่มอัด กดอีกครั้งเพื่อหยุดและส่ง (สูงสุด 2 นาที)
/// - ข้อความเสียงกดเพื่อฟัง, คลิกขวาเพื่อบันทึกเป็นไฟล์ .wav
/// - ปิดหน้าต่างแล้วจะแค่ซ่อน ประวัติการคุยจึงยังอยู่
/// </summary>
public sealed class ChatWindow : Form
{
    private const int MaxRecordSeconds = 120;

    private readonly UiFlowPanel _list;
    private readonly TextBox _input;
    private readonly UiButton _sendButton;
    private readonly UiButton _recordButton;
    private readonly UiButton _cancelRecordButton;
    private readonly Func<string, bool> _onSend;
    private readonly Func<VoiceMsg, bool>? _onSendVoice;
    private readonly string _myName;
    private readonly List<Label> _textLabels = new();

    private readonly VoiceRecorder _recorder = new();
    private readonly System.Windows.Forms.Timer _recordTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _playTimer = new();
    private SoundPlayer? _player;
    private Button? _playingButton;
    private bool _forceClose;

    public ChatWindow(string title, string myName, Func<string, bool> onSend, Func<VoiceMsg, bool>? onSendVoice = null)
    {
        _myName = myName;
        _onSend = onSend;
        _onSendVoice = onSendVoice;

        Text = title;
        Theme.ConfigureForm(this, new Size(360, 320));
        UiWindowPlacement.Attach(this, "chat", new Size(480, 600), new Size(360, 320));
        Font = Theme.FontChat;

        _list = new UiFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Background,
            Padding = new Padding(Theme.Space3),
        };
        _list.Resize += (_, _) => ResizeBubbles();

        _input = UiInput.Create();
        _input.Dock = DockStyle.Fill;
        _input.AccessibleName = "ข้อความที่จะส่ง";
        _sendButton = new UiButton("ส่ง", ButtonKind.Primary, UiIcon.Send) { Dock = DockStyle.Right, Width = 80 };
        _recordButton = new UiButton("อัดเสียง", ButtonKind.Secondary, UiIcon.Mic)
        {
            Dock = DockStyle.Left,
            Width = 112,
            Visible = onSendVoice is not null,
        };
        _cancelRecordButton = new UiButton("ยกเลิก", ButtonKind.Ghost) { Dock = DockStyle.Right, Width = 80, Visible = false };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.ButtonHeightLarge + Theme.Space4, Padding = new Padding(Theme.Space3), BackColor = Theme.Surface };
        bottom.Controls.Add(_input);
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
            ForeColor = Theme.TextPrimary,
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
            ForeColor = Theme.TextMuted,
            Font = Theme.FontChatItalic,
            UseMnemonic = false,
            MaximumSize = new Size(BubbleTextWidth, 0),
            Margin = new Padding(Theme.Space2),
        };
        _textLabels.Add(label);
        _list.Controls.Add(label);
        _list.ScrollControlIntoView(label);
    }

    public void AppendVoice(VoiceMsg msg, bool mine)
    {
        var button = new UiButton(PlayCaption(msg.Seconds), ButtonKind.Secondary, UiIcon.Play)
        {
            AutoSize = true,
            Margin = Padding.Empty,
        };
        button.Click += (_, _) => TogglePlayback(button, msg);

        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add("บันทึกเป็นไฟล์ .wav...", null, (_, _) => SaveVoice(msg));
        button.ContextMenuStrip = menu;

        AddBubble(mine ? "คุณ" : msg.Sender, msg.Time, mine, button);
    }

    private static string PlayCaption(double seconds) =>
        $"ข้อความเสียง  {TimeSpan.FromSeconds(Math.Max(1, Math.Round(seconds))):m\\:ss}";

    private void TogglePlayback(Button button, VoiceMsg msg)
    {
        bool sameButton = _playingButton == button;
        StopPlayback();
        if (sameButton) return;

        try
        {
            _player = new SoundPlayer(new MemoryStream(msg.Wav));
            _player.Play();
        }
        catch (Exception ex)
        {
            StopPlayback();
            AppendSystem("เล่นเสียงไม่ได้: " + ex.Message);
            return;
        }

        _playingButton = button;
        button.Tag = button.Text;
        button.Text = "หยุด";
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

        _recordButton.Kind = ButtonKind.Danger;
        _recordButton.Width = 150;
        _cancelRecordButton.Visible = true;
        _sendButton.Visible = false;
        _input.Enabled = false;
        OnRecordTick();
        _recordTimer.Start();
    }

    private void OnRecordTick()
    {
        var elapsed = _recorder.Elapsed;
        _recordButton.Text = $"หยุดและส่ง {elapsed:m\\:ss}";
        if (elapsed.TotalSeconds >= MaxRecordSeconds)
            FinishRecording(send: true);
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
            _recordButton.Kind = ButtonKind.Secondary;
            _recordButton.Text = "อัดเสียง";
            _recordButton.Width = 112;
            _cancelRecordButton.Visible = false;
            _sendButton.Visible = true;
            _input.Enabled = true;
            _input.Focus();
        }
    }

    private int BubbleTextWidth => Math.Max(160, _list.ClientSize.Width - 90);

    private void AddBubble(string sender, DateTime time, bool mine, Control content)
    {
        var bubble = new UiFlowPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = mine ? Theme.Mix(Theme.Accent, Theme.Surface, 0.65) : Theme.SurfaceAlt,
            Padding = new Padding(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2),
            Margin = new Padding(mine ? 50 : Theme.Space1, Theme.Space1, mine ? Theme.Space1 : 50, Theme.Space1),
        };
        bubble.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"{sender}   {time:HH:mm}",
            Font = Theme.FontCaptionStrong,
            ForeColor = mine ? Theme.TextPrimary : Theme.Online,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, Theme.Space1),
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
                dwFlags = 3 | 12,
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
