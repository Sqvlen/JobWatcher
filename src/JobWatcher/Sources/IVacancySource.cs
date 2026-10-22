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
    /// Fetches the vacancies the source currently lists. Throws <see cref="SourceNotConnectedException"/> when
    /// the source is deliberately not connected; any other exception means the fetch failed.
    /// </summary>
    Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct);
}
