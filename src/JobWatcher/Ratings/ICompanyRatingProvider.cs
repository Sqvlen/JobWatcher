using JobWatcher.Domain;

namespace JobWatcher.Ratings;

public interface ICompanyRatingProvider
{
    /// <summary>The company's rating, or null when none is known or the match is not confident.</summary>
    Task<CompanyRating?> GetAsync(string company, CancellationToken ct);
}
