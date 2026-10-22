using System.Net;
using System.Text;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Parsing;
using JobWatcher.Sources;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

// The details of a new Djinni vacancy, read from the schema.org JobPosting JSON-LD block of its public page
// (docs/sources/djinni.md, "Читання сторінок вакансій").
public sealed class DjinniVacancyPageTests
{
    private const string FeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";

    // The one User-Agent of the Djinni client, for the feed and the vacancy pages alike; it says what the client reads.
    private const string ExpectedUserAgent =
        "JobWatcher/1.0 (personal job alerts; reads the public Djinni RSS feed and the pages of new vacancies)";

    // Real anonymous vacancy pages of 2026-10-22, trimmed to the <title> and the JSON-LD block (only the long
    // "description" value inside the block is replaced; it is not used).
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string RemoteUkraineSalaryPage = Path.Combine(FixtureDir, "djinni-page-854603-remote-ua-salary.html");
    private static readonly string RemoteEuropePage = Path.Combine(FixtureDir, "djinni-page-851814-remote-europe.html");
    private static readonly string OfficeKyivPage = Path.Combine(FixtureDir, "djinni-page-852123-office-kyiv.html");
    private static readonly string RemoteUkrainePage = Path.Combine(FixtureDir, "djinni-page-852019-remote-ua.html");

