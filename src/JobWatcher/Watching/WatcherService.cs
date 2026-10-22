using JobWatcher.Configuration;
using JobWatcher.Formatting;
using JobWatcher.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Watching;

/// <summary>
/// Runs the watch cycle at once on start and then once per poll interval until the host stops.
/// </summary>
/// <remarks>
/// A cycle that fails is logged and the next one runs on schedule: the cycle is built to leave anything it could
/// not finish unseen, so a later cycle repeats it. When <c>FailureAlertThreshold</c> cycles in a row have failed
/// (for example because the seen-vacancies database cannot be written), the user is told once in Telegram; a
/// successful cycle ends the streak. Cycles never overlap; the next tick is awaited only after the
/// current cycle has finished, and ticks missed meanwhile are not queued.
/// </remarks>
public sealed class WatcherService : BackgroundService
{
    private readonly IWatchCycle _cycle;
    private readonly TimeSpan _interval;
    private readonly int _failureAlertThreshold;
    private readonly IMessageNotifier _notifier;
    private readonly ILogger<WatcherService> _log;

    // Cycles failed in a row, and whether the user has been told about this streak (set only once delivered).
    private int _failedCycles;
    private bool _alerted;

    public WatcherService(IWatchCycle cycle, IOptions<WatcherOptions> options, IMessageNotifier notifier, ILogger<WatcherService> log)
        : this(cycle, TimeSpan.FromMinutes(options.Value.PollIntervalMinutes), options.Value.FailureAlertThreshold, notifier, log)
    {
    }

    internal WatcherService(
        IWatchCycle cycle,
        TimeSpan interval,
        int failureAlertThreshold,
        IMessageNotifier notifier,
        ILogger<WatcherService> log)
    {
        _cycle = cycle;
        _interval = interval;
        _failureAlertThreshold = failureAlertThreshold;
        _notifier = notifier;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping is the normal way out of the loop.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _cycle.RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes a cancellation that did not come from stopping, such as an HTTP timeout.
            _log.LogError(ex, "Watch cycle failed; the next cycle runs on schedule");
            _failedCycles++;
            if (_failedCycles >= _failureAlertThreshold && !_alerted)
            {
                await TryAlertAsync(ex, stoppingToken);
            }

            return;
        }

        if (_failedCycles > 0)
        {
            _log.LogInformation("Watch cycle succeeded again after {Count} failed cycles", _failedCycles);
        }

        _failedCycles = 0;
        _alerted = false;
    }

    // Never throws but for stopping: an alert that is not delivered is offered again after the next failed cycle.
    private async Task TryAlertAsync(Exception failure, CancellationToken stoppingToken)
    {
        try
        {
            await _notifier.SendAsync(CycleFailingAlert(_failedCycles, failure), stoppingToken);
            _alerted = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramDeliveryException ex)
        {
            _log.LogWarning("Telegram did not accept the alert about failing watch cycles: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            // Only the type: the text of an unexpected exception of the HTTP stack may hold the request URL.
            _log.LogWarning("Sending the alert about failing watch cycles failed with {ExceptionType}", ex.GetType().Name);
        }
    }

    // Only the exception's type: its message may hold a path, a URL or another detail that must not leave the server.
    private static string CycleFailingAlert(int failures, Exception failure) =>
        $"⚠️ Цикл перевірки не вдається ({failures} разів поспіль): {MessageFormatter.EscapeHtml(failure.GetType().Name)}. "
        + "Подробиці в журналі сервісу.";
}
