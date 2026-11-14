using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>Only fixed event/result labels and metadata enter this log; never packet payloads.</summary>
public sealed record AuditEvent(string Event, string Ip, string Result, string Detail = "");

public sealed record AuditVerification(bool Valid, int Records, string? Error);

/// <summary>
/// Append-only JSONL hash chain. Each line includes SHA-256 of the previous complete
/// UTF-8 line (without its newline). Genesis uses 64 zeroes. Rotation keeps at most
/// five approximately 10-MiB files and links the first new line to the last old line.
/// The caller must place this directory where untrusted users cannot write or delete it.
/// A hash chain detects edits, not deletion of the entire retained history by an administrator.
/// </summary>
public sealed class AuditLog : IDisposable
{
    public const long RotateBytes = 10 * 1024 * 1024;
    public const int FileCount = 5;
    private static readonly string[] Events = { "connect", "disconnect", "auth-fail", "tls-fail", "rate-limit", "lock", "unlock", "power", "run", "blocklist", "revoke", "enroll", "audit-fail" };
    private readonly object _gate = new();
    private readonly string _folder;
    private readonly long _rotateBytes;
    private string _previous = new('0', 64);
    private bool _broken;

    private sealed record Line(long Sequence, DateTime TimeUtc, string Event, string Ip, string Result, string Detail, string PreviousHash, string Hash);
    private string CurrentPath => Path.Combine(_folder, "audit.log");
    private string ArchivePath(int index) => Path.Combine(_folder, $"audit.{index}.log");

    public AuditLog(string folder, long rotateBytes = RotateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rotateBytes, 1024);
        _folder = folder;
        _rotateBytes = rotateBytes;
        Directory.CreateDirectory(folder);
        var check = Verify(folder);
        if (!check.Valid) throw new InvalidDataException("Audit chain integrity check failed: " + check.Error);
        string newest = Enumerable.Range(0, FileCount).Select(i => i == 0 ? CurrentPath : ArchivePath(i))
            .FirstOrDefault(File.Exists) ?? CurrentPath;
        if (File.Exists(newest))
        {
            // Newest existing file is current only when audit.log exists; otherwise the last archive supplies the hash.
            var lines = File.ReadAllLines(newest, Encoding.UTF8);
            if (lines.Length > 0) _previous = HashLine(lines[^1]);
        }
    }

    public void Append(AuditEvent entry)
    {
        if (!Events.Contains(entry.Event) || entry.Ip.Length > 64 || entry.Result.Length > 32 ||
            entry.Detail.Length > 128 || (entry.Ip + entry.Result + entry.Detail).Any(char.IsControl))
            throw new ArgumentException("Audit metadata contains an unsupported label or control character");
        lock (_gate)
        {
            if (_broken) throw new IOException("Audit log is unavailable; operation denied");
            try
            {
                long sequence = LastSequence() + 1;
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length >= _rotateBytes) Rotate();
                var line = new Line(sequence, DateTime.UtcNow, entry.Event, entry.Ip, entry.Result, entry.Detail, _previous, "");
                string payload = JsonSerializer.Serialize(line);
                line = line with { Hash = HashLine(payload) };
                string json = JsonSerializer.Serialize(line);
                using (var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                _previous = HashLine(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _broken = true; // Fail closed: never silently restart a broken chain at genesis.
                throw new IOException("Cannot append audit record", ex);
            }
        }
    }

    private long LastSequence()
    {
        if (!File.Exists(CurrentPath))
        {
            // After a rotation, recover the sequence from the most recent archive.
            for (int i = 1; i < FileCount; i++)
                if (File.Exists(ArchivePath(i))) return ReadLastSequence(ArchivePath(i));
            return 0;
        }
        return ReadLastSequence(CurrentPath);
    }

    private static long ReadLastSequence(string path)
    {
        var lines = File.ReadAllLines(path);
        return lines.Length == 0 ? 0 : JsonSerializer.Deserialize<Line>(lines[^1])?.Sequence
            ?? throw new InvalidDataException("Invalid audit record");
    }

    private void Rotate()
    {
        for (int i = FileCount - 1; i >= 1; i--)
        {
            string from = i == 1 ? CurrentPath : ArchivePath(i - 1);
            if (!File.Exists(from)) continue;
            string to = ArchivePath(i);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
    }

    public static AuditVerification Verify(string folder)
    {
        try
        {
            string current = Path.Combine(folder, "audit.log");
            var paths = new List<string>();
            for (int i = FileCount - 1; i >= 1; i--)
            {
                string path = Path.Combine(folder, $"audit.{i}.log");
                if (File.Exists(path)) paths.Add(path);
            }
            if (File.Exists(current)) paths.Add(current);
            string previous = new('0', 64);
            long sequence = 0;
            int records = 0;
            bool first = true;
            foreach (string path in paths)
            {
                foreach (string json in File.ReadLines(path, Encoding.UTF8))
                {
                    var line = JsonSerializer.Deserialize<Line>(json) ?? throw new InvalidDataException("Null audit line");
                    if (!first && (line.PreviousHash != previous || line.Sequence != sequence + 1))
                        return new AuditVerification(false, records, $"Edited, reordered or missing line {line.Sequence} in {Path.GetFileName(path)}");
                    if (first && line.Sequence < 1) return new AuditVerification(false, records, "Invalid initial sequence");
                    string recomputed = HashLine(JsonSerializer.Serialize(line with { Hash = "" }));
                    if (line.Hash != recomputed)
                        return new AuditVerification(false, records, $"Edited line {line.Sequence} in {Path.GetFileName(path)}");
                    if (!Events.Contains(line.Event) || line.TimeUtc.Kind != DateTimeKind.Utc ||
                        line.Ip.Length > 64 || line.Result.Length > 32 || line.Detail.Length > 128 ||
                        (line.Ip + line.Result + line.Detail).Any(char.IsControl))
                        return new AuditVerification(false, records, "Invalid audit metadata");
                    previous = HashLine(json);
                    sequence = line.Sequence;
                    records++;
                    first = false;
                }
            }
            return new AuditVerification(true, records, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new AuditVerification(false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string HashLine(string line) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    public void Dispose() { /* The FileStream is flushed and closed on every append; no writable handle remains. */ }
}