    private sealed record Recorded(HttpMethod Method, Uri? Uri, string UserAgent, IReadOnlyList<string> HeaderNames);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Recorded(
                request.Method,
                request.RequestUri,
                request.Headers.UserAgent.ToString(),
                [.. request.Headers.Select(header => header.Key)]));

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage FixturePage(string path) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) };

    private static string PageWith(string jsonLd) =>
        $"<!doctype html><html><head><title>t</title><script type=\"application/ld+json\">{jsonLd}</script></head><body></body></html>";

    private static string JobPosting(string fields) =>
        $$"""{"@context": "https://schema.org/", "@type": "JobPosting", "title": "Dev"{{(fields.Length > 0 ? ", " + fields : "")}}}""";

    private static DjinniSource Source(
        StubHandler handler, ManualTimeProvider? clock = null, List<TimeSpan>? delays = null, int delaySeconds = 2)
    {
        var options = new WatcherOptions { Sources = { Djinni = { FeedUrl = FeedUrl, PageRequestDelaySeconds = delaySeconds } } };
        var fakeClock = clock ?? new ManualTimeProvider();
        var source = new DjinniSource(new HttpClient(handler), Options.Create(options), fakeClock);
        source.Delay = (gap, _) =>
        {
            delays?.Add(gap);
            fakeClock.Advance(gap);

            return Task.CompletedTask;
        };

        return source;
    }

    // A vacancy as the feed gives it: no company, salary, place or work mode; years read from the feed text.
    private static Vacancy FeedVacancy(string id = "854603", string? url = null) => new(
        VacancySource.Djinni,
        id,
        "Middle .net developer",
        DjinniSource.UnknownCompany,
        url ?? $"https://djinni.co/jobs/{id}-middle-net-developer/",
        Salary: null,
        Location: null,
        WorkMode.Unknown,
        RemoteRestrictedTo: null,
        MinYearsExperience: 9,
        IsJunior: false,
        Description: "<p>feed text</p>");

    private static Task<Vacancy> EnrichFromAsync(string pagePath, string id) =>
        Source(new StubHandler(_ => FixturePage(pagePath))).EnrichAsync(FeedVacancy(id), CancellationToken.None);

    private static Task<Vacancy> EnrichWithJsonAsync(string jsonLd, Vacancy? vacancy = null) =>
        Source(new StubHandler(_ => Html(PageWith(jsonLd)))).EnrichAsync(vacancy ?? FeedVacancy(), CancellationToken.None);

    // ---- the four real pages ----

    [Fact]
    public async Task Remote_page_for_Ukraine_with_a_salary_fills_every_field()
    {
        var feed = FeedVacancy("854603");

        var vacancy = await Source(new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage))).EnrichAsync(feed, CancellationToken.None);

        // The first letter is a Cyrillic І, kept as the page gives it.
        Assert.Equal("Іnfinitytechnologies", vacancy.Company);
        Assert.Equal("$2000–2500", vacancy.Salary);
        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Null(vacancy.Location);
        Assert.Equal("Ukraine", vacancy.RemoteRestrictedTo);
        Assert.Equal(3, vacancy.MinYearsExperience);

        // Everything else stays as the feed gave it.
        Assert.Equal(feed with
        {
            Company = vacancy.Company,
            Salary = vacancy.Salary,
            WorkMode = vacancy.WorkMode,
            RemoteRestrictedTo = vacancy.RemoteRestrictedTo,
            MinYearsExperience = vacancy.MinYearsExperience,
        }, vacancy);
    }

    [Fact]
    public async Task Remote_page_for_Europe_without_a_salary()
    {
        var vacancy = await EnrichFromAsync(RemoteEuropePage, "851814");

        Assert.Equal("Bliscore", vacancy.Company);
        Assert.Null(vacancy.Salary);
        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Null(vacancy.Location);
        Assert.Equal("Europe", vacancy.RemoteRestrictedTo);
        Assert.Equal(4, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task Office_page_in_Kyiv()
    {
        var vacancy = await EnrichFromAsync(OfficeKyivPage, "852123");

        Assert.Equal("GoSwift Україна", vacancy.Company);
        Assert.Null(vacancy.Salary);
        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
        Assert.Equal("Київ", vacancy.Location);
        Assert.Null(vacancy.RemoteRestrictedTo);
        Assert.Equal(6, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task Remote_page_for_Ukraine_without_a_salary()
    {
        var vacancy = await EnrichFromAsync(RemoteUkrainePage, "852019");

        Assert.Equal("Bliscore", vacancy.Company);
        Assert.Null(vacancy.Salary);
        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Equal("Ukraine", vacancy.RemoteRestrictedTo);
        Assert.Equal(3, vacancy.MinYearsExperience);
    }

    // ---- the request ----

    private static StubHandler FeedAndPage() =>
        new(request => request.RequestUri!.AbsolutePath.StartsWith("/jobs/rss/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss version=\"2.0\"><channel/></rss>") }
            : FixturePage(RemoteUkraineSalaryPage));

    [Fact]
    public async Task Requests_only_the_vacancy_page_anonymously_with_the_user_agent_of_the_feed()
    {
        var handler = FeedAndPage();
        var source = Source(handler);

        await source.FetchAsync(CancellationToken.None);
        await source.EnrichAsync(FeedVacancy("854603"), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var (feed, page) = (handler.Requests[0], handler.Requests[1]);
        Assert.Equal(HttpMethod.Get, page.Method);
        Assert.Equal("https://djinni.co/jobs/854603-middle-net-developer/", page.Uri!.ToString());
        Assert.Equal(ExpectedUserAgent, feed.UserAgent);
        Assert.Equal(ExpectedUserAgent, page.UserAgent);

        // The adapter adds nothing but its name to the request: no cookie, no authorisation. (That the client keeps no
        // cookies of its own is checked on the registered client, in CompositionTests.)
        Assert.Equal(["User-Agent"], page.HeaderNames);
    }

    // The client follows no redirects (CompositionTests), so a redirect is an answer like any other non-success one:
    // the enrichment fails, naming the vacancy, and the other host is never asked.
    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task A_redirect_fails_the_enrichment_without_a_second_request(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Headers = { Location = new Uri("https://example.com/jobs/854603/") } });

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => Source(handler).EnrichAsync(FeedVacancy("854603"), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
        Assert.Contains(((int)status).ToString(), failure.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(DjinniHost, request.Uri!.Host);
    }

    [Theory]
    [InlineData("http://djinni.co/jobs/854603-dev/")]
    [InlineData("https://example.com/jobs/854603-dev/")]
    [InlineData("https://djinni.co.example.com/jobs/854603-dev/")]
    [InlineData("https://notdjinni.co/jobs/854603-dev/")]
    [InlineData("https://user:password@djinni.co/jobs/854603-dev/")]
    [InlineData("ftp://djinni.co/jobs/854603-dev/")]
    [InlineData("/jobs/854603-dev/")]
    [InlineData("not a url")]
    public async Task A_link_that_is_not_an_https_djinni_page_is_refused_without_any_request(string url)
    {
        var handler = new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage));

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => Source(handler).EnrichAsync(FeedVacancy("854603", url), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://djinni.co/jobs/854603-dev/")]
    [InlineData("https://www.djinni.co/jobs/854603-dev/")]
    [InlineData("https://DJINNI.CO/jobs/854603-dev/")]
    public async Task A_page_on_djinni_co_or_its_subdomain_is_requested(string url)
    {
        var handler = new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage));

        await Source(handler).EnrichAsync(FeedVacancy("854603", url), CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    // ---- failures ----

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HTTP_error_throws_naming_the_vacancy(HttpStatusCode status)
    {
        var source = Source(new StubHandler(_ => new HttpResponseMessage(status)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => source.EnrichAsync(FeedVacancy("854603"), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
        Assert.Contains(((int)status).ToString(), failure.Message);
        Assert.Equal(status, failure.StatusCode);
    }

    [Fact]
    public async Task A_page_without_JSON_LD_throws_naming_the_vacancy()
    {
        var source = Source(new StubHandler(_ => Html("<html><head><title>Just a moment...</title></head><body>Checking your browser</body></html>")));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => source.EnrichAsync(FeedVacancy("854603"), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
    }

    private const string DjinniHost = "djinni.co";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_invalid_unrelated_JSON_LD_block_is_skipped_before_or_after_the_JobPosting(bool invalidFirst)
    {
        const string invalid = "<script type=\"application/ld+json\">{\"@type\": \"Organization\", </script>";
        var posting = $"<script type=\"application/ld+json\">{JobPosting("""  "hiringOrganization": {"name": "Acme"}""")}</script>";
        var html = $"<html><head>{(invalidFirst ? invalid + posting : posting + invalid)}</head></html>";

        var vacancy = await Source(new StubHandler(_ => Html(html))).EnrichAsync(FeedVacancy(), CancellationToken.None);

        Assert.Equal("Acme", vacancy.Company);
    }

    [Fact]
    public async Task Invalid_JSON_beside_JSON_LD_without_a_JobPosting_throws_naming_the_vacancy()
    {
        var html = "<html><head><script type=\"application/ld+json\">{\"@type\": \"WebSite\"}</script>"
            + "<script type=\"application/ld+json\">{ broken</script></head></html>";

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => Source(new StubHandler(_ => Html(html))).EnrichAsync(FeedVacancy("854603"), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
    }

    [Fact]
    public async Task Invalid_JSON_throws_naming_the_vacancy()
    {
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => EnrichWithJsonAsync("""{"@type": "JobPosting", """));

        Assert.Contains("854603", failure.Message);
    }

    [Theory]
    [InlineData("""{"@context": "https://schema.org/", "@type": "Organization", "name": "Djinni"}""")]
    [InlineData("""[{"@type": "BreadcrumbList"}]""")]
    [InlineData("""{"@graph": [{"@type": "WebSite"}]}""")]
    [InlineData("42")]
    public async Task JSON_LD_without_a_JobPosting_throws_naming_the_vacancy(string jsonLd)
    {
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => EnrichWithJsonAsync(jsonLd));

        Assert.Contains("854603", failure.Message);
    }

    [Theory]
    [InlineData("""[{"@type": "BreadcrumbList"}, {"@type": "JobPosting", "hiringOrganization": {"name": "Acme"}}]""")]
    [InlineData("""{"@context": "https://schema.org", "@graph": [{"@type": "WebSite"}, {"@type": "JobPosting", "hiringOrganization": {"name": "Acme"}}]}""")]
    [InlineData("""{"@type": ["JobPosting"], "hiringOrganization": {"name": "Acme"}}""")]
    public async Task An_array_a_graph_and_a_list_of_types_are_accepted(string jsonLd)
    {
        var vacancy = await EnrichWithJsonAsync(jsonLd);

        Assert.Equal("Acme", vacancy.Company);
    }

    [Fact]
    public async Task The_JobPosting_is_found_among_several_JSON_LD_blocks()
    {
        var html = "<html><head><script type=\"application/ld+json\">{\"@type\": \"Organization\", \"name\": \"Djinni\"}</script>"
            + $"<script type=\"application/ld+json\">{JobPosting("""  "hiringOrganization": {"name": "Acme"}""")}</script></head></html>";

        var vacancy = await Source(new StubHandler(_ => Html(html))).EnrichAsync(FeedVacancy(), CancellationToken.None);

        Assert.Equal("Acme", vacancy.Company);
    }

    [Fact]
    public async Task An_oversized_page_throws_naming_the_vacancy()
    {
        var huge = new byte[DjinniSource.MaxPageBytes + 1];
        Array.Fill(huge, (byte)' ');
        var source = Source(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(huge) }));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => source.EnrichAsync(FeedVacancy("854603"), CancellationToken.None));

        Assert.Contains("854603", failure.Message);
    }

    [Fact]
    public async Task An_oversized_page_without_a_content_length_throws_too()
    {
        var huge = new byte[DjinniSource.MaxPageBytes + 1];
        Array.Fill(huge, (byte)' ');
        var source = Source(new StubHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(huge));
            content.Headers.ContentLength = null;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => source.EnrichAsync(FeedVacancy("854603"), CancellationToken.None));
    }

    // ---- politeness ----

    [Fact]
    public async Task Consecutive_page_requests_are_the_configured_gap_apart_and_the_first_does_not_wait()
    {
        var delays = new List<TimeSpan>();
        var source = Source(new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage)), delays: delays, delaySeconds: 7);

        await source.EnrichAsync(FeedVacancy("1"), CancellationToken.None);
        Assert.Empty(delays);

        await source.EnrichAsync(FeedVacancy("2"), CancellationToken.None);
        await source.EnrichAsync(FeedVacancy("3"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Only_the_rest_of_the_gap_is_waited_and_nothing_once_it_has_passed()
    {
        var clock = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var source = Source(new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage)), clock, delays);

        await source.EnrichAsync(FeedVacancy("1"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await source.EnrichAsync(FeedVacancy("2"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.EnrichAsync(FeedVacancy("3"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(1.5)], delays);
    }

    // The real wait: Task.Delay on the source's clock, not the test hook.
    [Fact]
    public async Task The_gap_is_really_waited_on_the_clock_of_the_source()
    {
        var clock = new ManualTimeProvider();
        var source = new DjinniSource(
            new HttpClient(new StubHandler(_ => FixturePage(RemoteUkraineSalaryPage))),
            Options.Create(new WatcherOptions { Sources = { Djinni = { FeedUrl = FeedUrl, PageRequestDelaySeconds = 3 } } }),
            clock);

        await source.EnrichAsync(FeedVacancy("1"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.EnrichAsync(FeedVacancy("2"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
    }

    // The feed request counts too: the first page request after it waits the rest of the gap.
    [Fact]
    public async Task The_first_page_request_after_the_feed_request_waits_the_gap()
    {
        var clock = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var source = Source(FeedAndPage(), clock, delays);

        await source.FetchAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await source.EnrichAsync(FeedVacancy("1"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(1.5)], delays);
    }

    [Fact]
    public async Task A_failed_page_request_also_counts_for_the_gap()
    {
        var delays = new List<TimeSpan>();
        var responses = new Queue<HttpResponseMessage>([new HttpResponseMessage(HttpStatusCode.InternalServerError), FixturePage(RemoteUkraineSalaryPage)]);
        var source = Source(new StubHandler(_ => responses.Dequeue()), delays: delays);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.EnrichAsync(FeedVacancy("1"), CancellationToken.None));
        await source.EnrichAsync(FeedVacancy("2"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public void The_enricher_follows_the_settings()
    {
        var options = new WatcherOptions { Sources = { Djinni = { ReadVacancyPages = false, MaxPagesPerCycle = 4 } } };

        IVacancyEnricher enricher = new DjinniSource(new HttpClient(), Options.Create(options));

        Assert.False(enricher.IsEnabled);
        Assert.Equal(4, enricher.MaxPerCycle);
        Assert.True(new DjinniSource(new HttpClient(), Options.Create(new WatcherOptions())).IsEnabled);
    }

    // ---- the mapping of single fields ----

    [Fact]
    public async Task A_JobPosting_without_usable_fields_leaves_the_vacancy_as_it_was()
    {
        var feed = FeedVacancy();

        var vacancy = await EnrichWithJsonAsync(
            JobPosting("""  "hiringOrganization": {"name": "   "}, "experienceRequirements": "3 years", "baseSalary": {"currency": "USD"}"""),
            feed);

        Assert.Equal(feed, vacancy);
    }

    [Theory]
    [InlineData(36.0, 3)]
    [InlineData(30.0, 3)]
    [InlineData(48.0, 4)]
    [InlineData(72.0, 6)]
    [InlineData(18.0, 2)]
    [InlineData(6.0, 1)]
    [InlineData(5.0, 0)]
    [InlineData(0.0, 0)]
    public async Task Years_are_the_months_of_experience_divided_by_12_rounded_half_up(double months, int years)
    {
        var amount = months.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var json = JobPosting($$"""  "experienceRequirements": {"monthsOfExperience": {{amount}} }""");

        var vacancy = await EnrichWithJsonAsync(json);

        Assert.Equal(years, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task An_office_in_several_cities_lists_them_all()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting(
            """  "jobLocation": {"@type": "Place", "address": {"addressCountry": "Україна", "addressLocality": ["Київ", "Львів"]}}"""));

        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
        Assert.Equal("Київ, Львів", vacancy.Location);
    }

    [Fact]
    public async Task An_office_whose_city_is_a_plain_string()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting(
            """  "jobLocation": {"@type": "Place", "address": {"addressLocality": "Львів"}}"""));

        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
        Assert.Equal("Львів", vacancy.Location);
    }

    [Fact]
    public async Task Telecommute_wins_over_a_job_location()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting(
            """  "jobLocationType": "TELECOMMUTE", "jobLocation": {"address": {"addressLocality": ["Київ"]}}"""));

        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Null(vacancy.Location);
    }

    [Theory]
    [InlineData("""{"address": {"addressCountry": "UA"}}""", "Ukraine")]
    [InlineData("""[{"address": {"addressCountry": "UKR"}}]""", "Ukraine")]
    [InlineData("""{"address": {"addressCountry": "Україна"}}""", "Ukraine")]
    [InlineData("""{"address": {"addressCountry": "US"}}""", "US")]
    [InlineData("""{"address": {"addressCountry": "USA"}}""", "US")]
    [InlineData("""{"address": {"addressCountry": "GB"}}""", "UK")]
    [InlineData("""{"address": {"addressCountry": "UK"}}""", "UK")]
    [InlineData("""{"address": {"addressCountry": null, "addressRegion": "Europe"}}""", "Europe")]
    [InlineData("""{"address": {"addressCountry": "PL"}}""", null)]
    [InlineData("""[{"address": {"addressCountry": "UA"}}, {"address": {"addressCountry": "PL"}}]""", null)]
    [InlineData("""[{"address": {"addressCountry": "UA"}}, {"address": {"addressRegion": "Europe"}}]""", null)]
    [InlineData("""[]""", null)]
    [InlineData("""{"address": {}}""", null)]
    public async Task A_remote_vacancy_is_restricted_only_to_exactly_one_known_region(string requirements, string? expected)
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting($"""  "jobLocationType": "TELECOMMUTE", "applicantLocationRequirements": {requirements}"""));

        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Equal(expected, vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task A_remote_vacancy_without_location_requirements_is_unrestricted()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting("""  "jobLocationType": "TELECOMMUTE" """));

        Assert.Null(vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task An_office_vacancy_is_never_restricted_to_a_region()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting(
            """  "jobLocation": {"address": {"addressLocality": ["Київ"]}}, "applicantLocationRequirements": {"address": {"addressCountry": "UA"}}"""));

        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
        Assert.Null(vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task A_salary_in_euro_per_year_is_read_from_the_page()
    {
        var vacancy = await EnrichWithJsonAsync(JobPosting(
            """  "baseSalary": {"@type": "MonetaryAmount", "currency": "EUR", "value": {"@type": "QuantitativeValue", "minValue": 60000, "maxValue": 80000, "unitText": "YEAR"}}"""));

        Assert.Equal("€60000–80000 / year", vacancy.Salary);
    }

    // ---- the salary text ----

    [Theory]
    [InlineData(2000, 2500, "USD", "MONTH", "$2000–2500", 2000, 2500, "USD", SalaryPeriod.Month)]
    [InlineData(2000, null, "USD", "MONTH", "від $2000", 2000, null, "USD", SalaryPeriod.Month)]
    [InlineData(null, 2500, "USD", "MONTH", "до $2500", null, 2500, "USD", SalaryPeriod.Month)]
    [InlineData(2500, 2500, "USD", "MONTH", "$2500", 2500, 2500, "USD", SalaryPeriod.Month)]
    [InlineData(3000, 4000, "EUR", "MONTH", "€3000–4000", 3000, 4000, "EUR", SalaryPeriod.Month)]
    [InlineData(3000, 4000, "GBP", "MONTH", "£3000–4000", 3000, 4000, "GBP", SalaryPeriod.Month)]
    [InlineData(50000, 80000, "UAH", "MONTH", "₴50000–80000", 50000, 80000, "UAH", SalaryPeriod.Month)]
    [InlineData(10000, 15000, "PLN", "MONTH", "10000–15000 PLN", 10000, 15000, "PLN", SalaryPeriod.Month)]
    [InlineData(10000, null, "PLN", "MONTH", "від 10000 PLN", 10000, null, "PLN", SalaryPeriod.Month)]
    [InlineData(60000, 80000, "USD", "YEAR", "$60000–80000 / year", 60000, 80000, "USD", SalaryPeriod.Year)]
    [InlineData(20, 40, "USD", "HOUR", "$20–40 / hour", 20, 40, "USD", SalaryPeriod.Hour)]
    [InlineData(500, 800, "USD", "MONTH", "$500–800 / month", 500, 800, "USD", SalaryPeriod.Month)]
    [InlineData(null, 900, "EUR", "MONTH", "до €900 / month", null, 900, "EUR", SalaryPeriod.Month)]
    [InlineData(2000, 2500, "usd", "month", "$2000–2500", 2000, 2500, "USD", SalaryPeriod.Month)]
    public void Salary_text_is_written_like_DOU_and_reads_back_the_same(
        int? min, int? max, string currency, string unit, string expected,
        int? parsedMin, int? parsedMax, string parsedCurrency, SalaryPeriod parsedPeriod)
    {
        var text = JobPostingPage.SalaryText(min, max, currency, unit);

        Assert.Equal(expected, text);
        Assert.Equal(new SalaryRange(parsedMin, parsedMax, parsedCurrency, parsedPeriod), SalaryParser.Parse(text));
    }

    [Fact]
    public void Salary_text_is_null_without_an_amount()
    {
        Assert.Null(JobPostingPage.SalaryText(null, null, "USD", "MONTH"));
    }
}
