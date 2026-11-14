using System.Diagnostics;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>Agent-side policy, loaded from a SYSTEM/admin-writable file by the service.</summary>
public sealed record RunAllowlist(List<string> Programs);

public sealed record RunDecision(bool Allowed, string Reason, ProcessStartInfo? StartInfo);

/// <summary>
/// Default-deny command dispatcher. HTTP(S) URLs have no arguments; executables must
/// exactly match an absolute path in the protected config. Arguments are parsed into
/// ArgumentList and UseShellExecute is false, so cmd.exe never interprets metacharacters.
/// </summary>
public static class RunCommandPolicy
{
    public static RunAllowlist Load(string path)
    {
        if (!File.Exists(path)) return new RunAllowlist(new List<string>());
        var list = JsonSerializer.Deserialize<RunAllowlist>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty RunCommand allowlist");
        if (list.Programs is null || list.Programs.Count > 1000)
            throw new InvalidDataException("Invalid RunCommand allowlist");
        foreach (string? program in list.Programs)
        {
            if (program is null || !Path.IsPathFullyQualified(program) || program.Any(char.IsControl) ||
                program.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Allowlist entries must be absolute executable paths");
        }
        return list;
    }

    public static RunDecision Evaluate(RunCmd? command, RunAllowlist allowlist)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.Target) || command.Target.Any(char.IsControl))
            return Deny("invalid-target");
        string target = command.Target.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!string.IsNullOrEmpty(command.Arguments) || string.IsNullOrEmpty(uri.Host))
                return Deny("url-arguments-or-host");
            // Shell association is used only for a validated, argument-free web URL.
            return new RunDecision(true, "url", new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        if (!Path.IsPathFullyQualified(target) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Deny("not-allowlisted");
        string full;
        try { full = Path.GetFullPath(target); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return Deny("invalid-path"); }
        if (!allowlist.Programs.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)))
            return Deny("not-allowlisted");
        if (!TrySplitArguments(command.Arguments ?? "", out var arguments))
            return Deny("invalid-arguments");
        var psi = new ProcessStartInfo(full) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);
        return new RunDecision(true, "allowlisted-program", psi);
    }

    private static RunDecision Deny(string reason) => new(false, reason, null);

    /// <summary>Windows argv quoting without a shell. Reject unmatched quotes rather than guessing.</summary>
    public static bool TrySplitArguments(string text, out List<string> arguments)
    {
        arguments = new List<string>();
        if (text.Length > 8192 || text.Any(char.IsControl)) return false;
        var current = new System.Text.StringBuilder();
        bool quoted = false, hasToken = false;
        int slashes = 0;
        foreach (char c in text)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                current.Append('\\', slashes / 2);
                if (slashes % 2 == 0) quoted = !quoted;
                else current.Append('"');
                hasToken = true;
                slashes = 0;
                continue;
            }
            current.Append('\\', slashes);
            slashes = 0;
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (hasToken) { arguments.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else { current.Append(c); hasToken = true; }
        }
        if (quoted) return false;
        current.Append('\\', slashes);
        if (hasToken) arguments.Add(current.ToString());
        return true;
    }
}
