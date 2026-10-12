using System.Collections.Concurrent;

namespace DanLoopServer.Services;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public JobCancellationLease Register(Guid jobId, CancellationToken applicationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        if (!_running.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Job {jobId} is already registered as running.");
        }
        return new JobCancellationLease(jobId, source, this);
    }

    public bool Cancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var source)) return false;
        source.Cancel();
        return true;
    }

    private void Release(Guid jobId, CancellationTokenSource source)
    {
        _running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(jobId, source));
        source.Dispose();
    }

    public sealed class JobCancellationLease(Guid jobId, CancellationTokenSource source, JobCancellationRegistry owner) : IDisposable
    {
        public CancellationToken Token => source.Token;
        public void Dispose() => owner.Release(jobId, source);
    }
}
