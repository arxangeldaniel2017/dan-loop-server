using System.Collections.Concurrent;

namespace DanLoopServer.Services;

public sealed class LoginSecurityService
{
    public const int ChallengeThreshold = 3;
    public const int AccountLockoutThreshold = 8;
    public static readonly TimeSpan AccountLockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TrackingWindow = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldChallenge(string ipAddress, string? username = null) =>
        Count(Key("ip", ipAddress)) >= ChallengeThreshold ||
        (!string.IsNullOrWhiteSpace(username) && Count(Key("user", Normalize(username))) >= ChallengeThreshold);

    public int RegisterFailure(string ipAddress, string username)
    {
        var ipCount = Increment(Key("ip", ipAddress));
        var userCount = Increment(Key("user", Normalize(username)));
        return Math.Max(ipCount, userCount);
    }

    public void RegisterSuccess(string ipAddress, string username)
    {
        _attempts.TryRemove(Key("ip", ipAddress), out _);
        _attempts.TryRemove(Key("user", Normalize(username)), out _);
    }

    public Task ApplyProgressiveDelayAsync(int failures, CancellationToken cancellationToken)
    {
        if (failures <= 1) return Task.CompletedTask;
        var milliseconds = Math.Min(5_000, 250 * (1 << Math.Min(failures - 1, 4)));
        return Task.Delay(milliseconds, cancellationToken);
    }

    private int Count(string key)
    {
        if (!_attempts.TryGetValue(key, out var state)) return 0;
        if (DateTimeOffset.UtcNow - state.WindowStartedAt <= TrackingWindow) return state.Count;
        _attempts.TryRemove(key, out _);
        return 0;
    }

    private int Increment(string key)
    {
        var now = DateTimeOffset.UtcNow;
        return _attempts.AddOrUpdate(key, _ => new AttemptState(1, now), (_, current) =>
            now - current.WindowStartedAt > TrackingWindow ? new AttemptState(1, now) : current with { Count = current.Count + 1 }).Count;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string Key(string kind, string value) => $"{kind}:{value}";
    private sealed record AttemptState(int Count, DateTimeOffset WindowStartedAt);
}
