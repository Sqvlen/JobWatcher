using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Options;

namespace JobWatcher.Sources;

/// <summary>
/// Reads the public RSS feed that Djinni offers for a vacancy search and, for each new vacancy, the schema.org
/// JobPosting data of its public page (see docs/sources/djinni.md).
/// </summary>
/// <param name="time">The clock of the gap between page requests; the system clock unless a test supplies its own.</param>
/// <remarks>
/// A feed item carries only a title, a link, an HTML description, a date and a category (the search keyword). It has
/// no company, no salary, no place and no work model, and none of them is guessed from the free text of the title or
/// the description: <see cref="Vacancy.Salary"/> and <see cref="Vacancy.Location"/> are null, the work mode is
/// unknown, and the company is the visible <see cref="UnknownCompany"/> text (the shared
/// <see cref="Placeholders.NotSpecified"/>) because the watch cycle drops a vacancy whose company is blank; the cycle
/// never asks for a rating of that text. The vacancy number in the link is the stable id.
/// The watch cycle then asks <see cref="EnrichAsync"/> for the details of each new vacancy: the anonymous public
/// vacancy page is requested (only an https link on djinni.co, at least <see cref="DjinniOptions.PageRequestDelaySeconds"/>
/// after the previous request, no cookies or credentials, no redirects followed: the registered client is set up so)
/// and only its JSON-LD block is read (<see cref="JobPostingPage"/>).
/// Not thread-safe: the single polling loop is its only caller.
/// </remarks>
public sealed partial class DjinniSource(HttpClient http, IOptions<WatcherOptions> options, TimeProvider? time = null)
    : IVacancySource, IVacancyEnricher
{
    /// <summary>The company of a Djinni vacancy whose page was not read: the feed does not say who posted it.</summary>
    public const string UnknownCompany = Placeholders.NotSpecified;

    /// <summary>The most of a vacancy page that is read; a real page is about 50 KB.</summary>
    internal const int MaxPageBytes = 3 * 1024 * 1024;

    // Sent with the feed request and with every page request alike, and says that it reads both.
    private const string UserAgent =
        "JobWatcher/1.0 (personal job alerts; reads the public Djinni RSS feed and the pages of new vacancies)";

    private const string DjinniHost = "djinni.co";

    // When the last request to Djinni (the feed or a page) began (a monotonic timestamp); null until the first one.
    private long? lastRequest;

    public VacancySource Source => VacancySource.Djinni;

    public bool IsEnabled => options.Value.Sources.Djinni.ReadVacancyPages;

    public int MaxPerCycle => options.Value.Sources.Djinni.MaxPagesPerCycle;

    /// <summary>Waits out the gap before a page request; replaceable only so tests do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    private TimeProvider Clock => time ?? TimeProvider.System;

    public Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct)
    {
        // The feed request counts for the gap too, so the first page request of a cycle does not follow it at once.
        lastRequest = Clock.GetTimestamp();

        return RssFeed.FetchAsync(http, options.Value.Sources.Djinni.FeedUrl, UserAgent, Source.DisplayName(), Map, ct);
    }

    /// <summary>
    /// Reads the vacancy's public page and fills in what its JobPosting data states. Throws, naming the vacancy, when
    /// the link is not an https page on djinni.co (no request is made then), on an HTTP error, on a page larger than
    /// <see cref="MaxPageBytes"/>, and when the page has no readable JobPosting.
    /// </summary>
    public async Task<Vacancy> EnrichAsync(Vacancy vacancy, CancellationToken ct)
    {
        // The link comes from the feed: only Djinni's own pages are ever requested.
        if (!IsDjinniPage(vacancy.Url, out var page))
        {
            throw new InvalidDataException(
                $"The link of Djinni vacancy {vacancy.SourceId} is not an https page on {DjinniHost}, so it is not requested.");
        }

        await WaitForTurnAsync(ct);
        var html = await ReadPageAsync(page, vacancy.SourceId, ct);

        return JobPostingPage.Enrich(vacancy, html);
    }

    // A page request comes at least PageRequestDelaySeconds after the start of the last request to Djinni, the feed or
    // a page (a failed request counts too).
    private async Task WaitForTurnAsync(CancellationToken ct)
    {
        if (lastRequest is { } last)
        {
            var gap = TimeSpan.FromSeconds(options.Value.Sources.Djinni.PageRequestDelaySeconds);
            var remaining = gap - Clock.GetElapsedTime(last);
            if (remaining > TimeSpan.Zero)
            {
                await (Delay?.Invoke(remaining, ct) ?? Task.Delay(remaining, Clock, ct));
            }
        }

        lastRequest = Clock.GetTimestamp();
    }

    // A plain anonymous GET. The client's timeout also covers reading the body, which ResponseHeadersRead leaves out.
    private async Task<string> ReadPageAsync(Uri page, string sourceId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(http.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Djinni answered HTTP {(int)response.StatusCode} ({response.StatusCode}) for the page of vacancy {sourceId}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxPageBytes)
        {
            throw TooLarge(sourceId);
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > MaxPageBytes)
            {
                throw TooLarge(sourceId);
            }

            buffer.Write(chunk, 0, read);
        }

        // Djinni serves its pages as UTF-8.
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static InvalidDataException TooLarge(string sourceId) =>
        new($"The page of Djinni vacancy {sourceId} is larger than {MaxPageBytes / (1024 * 1024)} MB, so it is not read.");

    // https only, on djinni.co or one of its subdomains, and without user information in the link.
    private static bool IsDjinniPage(string url, out Uri page)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || !(uri.Host.Equals(DjinniHost, StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith("." + DjinniHost, StringComparison.OrdinalIgnoreCase)))
        {
            page = null!;

            return false;
        }

        page = uri;

        return true;
    }

    // Null when the item is unusable: without a vacancy number or a title the vacancy cannot be told apart or shown.
    private static Vacancy? Map(XElement item)
    {
        var link = RssFeed.TextOf(item, "link");
        var title = RssFeed.TextOf(item, "title");
        if (link is null || title is null || VacancyLink().Match(link) is not { Success: true } id)
        {
            return null;
        }

        var description = RssFeed.DescriptionOf(item);

        return new Vacancy(
            VacancySource.Djinni,
            id.Groups["id"].Value,
            title,
            UnknownCompany,
            link,
            Salary: null,
            Location: null,
            WorkMode.Unknown,
            RemoteRestrictedTo: null,
            SourceMapping.MinYearsExperience(title, description),
            SourceMapping.IsJuniorTitle(title),
            description);
    }

    // "https://djinni.co/jobs/847830-net-engineer/": the number is the vacancy, the rest is a slug of its title,
    // which changes when the employer renames the vacancy. Other pages under /jobs/ have no number.
    [GeneratedRegex(@"/jobs/(?<id>\d+)(?:[-/?#]|$)")]
    private static partial Regex VacancyLink();
}
