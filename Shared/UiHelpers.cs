using System.Text.Json;

namespace RainDrop.Shared;

internal enum MachineStatusFilter { All, Online, Offline }

internal static class UiFilter
{
    internal static bool Matches(string? query, MachineStatusFilter status, bool online, string name, string address)
    {
        if (status == MachineStatusFilter.Online && !online) return false;
        if (status == MachineStatusFilter.Offline && online) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return name.Contains(q, StringComparison.OrdinalIgnoreCase) || address.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CountText(int visible, int total) =>
        visible == total ? $"{total} เครื่อง" : $"{visible}/{total} เครื่อง";
}

internal sealed record WindowPlacement(int X, int Y, int Width, int Height);

/// <summary>Remembers window bounds per key; clamps to the current virtual screen and minimum size.</summary>
internal static class UiWindowPlacement
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainDrop", "ui-windows.json");
    private static readonly object Gate = new();

    internal static Rectangle Clamp(Rectangle bounds, Rectangle screen, Size minimum)
    {
        int w = Math.Max(minimum.Width, bounds.Width);
        int h = Math.Max(minimum.Height, bounds.Height);
        w = Math.Min(w, screen.Width);
        h = Math.Min(h, screen.Height);
        int x = Math.Clamp(bounds.X, screen.Left, Math.Max(screen.Left, screen.Right - w));
        int y = Math.Clamp(bounds.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    internal static void Attach(Form form, string key, Size defaultSize, Size minimum)
    {
        form.MinimumSize = minimum;
        form.Size = defaultSize;
        var saved = Load(key);
        if (saved is not null)
        {
            var screen = Screen.FromRectangle(new Rectangle(saved.X, saved.Y, saved.Width, saved.Height)).WorkingArea;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = Clamp(new Rectangle(saved.X, saved.Y, saved.Width, saved.Height), screen, minimum);
        }
        form.FormClosed += (_, _) =>
        {
            if (form.WindowState != FormWindowState.Normal) return;
            Save(key, new WindowPlacement(form.Left, form.Top, form.Width, form.Height));
        };
    }

    private static Dictionary<string, WindowPlacement> Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Diag.Warn("UiWindowPlacement", "Could not read window placement; using defaults", ex);
        }
        return new();
    }

    private static WindowPlacement? Load(string key)
    {
        lock (Gate) return Read().TryGetValue(key, out var p) ? p : null;
    }

    private static void Save(string key, WindowPlacement placement)
    {
        lock (Gate)
        {
            try
            {
                var all = Read();
                all[key] = placement;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Warn("UiWindowPlacement", "Could not save window placement", ex);
            }
        }
    }
}
