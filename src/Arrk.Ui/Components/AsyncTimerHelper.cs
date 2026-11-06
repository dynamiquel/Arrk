namespace Arrk.Ui.Components;

/// <summary>
/// Wraps a System.Threading.Timer with a SemaphoreSlim to prevent concurrent executions
/// of the same async work. This avoids fire-and-forget tasks piling up.
///
/// Usage:
///   _refreshTimer = AsyncTimerHelper.Create(
///       async ct => { await DoWork(ct); await InvokeAsync(StateHasChanged); },
///       initialDelay, interval);
///
/// Always dispose via the returned CancellationTokenSource.
/// </summary>
public static class AsyncTimerHelper
{
    /// <summary>
    /// Creates a Timer that executes the callback with semaphore protection.
    /// The callback receives a CancellationToken that fires on dispose.
    /// </summary>
    public static (Timer Timer, CancellationTokenSource Cts) Create(
        Func<CancellationToken, Task> callback,
        TimeSpan initialDelay,
        TimeSpan interval)
    {
        CancellationTokenSource cts = new();
        SemaphoreSlim semaphore = new(1, 1);

        Timer timer = new(async _ =>
        {
            if (cts.IsCancellationRequested)
                return;

            if (!await semaphore.WaitAsync(0))
                return;

            try
            {
                await callback(cts.Token);
            }
            catch
            {
            }
            finally
            {
                semaphore.Release();
            }
        }, null, initialDelay, interval);

        return (timer, cts);
    }
}
