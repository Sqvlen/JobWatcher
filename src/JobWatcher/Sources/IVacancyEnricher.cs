using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>
/// A source that can read more about one of its vacancies than its list gives (Djinni: the vacancy page). The watch
/// cycle asks it only for new vacancies, one after another, at most <see cref="MaxPerCycle"/> per cycle, before the
/// profile filter.
/// </summary>
public interface IVacancyEnricher
{
    /// <summary>False when the user switched the reading of details off; then nothing is asked for.</summary>
    bool IsEnabled { get; }

    /// <summary>How many new vacancies are enriched per cycle; the rest wait for the next cycle.</summary>
    int MaxPerCycle { get; }

    /// <summary>
    /// A copy of the vacancy with the details filled in that the source could read; every field it could not read
    /// stays as it was. Throws when the details cannot be read.
    /// </summary>
    Task<Vacancy> EnrichAsync(Vacancy vacancy, CancellationToken ct);
}
