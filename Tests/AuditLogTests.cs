using System.Text;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raindrop-audit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void MidFileEdit_IsDetected()
    {
        using (var log = new AuditLog(_dir))
        {
            log.Append(new AuditEvent("connect", "127.0.0.1", "ok"));
            log.Append(new AuditEvent("run", "127.0.0.1", "denied", "not-allowlisted"));
            log.Append(new AuditEvent("disconnect", "127.0.0.1", "ok"));
        }
        Assert.True(AuditLog.Verify(_dir).Valid);
        string path = Path.Combine(_dir, "audit.log");
        var lines = File.ReadAllLines(path);
        lines[1] = lines[1].Replace("denied", "okxxxx");
        File.WriteAllLines(path, lines);
        var result = AuditLog.Verify(_dir);
        Assert.False(result.Valid);
        Assert.Contains("Edited", result.Error);
    }

    [Fact]
    public void LastLineEdit_IsDetectedBySelfHash()
    {
        using (var log = new AuditLog(_dir)) log.Append(new AuditEvent("lock", "10.0.0.1", "ok"));
        string path = Path.Combine(_dir, "audit.log");
        File.WriteAllText(path, File.ReadAllText(path).Replace("lock", "xxxx"));
        Assert.False(AuditLog.Verify(_dir).Valid);
    }

    [Fact]
    public void PayloadContent_IsRejectedBySchema()
    {
        using var log = new AuditLog(_dir);
        Assert.Throws<ArgumentException>(() => log.Append(new AuditEvent("chat", "127.0.0.1", "ok", "secret text")));
        Assert.Throws<ArgumentException>(() => log.Append(new AuditEvent("run", "127.0.0.1", "ok", "line1\nline2")));
    }

    [Fact]
    public void Rotation_KeepsFiveFilesAndChainLinksAcrossFiles()
    {
        using (var log = new AuditLog(_dir, rotateBytes: 4096))
        {
            for (int i = 0; i < 40; i++) log.Append(new AuditEvent("connect", "127.0.0.1", "ok", i.ToString()));
        }
        var result = AuditLog.Verify(_dir);
        Assert.True(result.Valid, result.Error);
        Assert.Equal(40, result.Records);
        Assert.True(Directory.GetFiles(_dir, "audit*.log").Length <= AuditLog.FileCount);
        Assert.True(File.Exists(Path.Combine(_dir, "audit.1.log")));
    }
}
