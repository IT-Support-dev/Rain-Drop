using RainDrop.Shared;

namespace RainDrop.AgentService;

internal enum GateStatus
{
    Allowed,
    WrongPassword,
    LockedOut,
    NotConfigured,
}

internal sealed record GateResult(GateStatus Status, int RetryAfterSeconds = 0);

/// <summary>
/// ตรวจรหัสผ่านผู้ดูแลฝั่ง Service (SYSTEM) เท่านั้น Agent ไม่ถือ hash อีกต่อไป
/// จำกัดลองผิด 5 ครั้ง แล้วล็อก 5 นาที เพื่อกันเดารหัสผ่านท่อ named pipe
/// นาฬิกาฉีดเข้ามาได้เพื่อให้เทสต์ตรวจพฤติกรรมล็อกหมดอายุได้โดยไม่ต้องรอจริง
/// </summary>
internal sealed class PasswordGate
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly byte[]? _salt;
    private readonly byte[]? _hash;

    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;

    public PasswordGate(byte[]? salt, byte[]? hash, Func<DateTime>? clock = null)
    {
        _salt = salt;
        _hash = hash;
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    public bool HasPassword => _salt is { Length: > 0 } && _hash is { Length: > 0 };

    public GateResult Verify(string? password)
    {
        lock (_gate)
        {
            DateTime now = _clock();

            if (now < _lockedUntil)
                return new GateResult(GateStatus.LockedOut, SecondsLeft(now));

            // หมดเวลาล็อกแล้ว เริ่มนับรอบใหม่
            if (_lockedUntil != DateTime.MinValue)
            {
                _lockedUntil = DateTime.MinValue;
                _failures = 0;
            }

            // ไม่ได้ตั้งรหัส = ไม่อนุญาตให้หยุด (ไม่มีรหัสเริ่มต้นอีกต่อไป)
            if (!HasPassword) return new GateResult(GateStatus.NotConfigured);

            if (PasswordHashing.Verify(password, _salt, _hash))
            {
                _failures = 0;
                return new GateResult(GateStatus.Allowed);
            }

            _failures++;
            if (_failures >= MaxAttempts)
            {
                _failures = 0;
                _lockedUntil = now + LockoutDuration;
                return new GateResult(GateStatus.LockedOut, SecondsLeft(now));
            }

            return new GateResult(GateStatus.WrongPassword);
        }
    }

    private int SecondsLeft(DateTime now) =>
        (int)Math.Ceiling((_lockedUntil - now).TotalSeconds);
}
