namespace DanLoopServer.Services;

public sealed class S3SyncWorker(S3ObjectStorage storage, ILogger<S3SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { if (storage.IsEnabled) await storage.SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "S3-compatible project storage synchronization failed; it will retry automatically"); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (storage.IsEnabled) await storage.SyncAsync(cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "Final S3-compatible project storage synchronization failed"); }
        await base.StopAsync(cancellationToken);
    }
}
