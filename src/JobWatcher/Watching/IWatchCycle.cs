namespace JobWatcher.Watching;

public interface IWatchCycle
{
    /// <summary>Polls every source once and announces the new matching vacancies.</summary>
    Task RunOnceAsync(CancellationToken ct);
}
