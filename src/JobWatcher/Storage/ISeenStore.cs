using JobWatcher.Domain;

namespace JobWatcher.Storage;

/// <summary>
/// Remembers which vacancies have already been seen, per source, so none is announced twice.
/// </summary>
public interface ISeenStore
{
    /// <summary>True when at least one vacancy of the source has been recorded (false on the first run).</summary>
    Task<bool> HasAnyAsync(VacancySource source, CancellationToken ct);

    Task<bool> ContainsAsync(VacancySource source, string sourceId, CancellationToken ct);

    /// <summary>Records the vacancies as seen. Ids that are already recorded are ignored.</summary>
    Task AddAsync(VacancySource source, IEnumerable<string> sourceIds, CancellationToken ct);
}
