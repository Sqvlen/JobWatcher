using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Parsing;

namespace JobWatcher.Filtering;

/// <summary>
/// Decides whether a vacancy fits the search profile. A vacancy is rejected only when the source
/// explicitly says it does not fit; missing information never rejects it.
/// </summary>
public sealed class VacancyFilter(ProfileOptions profile)
{
    public bool Matches(Vacancy v) =>
        MentionsTechnology(v) && MeetsExperience(v) && MatchesLocation(v) && MeetsMinSalary(v);

    private bool MentionsTechnology(Vacancy v) =>
        ContainsTerm(v.Title, profile.Keyword)
        || ContainsTerm(v.Description, profile.Keyword)
        || profile.Synonyms.Any(synonym =>
            ContainsTerm(v.Title, synonym) || ContainsTerm(v.Description, synonym));

    private bool MeetsExperience(Vacancy v) =>
        !v.IsJunior && (v.MinYearsExperience is not { } years || years >= profile.MinYearsExperience);

    private bool MatchesLocation(Vacancy v) =>
        v.WorkMode == WorkMode.Remote ? IsRemoteOpenToCandidate(v) : IsInTargetCity(v);

    private bool IsRemoteOpenToCandidate(Vacancy v) =>
        string.IsNullOrWhiteSpace(v.RemoteRestrictedTo)
        || profile.AllowedRemoteRegions.Contains(v.RemoteRestrictedTo.Trim(), StringComparer.OrdinalIgnoreCase);

    private bool IsInTargetCity(Vacancy v) =>
        string.IsNullOrWhiteSpace(v.Location)
        || profile.Cities.Any(city => ContainsIgnoringCase(v.Location, city));

    // Rejected only when the highest stated amount is clearly below the minimum. Anything that cannot be
    // compared as it stands (no salary, other currency, hourly or unknown period, "from X") passes.
    private bool MeetsMinSalary(Vacancy v)
    {
        if (profile.MinSalary is not { } minSalary || SalaryParser.Parse(v.Salary) is not { } salary)
        {
            return true;
        }

        if (!string.Equals(salary.Currency, minSalary.Currency, StringComparison.OrdinalIgnoreCase)
            || salary.Max is not { } highest
            || salary.Period is not (SalaryPeriod.Month or SalaryPeriod.Year))
        {
            return true;
        }

        return PerMonth(highest, salary.Period) >= PerMonth(minSalary.Amount, minSalary.Period);
    }

    private static decimal PerMonth(decimal amount, SalaryPeriod period) =>
        period == SalaryPeriod.Year ? amount / 12 : amount;

    private static bool ContainsIgnoringCase(string? text, string term) =>
        text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);

    // Like ContainsIgnoringCase, but the term must not run on into a longer word: ".NET8" and ".NET Core"
    // match, "acme.network" and "jobs.netflix.com" do not.
    private static bool ContainsTerm(string? text, string term)
    {
        if (text is null)
        {
            return false;
        }

        var from = 0;
        while (from <= text.Length)
        {
            var index = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + term.Length;
            if (end == text.Length || !char.IsLetter(text[end]))
            {
                return true;
            }

            from = index + 1;
        }

        return false;
    }
}
