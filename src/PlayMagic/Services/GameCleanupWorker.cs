namespace PlayMagic.Services;

/// <summary>Removes abandoned games and their dependent seats and cards.</summary>
public sealed class GameCleanupWorker(GameService games, ILogger<GameCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var removed = await games.CleanupExpiredAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Removed {GameCount} expired games", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not clean up expired games");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
