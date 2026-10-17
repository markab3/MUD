using System.Collections.Concurrent;

namespace MUD.Web.Services;

// Small per-IP cap on failed logins: MaxFailures within Window locks that IP out until the window ends.
public class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public LoginThrottle(TimeProvider? time = null) { _time = time ?? TimeProvider.System; }

    public bool IsBlocked(string ip)
    {
        if (!_failures.TryGetValue(ip, out var list)) { return false; }
        lock (list) { Prune(list); return list.Count >= MaxFailures; }
    }

    public void RecordFailure(string ip)
    {
        var list = _failures.GetOrAdd(ip, _ => new List<DateTimeOffset>());
        lock (list) { Prune(list); list.Add(_time.GetUtcNow()); }
    }

    public void Reset(string ip) => _failures.TryRemove(ip, out _);

    private void Prune(List<DateTimeOffset> list)
    {
        var cutoff = _time.GetUtcNow() - Window;
        list.RemoveAll(t => t <= cutoff);
    }
}
