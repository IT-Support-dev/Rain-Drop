using System.Net;

namespace RainDrop.Agent;

/// <summary>Sliding one-minute limits, normalized IP keys, bounded memory. Tests inject a clock.</summary>
internal sealed class ConnectionRateLimiter
{
    private sealed class Bucket
    {
        public readonly Queue<long> Connections = new();
        public readonly Queue<long> Failures = new();
        public long LastSeen;
    }
    private readonly Dictionary<string, Bucket> _buckets = new();
    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private readonly int _connectionsPerMinute;
    private readonly int _failuresPerMinute;
    private readonly int _maxIps;
    private const long Window = 60_000;

    public ConnectionRateLimiter(int connectionsPerMinute = 30, int failuresPerMinute = 8,
        int maxIps = 4096, Func<long>? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIps, 1);
        _connectionsPerMinute = connectionsPerMinute;
        _failuresPerMinute = failuresPerMinute;
        _maxIps = maxIps;
        _timestamp = timestamp ?? Environment.TickCount64;
    }

    public bool TryConnect(IPAddress ip)
    {
        lock (_gate)
        {
            var bucket = FindOrCreate(ip);
            if (bucket is null) return false;
            Prune(bucket);
            if (bucket.Connections.Count >= _connectionsPerMinute || bucket.Failures.Count >= _failuresPerMinute) return false;
            bucket.Connections.Enqueue(_timestamp());
            return true;
        }
    }

    public bool RecordFailure(IPAddress ip)
    {
        lock (_gate)
        {
            var bucket = FindOrCreate(ip);
            if (bucket is null) return false;
            Prune(bucket);
            bucket.Failures.Enqueue(_timestamp());
            return bucket.Failures.Count >= _failuresPerMinute;
        }
    }

    private Bucket? FindOrCreate(IPAddress ip)
    {
        long now = _timestamp();
        string key = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        if (_buckets.TryGetValue(key, out var bucket)) { bucket.LastSeen = now; return bucket; }
        if (_buckets.Count >= _maxIps)
        {
            foreach (var old in _buckets.Where(p => now - p.Value.LastSeen >= Window).Select(p => p.Key).ToList())
                _buckets.Remove(old);
            if (_buckets.Count >= _maxIps) return null; // fail closed; never evict an active penalty
        }
        bucket = new Bucket { LastSeen = now };
        _buckets[key] = bucket;
        return bucket;
    }

    private void Prune(Bucket bucket)
    {
        long now = _timestamp();
        static void Trim(Queue<long> queue, long now)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
        }
        Trim(bucket.Connections, now);
        Trim(bucket.Failures, now);
        bucket.LastSeen = now;
    }
}
