using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Options;

namespace JobWatcher.Sources;

/// <summary>
/// Reads the public RSS feed that DOU offers for a vacancy category (see docs/sources/dou.md).
/// </summary>
/// <remarks>
/// An item carries only a title, a link and an HTML description. DOU puts everything else into the title:
/// "{title} в {company}[, {salary}][, {place}, ...]", where a place is a city, "віддалено" (remote) or
/// "за кордоном" (abroad). The item's guid ends in a timestamp that changes whenever the employer republishes
/// the vacancy, so the vacancy number in the link is the stable id.
/// </remarks>
public sealed partial class DouRssSource(HttpClient http, IOptions<WatcherOptions> options) : IVacancySource
{
    private const string UserAgent = "JobWatcher/1.0 (personal job alerts; reads the public DOU RSS feed)";

    // Splits the vacancy title from the company. The last one wins: employers' titles may contain " в " too.
    private const string CompanySeparator = " в ";

    private const string RemoteMarker = "віддалено";

    public VacancySource Source => VacancySource.Dou;

    public Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct) =>
        RssFeed.FetchAsync(http, options.Value.Sources.Dou.FeedUrl, UserAgent, Source.DisplayName(), Map, ct);

    // Null when the item is unusable: without a vacancy number, a title or a company the vacancy cannot be told apart or shown.
    private static Vacancy? Map(XElement item)
    {
        var link = RssFeed.TextOf(item, "link");

        // DOU escapes the title as HTML on top of the XML escaping ("C# &amp;amp; Node.js").
        var headline = RssFeed.TextOf(item, "title") is { } rawTitle ? WebUtility.HtmlDecode(rawTitle) : null;
        if (link is null || headline is null || VacancyLink().Match(link) is not { Success: true } id)
        {
            return null;
        }

        if (ParseHeadline(headline, id.Groups["slug"].Value) is not { } parsed)
        {
            return null;
        }

        var description = RssFeed.DescriptionOf(item);

        var workMode = parsed.IsRemote ? WorkMode.Remote : parsed.Location is null ? WorkMode.Unknown : WorkMode.Office;

        return new Vacancy(
            VacancySource.Dou,
            id.Groups["id"].Value,
            parsed.Title,
            parsed.Company,
            link,
            parsed.Salary,
            parsed.Location,
            workMode,
            parsed.RemoteRestrictedTo,
            SourceMapping.MinYearsExperience(parsed.Title, description),
            IsJunior(parsed.Title),
            description);
    }

    private static bool IsJunior(string title) => SourceMapping.IsJuniorTitle(title);

    private static Headline? ParseHeadline(string headline, string slug)
    {
        var separator = headline.LastIndexOf(CompanySeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var title = headline[..separator].Trim();
        var parts = headline[(separator + CompanySeparator.Length)..]
            .Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (title.Length == 0 || parts.Length == 0)
        {
            return null;
        }

        var companyParts = CompanyPartCount(parts, slug);
        string? salary = null;
        var isRemote = false;
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var places = new List<string>();
        foreach (var part in parts[companyParts..])
        {
            if (salary is null && IsSalary(part))
            {
                salary = part;
            }
            else if (part.Equals(RemoteMarker, StringComparison.OrdinalIgnoreCase))
            {
                isRemote = true;
            }
            else if (RemoteAnywhere().Match(part) is { Success: true } anywhere)
            {
                isRemote = true;
                AddRegion(regions, anywhere.Groups["region"].Value);
            }
            else
            {
                if (RemoteInPlace().Match(part) is { Success: true } inPlace)
                {
                    isRemote = true;
                    AddRegion(regions, inPlace.Groups["region"].Value);
                }

                places.Add(part);
            }
        }

        return new Headline(
            title,
            string.Join(", ", parts[..companyParts]),
            salary,
            places.Count == 0 ? null : string.Join(", ", places),
            isRemote,
            regions.Count == 1 ? SourceMapping.NormalizeRegion(regions.Single()) : null);
    }

    // A company name may itself contain commas. The link carries the company's slug ("personalitieshr-company"
    // for "Personalities, HR Company"), so the name takes as many parts as it takes to spell the slug out,
    // and legal forms ("Inc.", "Ltd") that follow the name belong to it.
    private static int CompanyPartCount(string[] parts, string slug)
    {
        var count = 1;
        var slugKey = Key(slug);
        if (slugKey.Length > 0 && Key(parts[0]) != slugKey)
        {
            for (var n = 2; n <= parts.Length; n++)
            {
                if (Key(string.Concat(parts[..n])) == slugKey)
                {
                    count = n;
                    break;
                }
            }
        }

        while (count < parts.Length && LegalForm().IsMatch(parts[count]))
        {
            count++;
        }

        return count;
    }

    private static string Key(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsSalary(string part) => part.Any(char.IsDigit) && CurrencyMarker().IsMatch(part);

    private static void AddRegion(HashSet<string> regions, string region)
    {
        if (region.Length > 0)
        {
            regions.Add(region);
        }
    }

    private sealed record Headline(
        string Title,
        string Company,
        string? Salary,
        string? Location,
        bool IsRemote,
        string? RemoteRestrictedTo);

    // "/companies/freitty/vacancies/366693/": the slug names the company, the number is the vacancy.
    [GeneratedRegex(@"(?:/companies/(?<slug>[^/]+))?/vacancies/(?<id>\d+)/")]
    private static partial Regex VacancyLink();

    // "Remote" or "Remote Ukraine": a work mode, not a place.
    [GeneratedRegex(@"^remote(?:\s+(?<region>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteAnywhere();

    // "Poland (remote)": a place where the vacancy can be worked remotely.
    [GeneratedRegex(@"^(?<region>.+?)\s*\(remote\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteInPlace();

    [GeneratedRegex(@"[$€£₴]|грн|(?<!\p{L})(?:usd|eur|gbp|uah|pln)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyMarker();

    [GeneratedRegex(@"^(?:inc|llc|ltd|limited|gmbh|corp|co|plc)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalForm();
}
