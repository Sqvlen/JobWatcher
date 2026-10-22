using JobWatcher.Domain;

namespace JobWatcher.Watching;

/// <summary>
/// Remembers, per source, how many fetches in a row have failed and whether the outage has been announced, so an
/// alert goes out once per outage and not on every cycle. An alert counts as announced only once the caller
/// confirms its delivery (<see cref="MarkAlerted"/>, <see cref="MarkNotConnectedAnnounced"/>): a message that could
/// not be delivered is offered again on the next cycle. State lives in memory only. Not thread-safe: the single
/// polling loop is its only user.
/// </summary>
public sealed class SourceHealth(int failureThreshold)
{
    private readonly Dictionary<VacancySource, int> _consecutiveFailures = [];
    private readonly HashSet<VacancySource> _alerted = [];
    private readonly HashSet<VacancySource> _notConnectedAnnounced = [];

    /// <summary>The number of consecutive failures at which <see cref="RecordFailure"/> starts to report an outage.</summary>
    public int FailureThreshold { get; } = failureThreshold;

    /// <returns>
    /// True while the consecutive failures have reached the threshold and the outage has not yet been confirmed
    /// announced with <see cref="MarkAlerted"/>.
    /// </returns>
    public bool RecordFailure(VacancySource source)
    {
        var failures = _consecutiveFailures.GetValueOrDefault(source) + 1;
        _consecutiveFailures[source] = failures;

        return failures >= FailureThreshold && !_alerted.Contains(source);
    }

    /// <summary>Confirms that the alert for the source's current outage was delivered.</summary>
    public void MarkAlerted(VacancySource source) => _alerted.Add(source);

    /// <summary>Ends the outage: the failure streak and the alerted flag are cleared, so the next outage alerts again.</summary>
    public void RecordSuccess(VacancySource source)
    {
        _consecutiveFailures.Remove(source);
        _alerted.Remove(source);
    }

    /// <returns>True until <see cref="MarkNotConnectedAnnounced"/> confirms the notice was delivered.</returns>
    public bool ShouldAnnounceNotConnected(VacancySource source) => !_notConnectedAnnounced.Contains(source);

    /// <summary>Confirms that the not-connected notice for the source was delivered; it is not offered again in this process.</summary>
    public void MarkNotConnectedAnnounced(VacancySource source) => _notConnectedAnnounced.Add(source);
}
