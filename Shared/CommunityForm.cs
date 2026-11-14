using System.Diagnostics;

namespace RainDrop.Shared;

/// <summary>
/// หน้าต่าง "ชุมชน" แบบ Discord ใช้ร่วมกันทั้งผู้ดูแลและเครื่องลูก
///   ซ้าย:  รายชื่อช่องข้อความ, ช่องเสียง (กดเพื่อเข้า/ออก), และรายชื่อสมาชิก + สถานะออนไลน์
///   กลาง: ข้อความของช่องที่เลือก + แนบไฟล์/รูป
///   ล่าง:  ช่องพิมพ์ข้อความ
/// รับส่งผ่าน HubClient และคอลเสียงผ่าน VoiceChat
/// </summary>
public sealed class CommunityForm : Form
{
    private readonly HubClient _hub;
    private readonly VoiceChat _voice;
    private readonly string _myName;

    private readonly ListBox _textList = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly ListBox _voiceList = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly ListBox _memberList = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel _messages = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = new Padding(6) };
    private readonly Label _channelTitle = new() { Dock = DockStyle.Top, Height = 32, Font = new Font("Tahoma", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(240, 242, 245) };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true };
    private readonly Button _voiceButton = new() { Dock = DockStyle.Top, Height = 34, Text = "เข้าร่วมห้องเสียงที่เลือก", Enabled = false };
    private readonly Button _muteButton = new() { Dock = DockStyle.Top, Height = 28, Text = "ปิดไมค์", Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray };

    private readonly List<ChannelInfo> _channels = new();
    private readonly Dictionary<string, List<ChatPost>> _byChannel = new();
    private readonly Dictionary<int, string> _memberNames = new();
    private Dictionary<string, List<int>> _voiceRoster = new();
    private string? _currentChannel;
    private string? _myVoiceChannel;

    // การรับไฟล์: fileId -> บัฟเฟอร์ที่กำลังประกอบ
    private readonly Dictionary<Guid, MemoryStream> _incomingFiles = new();
    private readonly Dictionary<Guid, AttachmentInfo> _fileInfo = new();
    private readonly Dictionary<Guid, string> _sentFilePaths = new(); // ไฟล์ที่เราส่งเอง (ไม่ได้รับ chunk กลับ)

    public CommunityForm(HubClient hub, string myName)
    {
        _hub = hub;
        _myName = myName;
        _voice = new VoiceChat(pcm => _hub.SendVoice(pcm));

        Text = "Rain Drop - ชุมชน";
        Size = new Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9.5f);
        Icon = AppIcon.Load();

        BuildLayout();
        WireHub();

        _hub.Connected += () => UI(() => _status.Text = "เชื่อมต่อแล้ว กำลังเข้าร่วม...");
        _hub.Disconnected += reason => UI(() => _status.Text = "หลุดการเชื่อมต่อ: " + reason);
    }

    // ===================== วางเลย์เอาต์ =====================

    private void BuildLayout()
    {
        // ----- ซ้าย: ช่อง + สมาชิก (แผงกว้างคงที่ หลีกเลี่ยงปัญหา SplitterDistance ของ SplitContainer) -----
        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 240, ColumnCount = 1, RowCount = 7, BackColor = Color.FromArgb(246, 247, 249) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); // ปุ่มเสียง
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 44));

        left.Controls.Add(Header("# ช่องข้อความ"), 0, 0);
        left.Controls.Add(_textList, 0, 1);
        left.Controls.Add(Header("🔊 ช่องเสียง"), 0, 2);
        left.Controls.Add(_voiceList, 0, 3);
        var voiceButtons = new Panel { Dock = DockStyle.Fill };
        voiceButtons.Controls.Add(_muteButton);
        voiceButtons.Controls.Add(_voiceButton);
        left.Controls.Add(voiceButtons, 0, 4);
        left.Controls.Add(Header("สมาชิก"), 0, 5);
        left.Controls.Add(_memberList, 0, 6);

        // ----- ขวา: แชท -----
        var right = new Panel { Dock = DockStyle.Fill };

        var inputBar = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(6) };
        var send = new Button { Dock = DockStyle.Right, Width = 72, Text = "ส่ง" };
        var attach = new Button { Dock = DockStyle.Right, Width = 90, Text = "แนบไฟล์" };
        inputBar.Controls.Add(_input);
        inputBar.Controls.Add(attach);
        inputBar.Controls.Add(send);

        right.Controls.Add(_messages);
        right.Controls.Add(inputBar);
        right.Controls.Add(_channelTitle);

        // ลำดับ Dock: Fill (right) ต้องเพิ่มก่อน Left (nav) แล้วค่อย Bottom (status)
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(_status);

        _textList.SelectedIndexChanged += (_, _) => SelectTextChannel();
        _voiceList.SelectedIndexChanged += (_, _) => _voiceButton.Enabled = _voiceList.SelectedIndex >= 0;
        _voiceButton.Click += (_, _) => ToggleVoice();
        _muteButton.Click += (_, _) => ToggleMute();
        send.Click += (_, _) => SendText();
        attach.Click += (_, _) => AttachFile();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendText(); }
        };
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = new Font("Tahoma", 8.5f, FontStyle.Bold),
        ForeColor = Color.DimGray,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
    };

    // ===================== เชื่อม HubClient =====================

    private void WireHub()
    {
        _hub.Welcomed += w => UI(() => OnWelcome(w));
        _hub.RosterChanged += m => UI(() => OnRoster(m));
        _hub.ChatReceived += p => UI(() => OnChat(p));
        _hub.VoiceRosterChanged += v => UI(() => OnVoiceRoster(v));
        _hub.VoiceFrameReceived += (id, pcm) => _voice.Play(id, pcm); // ไม่ต้องผ่าน UI thread
        _hub.FileChunkReceived += (id, seq, last, data) => UI(() => OnFileChunk(id, seq, last, data));
    }

    private void OnWelcome(WelcomeMsg w)
    {
        _channels.Clear();
        _channels.AddRange(w.Channels);
        _textList.Items.Clear();
        _voiceList.Items.Clear();
        foreach (var ch in w.Channels)
        {
            if (ch.Kind == ChannelKind.Text) _textList.Items.Add("# " + ch.Name);
            else _voiceList.Items.Add("🔊 " + ch.Name);
        }

        _byChannel.Clear();
        foreach (var post in w.History)
        {
            if (!_byChannel.TryGetValue(post.ChannelId, out var list))
                _byChannel[post.ChannelId] = list = new List<ChatPost>();
            list.Add(post);
        }

        OnRoster(w.Members);
        if (_textList.Items.Count > 0) _textList.SelectedIndex = 0;
        _status.Text = $"เข้าร่วมแล้วในชื่อ {_myName}";
    }

    private void OnRoster(List<MemberInfo> members)
    {
        _memberNames.Clear();
        _memberList.Items.Clear();
        foreach (var m in members.OrderByDescending(x => x.Role == MemberRole.Admin).ThenBy(x => x.Name))
        {
            _memberNames[m.Id] = m.Name;
            string dot = m.State == PresenceState.Online ? "●" : "○";
            string tag = m.Role == MemberRole.Admin ? " [ผู้ดูแล]" : "";
            string inVoice = m.VoiceChannelId is not null ? "  🔊" : "";
            _memberList.Items.Add($"{dot} {m.Name}{tag}{inVoice}");
        }
    }

    private void OnChat(ChatPost post)
    {
        if (!_byChannel.TryGetValue(post.ChannelId, out var list))
            _byChannel[post.ChannelId] = list = new List<ChatPost>();
        list.Add(post);

        // จำข้อมูลไฟล์แนบไว้ทันที เพื่อให้ไฟล์ที่ทยอยเข้ามาถูกบันทึกด้วยชื่อที่ถูกต้อง
        if (post.Attachment is { } a && Guid.TryParse(a.FileId, out var gid)) _fileInfo[gid] = a;

        if (post.ChannelId == _currentChannel)
            AddBubble(post);
        else
            FlashChannel(post.ChannelId);
    }

    private void OnVoiceRoster(VoiceRosterMsg v)
    {
        _voiceRoster = v.ByChannel;
        RefreshVoiceList(); // อัปเดตว่าช่องเสียงไหนมีใครอยู่บ้าง
    }

    private void RefreshVoiceList()
    {
        var voiceChannels = _channels.Where(c => c.Kind == ChannelKind.Voice).ToList();
        int sel = _voiceList.SelectedIndex;
        _voiceList.BeginUpdate();
        _voiceList.Items.Clear();
        foreach (var ch in voiceChannels)
        {
            int count = _voiceRoster.TryGetValue(ch.Id, out var ids) ? ids.Count : 0;
            string names = count > 0 ? "  (" + string.Join(", ", (_voiceRoster[ch.Id]).Select(NameOf)) + ")" : "";
            string here = ch.Id == _myVoiceChannel ? "✔ " : "";
            _voiceList.Items.Add($"{here}🔊 {ch.Name}{names}");
        }
        if (sel >= 0 && sel < _voiceList.Items.Count) _voiceList.SelectedIndex = sel;
        _voiceList.EndUpdate();
    }

    private string NameOf(int id) => _memberNames.TryGetValue(id, out var n) ? n : $"#{id}";

    // ===================== ช่องข้อความ =====================

    private void SelectTextChannel()
    {
        int i = _textList.SelectedIndex;
        var textChannels = _channels.Where(c => c.Kind == ChannelKind.Text).ToList();
        if (i < 0 || i >= textChannels.Count) return;

        _currentChannel = textChannels[i].Id;
        _channelTitle.Text = "# " + textChannels[i].Name;
        _messages.Controls.Clear();
        if (_byChannel.TryGetValue(_currentChannel, out var list))
            foreach (var post in list) AddBubble(post);
    }

    private void FlashChannel(string channelId)
    {
        var ch = _channels.FirstOrDefault(c => c.Id == channelId);
        if (ch is not null) _status.Text = $"มีข้อความใหม่ในช่อง # {ch.Name}";
    }

    private void SendText()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || _currentChannel is null) return;
        _hub.PostChat(_currentChannel, text);
        _input.Clear();
        _input.Focus();
    }

    private void AddBubble(ChatPost post)
    {
        bool mine = post.SenderId == _hub.SelfId;
        var bubble = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = mine ? Color.FromArgb(214, 234, 255) : Color.FromArgb(238, 240, 243),
            Margin = new Padding(mine ? 120 : 4, 3, mine ? 4 : 120, 3),
            Padding = new Padding(8, 5, 8, 7),
        };
        bubble.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"{post.SenderName}   {post.Time:HH:mm}",
            Font = new Font("Tahoma", 8, FontStyle.Bold),
            ForeColor = mine ? Color.SteelBlue : Color.SeaGreen,
            Margin = new Padding(0, 0, 0, 3),
        });
        if (!string.IsNullOrEmpty(post.Text))
            bubble.Controls.Add(new Label { AutoSize = true, Text = post.Text, MaximumSize = new Size(420, 0), UseMnemonic = false });

        if (post.Attachment is { } att)
        {
            if (Guid.TryParse(att.FileId, out var gid)) _fileInfo[gid] = att;
            bubble.Controls.Add(BuildAttachment(att));
        }

        _messages.Controls.Add(bubble);
        _messages.ScrollControlIntoView(bubble);
    }

    // ===================== แนบไฟล์ / รูป =====================

    private Control BuildAttachment(AttachmentInfo att)
    {
        // ถ้าไฟล์พร้อมแล้ว (เราส่งเอง หรือรับครบแล้ว) แสดงเลย ไม่งั้นแสดงตัวโหลด
        string? path = Guid.TryParse(att.FileId, out var gid) ? ResolveFilePath(gid) : null;
        if (att.IsImage && path is not null && File.Exists(path))
        {
            try
            {
                using var img = Image.FromFile(path);
                var thumb = new Bitmap(img); // คัดลอกเพื่อปล่อยไฟล์
                var pic = new PictureBox { Image = thumb, SizeMode = PictureBoxSizeMode.Zoom, Size = FitSize(thumb.Size, 260, 200), Cursor = Cursors.Hand };
                pic.Click += (_, _) => OpenFile(path);
                return pic;
            }
            catch { /* ตกไปแสดงเป็นลิงก์ */ }
        }

        var link = new LinkLabel { AutoSize = true, Text = $"📎 {att.FileName} ({Human(att.Size)})" };
        if (path is not null && File.Exists(path)) link.Click += (_, _) => OpenFile(path);
        else link.Text += "  (กำลังรับ...)";
        link.Tag = att.FileId;
        return link;
    }

    private string? ResolveFilePath(Guid fileId)
    {
        if (_sentFilePaths.TryGetValue(fileId, out var p)) return p;
        if (_fileInfo.TryGetValue(fileId, out var info) && !_incomingFiles.ContainsKey(fileId))
            return Path.Combine(DownloadDir, SafeName(fileId, info.FileName));
        return null;
    }

    private void AttachFile()
    {
        if (_currentChannel is null) { _status.Text = "เลือกช่องข้อความก่อน"; return; }
        using var dialog = new OpenFileDialog { Title = "เลือกไฟล์ที่จะส่ง" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var fi = new FileInfo(dialog.FileName);
        if (fi.Length > HubProtocol.MaxFileSize)
        {
            MessageBox.Show($"ไฟล์ใหญ่เกิน {Human(HubProtocol.MaxFileSize)}", "Rain Drop");
            return;
        }

        var fileId = Guid.NewGuid();
        bool isImage = IsImageName(fi.Name);
        var att = new AttachmentInfo(fileId.ToString(), fi.Name, fi.Length, isImage);
        _sentFilePaths[fileId] = fi.FullName;

        // ประกาศข้อความที่มีไฟล์แนบก่อน แล้วค่อยทยอยส่งเนื้อไฟล์
        _hub.PostChat(_currentChannel, string.IsNullOrWhiteSpace(_input.Text) ? "" : _input.Text.Trim(), att);
        _input.Clear();
        _ = Task.Run(() => SendFileChunks(fileId, fi.FullName));
    }

    private async Task SendFileChunks(Guid fileId, string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var buffer = new byte[HubProtocol.FileChunkSize];
            int seq = 0, read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                bool last = fs.Position >= fs.Length;
                _hub.SendFileChunk(fileId, seq++, last, buffer.AsSpan(0, read));
                await Task.Delay(5); // กันไม่ให้ไปเบียดช่องเสียง
            }
        }
        catch (Exception ex)
        {
            UI(() => _status.Text = "ส่งไฟล์ไม่สำเร็จ: " + ex.Message);
        }
    }

    private void OnFileChunk(Guid fileId, int seq, bool last, byte[] data)
    {
        if (!_incomingFiles.TryGetValue(fileId, out var ms))
            _incomingFiles[fileId] = ms = new MemoryStream();
        ms.Write(data, 0, data.Length);

        if (!last) return;

        // ประกอบเสร็จ: เขียนลงไฟล์ใน Downloads\RainDrop
        _fileInfo.TryGetValue(fileId, out var info);
        string name = info?.FileName ?? (fileId + ".dat");
        string path = Path.Combine(DownloadDir, SafeName(fileId, name));
        try
        {
            Directory.CreateDirectory(DownloadDir);
            File.WriteAllBytes(path, ms.ToArray());
        }
        catch { /* เขียนไม่ได้ */ }
        finally
        {
            ms.Dispose();
            _incomingFiles.Remove(fileId);
        }

        RerenderCurrentChannel(); // ให้ลิงก์/รูปของไฟล์นี้อัปเดตเป็นพร้อมใช้งาน
    }

    private void RerenderCurrentChannel()
    {
        if (_currentChannel is null) return;
        // เก็บ fileInfo จากข้อความในช่องปัจจุบันไว้ก่อน (เผื่อ announcement มากับไฟล์)
        if (_byChannel.TryGetValue(_currentChannel, out var list))
            foreach (var p in list)
                if (p.Attachment is { } a && Guid.TryParse(a.FileId, out var gid))
                    _fileInfo[gid] = a;

        _messages.SuspendLayout();
        _messages.Controls.Clear();
        if (list is not null) foreach (var post in list) AddBubble(post);
        _messages.ResumeLayout();
    }

    // ===================== คอลเสียง =====================

    private void ToggleVoice()
    {
        var voiceChannels = _channels.Where(c => c.Kind == ChannelKind.Voice).ToList();
        int i = _voiceList.SelectedIndex;
        if (i < 0 || i >= voiceChannels.Count) return;
        string target = voiceChannels[i].Id;

        if (_myVoiceChannel == target)
        {
            LeaveVoice();
            return;
        }

        try
        {
            _voice.Stop();
            _voice.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("เปิดไมโครโฟน/ลำโพงไม่ได้: " + ex.Message +
                "\n\nตรวจว่ามีอุปกรณ์เสียงและอนุญาตให้แอปใช้ไมโครโฟนแล้ว", "Rain Drop");
            return;
        }

        _myVoiceChannel = target;
        _hub.JoinVoice(target);
        _voiceButton.Text = "ออกจากห้องเสียง";
        _muteButton.Visible = true;
        _muteButton.Text = "ปิดไมค์";
        _voice.Muted = false;
        _status.Text = $"อยู่ในห้องเสียง: {voiceChannels[i].Name}";
        RefreshVoiceList();
    }

    private void LeaveVoice()
    {
        _voice.Stop();
        _myVoiceChannel = null;
        _hub.JoinVoice(null);
        _voiceButton.Text = "เข้าร่วมห้องเสียงที่เลือก";
        _muteButton.Visible = false;
        _status.Text = "ออกจากห้องเสียงแล้ว";
        RefreshVoiceList();
    }

    private void ToggleMute()
    {
        _voice.Muted = !_voice.Muted;
        _muteButton.Text = _voice.Muted ? "เปิดไมค์" : "ปิดไมค์";
    }

    // ===================== ตัวช่วย =====================

    private void UI(Action action)
    {
        if (IsDisposed) return;
        if (IsHandleCreated) BeginInvoke(action);
    }

    private static string DownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RainDrop");

    private static string SafeName(Guid id, string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return id.ToString("N")[..8] + "_" + name;
    }

    private static bool IsImageName(string name)
    {
        string e = Path.GetExtension(name).ToLowerInvariant();
        return e is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }

    private static Size FitSize(Size src, int maxW, int maxH)
    {
        double scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
        if (scale > 1) scale = 1;
        return new Size((int)(src.Width * scale), (int)(src.Height * scale));
    }

    private static string Human(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1 << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_hub.IsConnected) _hub.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // ปิดหน้าต่าง = ออกจากห้องเสียงและตัดการเชื่อมต่อ (เปิดใหม่ได้)
        _voice.Dispose();
        base.OnFormClosing(e);
    }
}
