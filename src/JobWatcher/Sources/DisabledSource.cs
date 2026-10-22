using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>Stands in for a source that is not connected: every fetch reports why, so the user is told once.</summary>
public sealed class DisabledSource(VacancySource source, string reason) : IVacancySource
{
    public VacancySource Source => source;

    public Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct) =>
        Task.FromException<IReadOnlyList<Vacancy>>(new SourceNotConnectedException(source, reason));
}
