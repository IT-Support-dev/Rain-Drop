namespace RainDrop.Shared;

internal enum DialogKind { Info, Warning, Error, Question }

/// <summary>Dark, token-based replacement for MessageBox and the old Prompt dialog.</summary>
internal static class UiDialog
{
    internal static void Show(IWin32Window? owner, string title, string message, DialogKind kind = DialogKind.Info) =>
        ShowCore(owner, title, message, kind, null, null, out _, out _);

    internal static bool Confirm(IWin32Window? owner, string title, string message, string confirmText = "ยืนยัน", bool danger = false)
    {
        ShowCore(owner, title, message, danger ? DialogKind.Warning : DialogKind.Question,
            new[] { (confirmText, danger ? ButtonKind.Danger : ButtonKind.Primary), ("ยกเลิก", ButtonKind.Secondary) },
            1, out int index, out _);
        return index == 0;
    }

    internal static string[]? Prompt(IWin32Window? owner, string title, params (string Label, string Value, bool Secret)[] fields)
    {
        ShowCore(owner, title, null, DialogKind.Info,
            new[] { ("ตกลง", ButtonKind.Primary), ("ยกเลิก", ButtonKind.Secondary) }, 1, out int index, out string[]? values, fields);
        return index == 0 ? values : null;
    }

    private static void ShowCore(IWin32Window? owner, string title, string? message, DialogKind kind,
        (string Text, ButtonKind Kind)[]? buttons, int cancelIndex, out int clicked, out string[]? values,
        (string Label, string Value, bool Secret)[]? fields = null)
    {
        clicked = cancelIndex;
        values = null;
        using var form = new Form();
        Theme.ConfigureForm(form, new Size(420, 200));
        form.Text = title;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.StartPosition = FormStartPosition.CenterParent;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.Padding = new Padding(Theme.Space5);

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, Theme.Space3) };
        var icon = kind switch { DialogKind.Warning => UiIcon.Warning, DialogKind.Error => UiIcon.Warning, DialogKind.Question => UiIcon.Info, _ => UiIcon.Info };
        var color = kind switch { DialogKind.Warning => Theme.Waiting, DialogKind.Error => Theme.Danger, _ => Theme.AccentHover };
        head.Controls.Add(new PictureBox { Image = UiIcons.Get(icon, color, Theme.IconSizeLarge), Size = new Size(Theme.IconSizeLarge, Theme.IconSizeLarge), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, Theme.Space2, 0) });
        head.Controls.Add(new Label { Text = title, Font = Theme.FontTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(0) });
        layout.Controls.Add(head);

        if (message is not null)
            layout.Controls.Add(new Label { Text = message, Font = Theme.FontBody, ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, Theme.Space3) });

        var boxes = new List<TextBox>();
        if (fields is not null)
        {
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, Theme.Space3) };
            foreach (var (label, value, secret) in fields)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Font = Theme.FontBody, ForeColor = Theme.TextSecondary, Margin = new Padding(0, Theme.Space2, 0, Theme.Space1) });
                var box = UiInput.Create(value, secret: secret);
                box.Width = 420;
                box.AccessibleName = label;
                boxes.Add(box);
                grid.Controls.Add(box);
            }
            layout.Controls.Add(grid);
        }

        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, Theme.Space2, 0, 0) };
        buttons ??= new[] { ("ตกลง", ButtonKind.Primary) };
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int index = i;
            var (text, buttonKind) = buttons[i];
            var button = new UiButton(text, buttonKind) { Width = 112, Height = Theme.ButtonHeightLarge };
            button.Click += (_, _) => { clicked = index; form.DialogResult = index == cancelIndex ? DialogResult.Cancel : DialogResult.OK; form.Close(); };
            actions.Controls.Add(button);
            if (i == 0) form.AcceptButton = button;
            if (i == cancelIndex) form.CancelButton = button;
        }
        layout.Controls.Add(actions);
        form.Controls.Add(layout);
        if (boxes.Count > 0) form.Shown += (_, _) => boxes[0].Focus();
        form.ShowDialog(owner);
        if (fields is not null)
            values = boxes.Select((b, i) => fields[i].Secret ? b.Text : b.Text.Trim()).ToArray();
    }
}
