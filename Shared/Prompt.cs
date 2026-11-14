namespace RainDrop.Shared;

/// <summary>Compat facade kept for existing callers; renders the shared dark dialog.</summary>
public static class Prompt
{
    public static string[]? Show(IWin32Window? owner, string title, params (string Label, string Value)[] fields) =>
        UiDialog.Prompt(owner, title, fields.Select(f => (f.Label, f.Value, false)).ToArray());

    public static string[]? ShowWithSecrets(IWin32Window? owner, string title, params (string Label, string Value, bool Secret)[] fields) =>
        UiDialog.Prompt(owner, title, fields);
}
