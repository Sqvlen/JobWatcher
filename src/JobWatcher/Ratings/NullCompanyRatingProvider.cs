using JobWatcher.Domain;

namespace JobWatcher.Ratings;

/// <summary>Used while no lawful rating source is connected: messages simply carry no rating line.</summary>
public sealed class NullCompanyRatingProvider : ICompanyRatingProvider
{
    public Task<CompanyRating?> GetAsync(string company, CancellationToken ct) =>
        Task.FromResult<CompanyRating?>(null);
}
