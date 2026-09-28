namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeJobRecoveryService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumRecoveryAge = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<UpgradeJobRecoveryService> logger;

    public UpgradeJobRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<UpgradeJobRecoveryService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileActiveJobAsync(stoppingToken);

            try
            {
                await Task.Delay(ReconciliationInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReconcileActiveJobAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobStore = scope.ServiceProvider.GetRequiredService<IUpgradeJobStore>();
            if (!jobStore.TryGetLatestActive(out var activeJob))
            {
                logger.LogDebug("No active upgrade job was found during recovery reconciliation.");
                return;
            }

            logger.LogWarning(
                "Active upgrade job found during recovery reconciliation. JobId: {JobId}. Stage: {Stage}. Status: {Status}.",
                activeJob.Id,
                activeJob.CurrentStage,
                activeJob.Status);

            if (DateTimeOffset.UtcNow - activeJob.StartedAt < MinimumRecoveryAge)
            {
                logger.LogDebug(
                    "Recovery reconciliation skipped because the active job was started recently. JobId: {JobId}.",
                    activeJob.Id);
                return;
            }

            var executionRegistry = scope.ServiceProvider.GetRequiredService<IUpgradeJobExecutionRegistry>();
            if (executionRegistry.IsActive(activeJob.Id))
            {
                logger.LogDebug(
                    "Recovery reconciliation skipped because the job is currently owned by an active runner. JobId: {JobId}.",
                    activeJob.Id);
                return;
            }

            if (activeJob.UpgradeType == UpgradeJobType.Rollback)
            {
                var rollbackJobRunner = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackJobRunner>();
                rollbackJobRunner.Recover(activeJob.Id);
                await Task.CompletedTask;
                return;
            }

            var jobRunner = scope.ServiceProvider.GetRequiredService<IUpgradeJobRunner>();
            jobRunner.Recover(activeJob.Id);
            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job recovery reconciliation failed.");
        }
    }
}
