using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Sources;

/// <summary>
/// Reads the public vacancy list of ELEKS (careers.eleks.com, see docs/sources/eleks.md) once a day, and for each new
/// vacancy the text of its public page.
/// </summary>
/// <param name="time">The clock of the fetch interval and of the gaps between requests; the system clock unless a test supplies its own.</param>
/// <param name="log">Where a call that makes no request says so; nothing is logged when it is null.</param>
/// <remarks>
/// The list is the one the address <see cref="EleksOptions.ListUrl"/> shows, filters included: Eleks itself decides
/// which vacancies are .NET and which are in Ukraine or remote, so this class neither checks the place again nor looks
/// at cities. A card of the list has only a title, the places (a country and cities, or "Remote") and the technology
/// tags: no pay, no years of experience, no work mode beyond "Remote" among the places. The slug in the vacancy's link
/// is its stable id. The tags are kept as the description (until the page is read), so that a vacancy whose title does
/// not name the technology still does, as far as the profile filter is concerned.
/// A real fetch happens at most once per <see cref="EleksOptions.MinFetchIntervalMinutes"/> after the last good one,
/// to be easy on the site. A call inside the interval makes no request and returns an empty list; a fetch that failed
/// does not start the interval, so the next cycle tries again (there is no quota to protect, and a day without
/// vacancies because of a passing error would be the worse outcome). The interval is remembered in memory only, so a
/// restart of the process fetches again; the seen-vacancies store keeps that from announcing anything twice.
/// Every request (a list page or a vacancy page) comes at least <see cref="EleksOptions.PageRequestDelaySeconds"/>
/// after the previous one, is an anonymous GET with an honest User-Agent, and goes to careers.eleks.com only; the
/// registered client keeps no cookies and follows no redirects.
/// Not thread-safe: the single polling loop is its only caller.
/// </remarks>
public sealed partial class EleksSource(
    HttpClient http,
    IOptions<WatcherOptions> options,
    TimeProvider? time = null,
    ILogger<EleksSource>? log = null) : IVacancySource, IVacancyEnricher
{
    /// <summary>The employer of every vacancy of the site.</summary>
    public const string Company = "ELEKS";

    /// <summary>The most of a page that is read; a real page is about 650 KB.</summary>
    internal const int MaxPageBytes = 3 * 1024 * 1024;

    // A longer list is cut here (and says so in the log): 18 vacancies a page, so 90 vacancies, far beyond what a
    // filter on one technology and place returns.
    private const int MaxListPages = 5;

    private const string UserAgent =
        "JobWatcher/1.0 (personal job alerts; reads the public vacancy list and the pages of new vacancies of careers.eleks.com)";

    private const string RemotePlace = "Remote";

    // When the last request began (a monotonic timestamp); null until the first one.
    private long? lastRequest;

    // When the last good fetch of the list ended (a monotonic timestamp); null until the first one.
    private long? lastGoodFetch;

    // When the interval of the last good fetch ends, in UTC, for the message of a call inside the interval.
    private DateTimeOffset nextFetchDue;

    public VacancySource Source => VacancySource.Eleks;

    // The list is narrowed to what the user wants by Eleks itself (the filters in the address), so nothing is held back
    // at the first start; and while that list is empty, the first vacancy to appear is announced, not swallowed as a baseline.
    public bool AnnouncesExistingOnFirstRun => true;

    public bool IsEnabled => options.Value.Sources.Eleks.ReadVacancyPages;

    public int MaxPerCycle => options.Value.Sources.Eleks.MaxPagesPerCycle;

    private TimeProvider Clock => time ?? TimeProvider.System;

    public async Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct)
    {
        var eleks = options.Value.Sources.Eleks;
        var interval = TimeSpan.FromMinutes(eleks.MinFetchIntervalMinutes);
        if (lastGoodFetch is { } done && Clock.GetElapsedTime(done) < interval)
        {
            // The empty list looks like "no vacancies" in the watch cycle's summary; this line says why.
            log?.LogInformation(
                "Eleks: no request this time, the next one is due after {NextFetch:O} (MinFetchIntervalMinutes={Minutes})",
                nextFetchDue.UtcDateTime,
                eleks.MinFetchIntervalMinutes);

            return [];
        }

        var vacancies = await ReadListAsync(new Uri(eleks.ListUrl), ct);
        lastGoodFetch = Clock.GetTimestamp();
        nextFetchDue = Clock.GetUtcNow() + interval;

        return vacancies;
    }

    /// <summary>
    /// Reads the vacancy's public page and fills in what its text states: the description (from which the requirements
    /// are taken) and, from it, the years of experience asked for. Throws, naming the vacancy, when the link is not an
    /// https vacancy page on careers.eleks.com (no request is made then), on an HTTP error, on a page larger than
    /// <see cref="MaxPageBytes"/>, and when the page has no vacancy text.
    /// </summary>
    public async Task<Vacancy> EnrichAsync(Vacancy vacancy, CancellationToken ct)
    {
        // The link comes from the list: only the site's own vacancy pages are ever requested.
        if (!Uri.TryCreate(vacancy.Url, UriKind.Absolute, out var page) || !IsVacancyLink(page, out _))
        {
            throw new InvalidDataException(
                $"The link of Eleks vacancy {vacancy.SourceId} is not an https vacancy page on {EleksOptions.Host}, so it is not requested.");
        }

        var html = await GetHtmlAsync(page, $"the page of vacancy {vacancy.SourceId}", ct);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Only the vacancy text: the page also has the recruiter's contacts, the menu and the forms.
        if (document.DocumentNode.SelectSingleNode(Descendant("div", "vacancy__text")) is not { } text
            || string.IsNullOrWhiteSpace(text.InnerText))
        {
            throw new InvalidDataException(
                $"The Eleks page of vacancy {vacancy.SourceId} has no vacancy text; the page markup has probably changed.");
        }

        var description = DescriptionOf(text.InnerHtml, vacancy.Description);

        return vacancy with
        {
            MinYearsExperience = SourceMapping.MinYearsExperience(vacancy.Title, description),
            Description = description,
        };
    }

    // The text of the page, then the technology tags of the list as one more line, which the profile filter reads too.
    private static string DescriptionOf(string pageHtml, string? technologies) =>
        string.IsNullOrWhiteSpace(technologies) ? pageHtml : $"{pageHtml}\n<p>{WebUtility.HtmlEncode(technologies)}</p>";

    private async Task<IReadOnlyList<Vacancy>> ReadListAsync(Uri listUrl, CancellationToken ct)
    {
        var first = await ReadListPageAsync(listUrl, ct);
        var vacancies = new List<Vacancy>(first.Vacancies);

        if (first.Pages > MaxListPages)
        {
            log?.LogWarning("The Eleks list has {Pages} pages; only the first {Max} are read", first.Pages, MaxListPages);
        }

        for (var number = 2; number <= Math.Min(first.Pages, MaxListPages); number++)
        {
            vacancies.AddRange((await ReadListPageAsync(PageUrl(listUrl, number), ct)).Vacancies);
        }

        return vacancies.DistinctBy(vacancy => vacancy.SourceId).ToList();
    }

    // The address the site redirects "?paged=N" to; asking for it directly saves the redirect, which the client does not follow.
    private static Uri PageUrl(Uri listUrl, int number) =>
        new UriBuilder(listUrl) { Path = listUrl.AbsolutePath.TrimEnd('/') + "/page/" + number + "/" }.Uri;

    private async Task<ListPage> ReadListPageAsync(Uri url, CancellationToken ct)
    {
        var html = await GetHtmlAsync(url, "the vacancy list", ct);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        // An empty list keeps the container (with a "no result" block), so a missing container is no empty list but
        // some other page: an anti-bot page, an error page or a redesign.
        if (document.DocumentNode.SelectSingleNode(Descendant("div", "vacancies-list")) is not { } list)
        {
            throw new InvalidDataException(
                "The Eleks page has no vacancy list; the site's page markup has probably changed (or the site answered with something else).");
        }

        var cards = list.SelectNodes(Descendant("a", "vacancy-item"))?.ToList() ?? [];
        var vacancies = cards.Select(card => Map(card, url)).OfType<Vacancy>().DistinctBy(vacancy => vacancy.SourceId).ToList();
        if (cards.Count > 0 && vacancies.Count == 0)
        {
            throw new InvalidDataException(
                $"None of the {cards.Count} cards of the Eleks list could be read; the page markup has probably changed.");
        }

        return new ListPage(vacancies, PageCount(document));
    }

    // The highest number among the links of the pager and its current page; 1 when there is no pager. Only the links and
    // the current page count: the list around them (also a "page-numbers") has the numbers run together as its text.
    private static int PageCount(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes($"{Descendant("a", "page-numbers")} | {Descendant("span", "page-numbers")}")
            ?.Select(node => int.TryParse(node.InnerText.Trim(), out var number) ? number : 1) ?? [])
        .Append(1)
        .Max();

    // Null when the card is unusable: without a title it cannot be shown, and a link that is not a vacancy page of the
    // site is not a vacancy of it.
    private static Vacancy? Map(HtmlNode card, Uri listUrl)
    {
        if (!Uri.TryCreate(listUrl, card.GetAttributeValue("href", ""), out var link)
            || !IsVacancyLink(link, out var slug)
            || TextOf(card, "vacancy-item__title") is not { } title)
        {
            return null;
        }

        var location = TextOf(card, "vacancy-item__location");
        var technologies = TextOf(card, "vacancy-item__technology");

        return new Vacancy(
            VacancySource.Eleks,
            slug,
            title,
            Company,
            link.AbsoluteUri,
            Salary: null,
            location,
            IsRemote(location) ? WorkMode.Remote : WorkMode.Unknown,
            RemoteRestrictedTo: null,
            SourceMapping.MinYearsExperience(title, technologies),
            SourceMapping.IsJuniorTitle(title),
            technologies);
    }

    // "Remote" is one of the places ("Poland, Remote, Croatia, Europe"), not a word inside one.
    private static bool IsRemote(string? location) =>
        location is not null
        && location.Split(',').Any(place => place.Trim().Equals(RemotePlace, StringComparison.OrdinalIgnoreCase));

    // https://careers.eleks.com/vacancies/<slug>/ and nothing else, without user information in the link.
    private static bool IsVacancyLink(Uri uri, out string slug)
    {
        slug = "";
        if (uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || !uri.Host.Equals(EleksOptions.Host, StringComparison.OrdinalIgnoreCase)
            || VacancyPath().Match(uri.AbsolutePath) is not { Success: true } match)
        {
            return false;
        }

        slug = match.Groups["slug"].Value;

        return true;
    }

    // The element's text with entities decoded and whitespace collapsed; null when it is absent or blank.
    private static string? TextOf(HtmlNode card, string cssClass)
    {
        var text = card.SelectSingleNode(Descendant("div", cssClass)) is { } node
            ? Whitespace().Replace(HtmlEntity.DeEntitize(node.InnerText), " ").Trim()
            : "";

        return text.Length > 0 ? text : null;
    }

    private static string Descendant(string tag, string cssClass) =>
        $".//{tag}[contains(concat(' ', normalize-space(@class), ' '), ' {cssClass} ')]";

    // A request comes at least PageRequestDelaySeconds after the start of the last one (a failed request counts too).
    private async Task WaitForTurnAsync(CancellationToken ct)
    {
        if (lastRequest is { } last)
        {
            var gap = TimeSpan.FromSeconds(options.Value.Sources.Eleks.PageRequestDelaySeconds);
            var remaining = gap - Clock.GetElapsedTime(last);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, Clock, ct);
            }
        }

        lastRequest = Clock.GetTimestamp();
    }

    // A plain anonymous GET. The client's timeout also covers reading the body, which ResponseHeadersRead leaves out.
    private async Task<string> GetHtmlAsync(Uri page, string what, CancellationToken ct)
    {
        await WaitForTurnAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(http.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Eleks answered HTTP {(int)response.StatusCode} ({response.StatusCode}) for {what}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxPageBytes)
        {
            throw TooLarge(what);
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > MaxPageBytes)
            {
                throw TooLarge(what);
            }

            buffer.Write(chunk, 0, read);
        }

        // The site serves its pages as UTF-8.
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static InvalidDataException TooLarge(string what) =>
        new($"Eleks: {what} is larger than {MaxPageBytes / (1024 * 1024)} MB, so it is not read.");

    [GeneratedRegex(@"^/vacancies/(?<slug>[^/]+)/$")]
    private static partial Regex VacancyPath();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record ListPage(IReadOnlyList<Vacancy> Vacancies, int Pages);
}
