using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>One job board the watcher polls for vacancies.</summary>
public interface IVacancySource
{
    /// <summary>
    /// Which job board this is; it keys the seen-vacancy store, and every vacancy the source returns carries it.
    /// Its <see cref="VacancySourceExtensions.DisplayName"/> appears in alerts.
    /// </summary>
    VacancySource Source { get; }

    /// <summary>
    /// True when the source's list is already narrowed to what the user wants (Eleks, by the filters in its address), so
    /// that its first fetch is not remembered silently as a baseline: the vacancies it lists then are announced like any
    /// later ones. That also settles an empty first fetch: the first vacancy to appear after it is announced, not taken
    /// for the baseline. False, the default, for a source that lists everything open on its board, whose existing
    /// vacancies would otherwise flood the user at the first start.
    /// </summary>
    bool AnnouncesExistingOnFirstRun => false;

    /// <summary>
    /// Fetches the vacancies the source currently lists. Throws <see cref="SourceNotConnectedException"/> when
    /// the source is deliberately not connected; any other exception means the fetch failed.
    /// </summary>
    Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct);
}
