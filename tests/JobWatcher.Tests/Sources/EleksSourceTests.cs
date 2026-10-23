// FIXTURE NOTE: Fixtures/eleks-*.html have the markup of the real list and vacancy pages of careers.eleks.com (read on
// 2026-10-23, see docs/sources/eleks.md), but their vacancies, places, links and texts are made up, so that no vacancy
// of the employer is copied into the repository. Tests never touch the network: every request goes to a stub
// HttpMessageHandler, and the clock is a ManualTimeProvider, so the gaps between requests cost no time.

using System.Net;
using System.Text;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Parsing;
using JobWatcher.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

public sealed class EleksSourceTests
{
    private const string ListUrl = "https://careers.eleks.com/vacancies/?technology=21,27&location=206,98";
    private const string SecondPageUrl = "https://careers.eleks.com/vacancies/page/2/?technology=21,27&location=206,98";

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private sealed record Recorded(HttpMethod Method, Uri? Uri, string UserAgent, IReadOnlyList<string> HeaderNames);

    private sealed class StubHandler(Func<Recorded, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var recorded = new Recorded(
                request.Method,
                request.RequestUri,
                request.Headers.UserAgent.ToString(),
                request.Headers.Select(header => header.Key).ToList());
            Requests.Add(recorded);

            return Task.FromResult(respond(recorded));
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDir, name));

    private static HttpResponseMessage Ok(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static WatcherOptions SettingsFor(string listUrl = ListUrl, int? minFetchIntervalMinutes = null)
    {
        var settings = new WatcherOptions { Sources = { Eleks = { Enabled = true, ListUrl = listUrl } } };
        settings.Sources.Eleks.MinFetchIntervalMinutes = minFetchIntervalMinutes ?? settings.Sources.Eleks.MinFetchIntervalMinutes;

        return settings;
    }

    private static EleksSource SourceFor(
        StubHandler handler,
        WatcherOptions? settings = null,
        TimeProvider? time = null,
        ILogger<EleksSource>? log = null) =>
        new(new HttpClient(handler), Options.Create(settings ?? SettingsFor()), time ?? new ManualTimeProvider(), log);

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(Func<Recorded, HttpResponseMessage> respond) =>
        SourceFor(new StubHandler(respond)).FetchAsync(CancellationToken.None);

    private static Task<IReadOnlyList<Vacancy>> FetchFixtureAsync() => FetchAsync(_ => Ok(Fixture("eleks-list.html")));

    // One card of the list in the real markup.
    private static string Card(string slug, string title, string location, string technology = ".NET") =>
        $"<a class=\"vacancy-item animated fadeIn \" data-technology=\"net\" href=\"https://careers.eleks.com/vacancies/{slug}/\">"
        + $"<div class=\"vacancy-item__title\">{title}</div><div class=\"vacancy-item__location\">{location}</div>"
        + $"<div class=\"vacancy-item__technology\">{technology}</div></a>";

    // A list page: the container with the cards, and the pager of the real site when the list has more than one page.
    private static string ListPage(string cards, int current = 1, int pages = 1)
    {
        var pager = new StringBuilder();
        if (pages > 1)
        {
            pager.Append("<ul class='page-numbers'>");
            for (var number = 1; number <= pages; number++)
            {
                pager.Append(number == current
                    ? $"<li><span aria-current=\"page\" class=\"page-numbers current\">{number}</span></li>"
                    : $"<li><a class=\"page-numbers\" href=\"/vacancies/?technology=21,27&#038;paged={number}\">{number}</a></li>");
            }

            pager.Append("</ul>");
        }

        return "<html><body><div class=\"vacancies-list\">" + cards + "</div>"
            + "<div class=\"pagination-wrapper\">" + pager + "</div></body></html>";
    }

    // The list is narrowed to ".NET, Ukraine or Remote" by Eleks, so there is no flood to hold back at the first start,
    // and with an empty list today the first vacancy to appear must not be swallowed as the baseline.
    [Fact]
    public void The_first_read_announces_what_is_listed_instead_of_remembering_it_silently()
    {
        IVacancySource source = SourceFor(new StubHandler(_ => Ok("")));

        Assert.True(source.AnnouncesExistingOnFirstRun);
    }

    [Fact]
    public async Task Fetch_requests_the_configured_list_once_and_says_who_is_asking()
    {
        var handler = new StubHandler(_ => Ok(Fixture("eleks-list.html")));

        await SourceFor(handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(ListUrl, request.Uri!.ToString());
        Assert.StartsWith("JobWatcher/", request.UserAgent);
        Assert.Contains("personal job alerts", request.UserAgent);
        Assert.DoesNotContain(request.HeaderNames, name => name is "Authorization" or "Cookie");
    }

    [Fact]
    public async Task Fetch_turns_a_card_into_a_vacancy_of_eleks()
    {
        var vacancies = await FetchFixtureAsync();

        // The list says nothing of years, pay or the work mode (beyond the places), so those stay unknown; the
        // description is the technology tags, which are what the list tells about the work.
        Assert.Equal(
            new Vacancy(
                VacancySource.Eleks,
                "senior-net-developer-101",
                "Senior .Net Developer",
                "ELEKS",
                "https://careers.eleks.com/vacancies/senior-net-developer-101/",
                Salary: null,
                "Ukraine",
                WorkMode.Unknown,
                RemoteRestrictedTo: null,
                MinYearsExperience: null,
                IsJunior: false,
                "Software Engineering, .NET"),
            vacancies.Single(vacancy => vacancy.SourceId == "senior-net-developer-101"));
    }

    [Fact]
    public async Task Fetch_skips_cards_without_a_title_and_links_that_are_not_vacancies_of_the_site()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(
            [
                "senior-net-developer-101",
                "lead-net-developer-102",
                "middle-net-engineer-103",
                "senior-backend-engineer-104",
                "junior-net-developer-105",
            ],
            vacancies.Select(vacancy => vacancy.SourceId));
    }

    [Fact]
    public async Task Fetch_decodes_the_html_entities_of_a_title()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal("Middle .NET Engineer & Azure", vacancies.Single(vacancy => vacancy.SourceId == "middle-net-engineer-103").Title);
    }

    [Fact]
    public async Task Fetch_keeps_the_places_as_the_site_writes_them()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(
            "Ternopil, Ivano-Frankivsk, Lviv, Kyiv, Uzhhorod, Chernivtsi, Ukraine",
            vacancies.Single(vacancy => vacancy.SourceId == "lead-net-developer-102").Location);
    }

    [Theory]
    [InlineData("Remote", WorkMode.Remote)]
    [InlineData("remote", WorkMode.Remote)]
    [InlineData("Poland, Remote, Croatia, Europe", WorkMode.Remote)]
    [InlineData("Ukraine", WorkMode.Unknown)]
    [InlineData("Lviv, Ukraine", WorkMode.Unknown)]
    [InlineData("Remoteville, Ukraine", WorkMode.Unknown)]
    public async Task Fetch_takes_a_vacancy_for_remote_only_when_remote_is_one_of_its_places(string location, WorkMode expected)
    {
        var vacancies = await FetchAsync(_ => Ok(ListPage(Card("dev-1", "Developer", location))));

        var vacancy = Assert.Single(vacancies);
        Assert.Equal(expected, vacancy.WorkMode);
        Assert.Equal(location, vacancy.Location);

        // A remote vacancy is not tied to a region: the list names the places, not who may apply from where.
        Assert.Null(vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task Fetch_takes_a_junior_title_for_junior()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(
            ["junior-net-developer-105"],
            vacancies.Where(vacancy => vacancy.IsJunior).Select(vacancy => vacancy.SourceId));
    }

    [Fact]
    public async Task Fetch_lists_nothing_for_a_list_without_vacancies()
    {
        var vacancies = await FetchAsync(_ => Ok(Fixture("eleks-list-empty.html")));

        Assert.Empty(vacancies);
    }

    [Fact]
    public async Task Fetch_fails_when_the_page_has_no_list_at_all()
    {
        // What an anti-bot page or a redesigned site would look like: not an empty list, but no list.
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchAsync(_ => Ok("<html><body><h1>Checking your browser</h1></body></html>")));

        Assert.Contains("markup has probably changed", failure.Message);
    }

    [Fact]
    public async Task Fetch_fails_when_none_of_the_cards_can_be_read()
    {
        var cards = Card("dev-1", " ", "Ukraine") + "<a class=\"vacancy-item\" href=\"https://careers.eleks.com/about/\"><div class=\"vacancy-item__title\">About</div></a>";

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(ListPage(cards))));

        Assert.Contains("2 cards", failure.Message);
    }

    [Fact]
    public async Task Fetch_fails_naming_the_status_when_the_site_answers_with_an_error()
    {
        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => FetchAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Contains("503", failure.Message);
    }

    // ---- the list has more than one page ----

    [Fact]
    public async Task Fetch_reads_the_next_page_of_the_list_at_its_own_address_a_gap_after_the_first()
    {
        var clock = new ManualTimeProvider();
        var handler = new StubHandler(request => Ok(request.Uri!.AbsolutePath.Contains("/page/2/")
            ? ListPage(Card("second-page-dev-2", "Second Page Developer", "Ukraine"), current: 2, pages: 2)
            : ListPage(Card("first-page-dev-1", "First Page Developer", "Ukraine"), current: 1, pages: 2)));

        var vacancies = await SourceFor(handler, time: clock).FetchAsync(CancellationToken.None);

        Assert.Equal(["first-page-dev-1", "second-page-dev-2"], vacancies.Select(vacancy => vacancy.SourceId));

        // The address of the second page is the one the site redirects "?paged=2" to; no redirect is followed.
        Assert.Equal([ListUrl, SecondPageUrl], handler.Requests.Select(request => request.Uri!.ToString()));
        Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
    }

    [Fact]
    public async Task Fetch_reads_at_most_five_pages_of_the_list_and_warns_about_the_rest()
    {
        var log = new RecordingLogger<EleksSource>();
        var handler = new StubHandler(request =>
        {
            var page = request.Uri!.AbsolutePath.Contains("/page/")
                ? int.Parse(request.Uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2])
                : 1;

            return Ok(ListPage(Card($"dev-{page}", $"Developer {page}", "Ukraine"), current: page, pages: 9));
        });

        var vacancies = await SourceFor(handler, log: log).FetchAsync(CancellationToken.None);

        Assert.Equal(
            ["/vacancies/", "/vacancies/page/2/", "/vacancies/page/3/", "/vacancies/page/4/", "/vacancies/page/5/"],
            handler.Requests.Select(request => request.Uri!.AbsolutePath));
        Assert.Equal(5, vacancies.Count);
        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("9 pages", warning.Message);
    }

    // ---- once a day ----

    [Fact]
    public async Task A_fetch_within_a_day_of_a_good_one_makes_no_request_and_says_so()
    {
        var clock = new ManualTimeProvider();
        var log = new RecordingLogger<EleksSource>();
        var handler = new StubHandler(_ => Ok(Fixture("eleks-list.html")));
        var source = SourceFor(handler, time: clock, log: log);

        var first = await source.FetchAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        var second = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.Single(handler.Requests);

        // The empty list reads as "no vacancies" in the cycle's summary; this line says why.
        var entry = Assert.Single(log.Entries, entry => entry.Message.Contains("no request this time"));
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public async Task The_list_is_requested_again_a_day_after_a_good_fetch()
    {
        var clock = new ManualTimeProvider();
        var handler = new StubHandler(_ => Ok(Fixture("eleks-list.html")));
        var source = SourceFor(handler, time: clock);

        await source.FetchAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(1));
        var again = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEmpty(again);
    }

    [Fact]
    public async Task The_interval_between_fetches_is_the_configured_one()
    {
        var clock = new ManualTimeProvider();
        var handler = new StubHandler(_ => Ok(Fixture("eleks-list.html")));
        var source = SourceFor(handler, SettingsFor(minFetchIntervalMinutes: 60), clock);

        await source.FetchAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(59));
        await source.FetchAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await source.FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    // The day is protection of the site, not of a quota: a fetch that failed must not cost a whole day.
    [Fact]
    public async Task A_failed_fetch_is_tried_again_by_the_next_cycle_not_a_day_later()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => calls++ == 0
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(Fixture("eleks-list.html")));
        var source = SourceFor(handler, time: clock);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(15));
        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEmpty(vacancies);
    }

    // ---- the page of a new vacancy ----

    private const string PageUrl = "https://careers.eleks.com/vacancies/senior-net-developer-101/";

    // A vacancy as the list gives it.
    private static Vacancy Listed(string url = PageUrl, string? technologies = "Software Engineering, .NET") =>
        new(
            VacancySource.Eleks,
            "senior-net-developer-101",
            "Senior .Net Developer",
            "ELEKS",
            url,
            Salary: null,
            "Ukraine",
            WorkMode.Unknown,
            RemoteRestrictedTo: null,
            MinYearsExperience: null,
            IsJunior: false,
            technologies);

    private static Task<Vacancy> EnrichAsync(Vacancy vacancy, Func<Recorded, HttpResponseMessage> respond) =>
        SourceFor(new StubHandler(respond)).EnrichAsync(vacancy, CancellationToken.None);

    private static Task<Vacancy> EnrichFixtureAsync(Vacancy? vacancy = null) =>
        EnrichAsync(vacancy ?? Listed(), _ => Ok(Fixture("eleks-vacancy-page.html")));

    [Fact]
    public async Task Enrich_requests_the_page_of_the_vacancy_and_says_who_is_asking()
    {
        var handler = new StubHandler(_ => Ok(Fixture("eleks-vacancy-page.html")));

        await SourceFor(handler).EnrichAsync(Listed(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(PageUrl, request.Uri!.ToString());
        Assert.StartsWith("JobWatcher/", request.UserAgent);
        Assert.DoesNotContain(request.HeaderNames, name => name is "Authorization" or "Cookie");
    }

    [Fact]
    public async Task A_page_is_requested_a_gap_after_the_list()
    {
        var clock = new ManualTimeProvider();
        var source = SourceFor(new StubHandler(request => Ok(request.Uri!.AbsolutePath == "/vacancies/"
            ? Fixture("eleks-list.html")
            : Fixture("eleks-vacancy-page.html"))), time: clock);

        var vacancies = await source.FetchAsync(CancellationToken.None);
        await source.EnrichAsync(vacancies[0], CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
    }

    [Fact]
    public async Task Enrich_takes_the_requirements_from_the_vacancy_text_of_the_page()
    {
        var vacancy = await EnrichFixtureAsync();

        // The first four items, as for every source; the recruiter's contacts beside the text are not part of it.
        Assert.Equal(
            [
                "7+ years of experience in software development",
                "4+ years of experience in .NET",
                "Experience with Azure & Docker",
                "Advanced level of English",
            ],
            RequirementsExtractor.Extract(vacancy.Description));
        Assert.DoesNotContain("Recruiter", vacancy.Description);
    }

    [Fact]
    public async Task Enrich_takes_the_years_of_experience_from_the_requirements()
    {
        var vacancy = await EnrichFixtureAsync();

        Assert.Equal(7, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task Enrich_keeps_the_technology_tags_of_the_list_in_the_description()
    {
        // The text of this page does not name every technology the tags do; the profile filter reads the description.
        var vacancy = await EnrichFixtureAsync(Listed(technologies: "Software Engineering, Rust"));

        Assert.Contains("Software Engineering, Rust", vacancy.Description);
    }

    [Fact]
    public async Task Enrich_changes_nothing_else_of_the_vacancy()
    {
        var listed = Listed();

        var vacancy = await EnrichFixtureAsync(listed);

        Assert.Equal(listed with { MinYearsExperience = 7, Description = vacancy.Description }, vacancy);
    }

    [Fact]
    public async Task Enrich_fails_naming_the_vacancy_when_the_page_has_no_vacancy_text()
    {
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => EnrichAsync(Listed(), _ => Ok("<html><body><h1>Page not found</h1></body></html>")));

        Assert.Contains("senior-net-developer-101", failure.Message);
        Assert.Contains("markup has probably changed", failure.Message);
    }

    [Fact]
    public async Task Enrich_fails_naming_the_status_when_the_site_answers_with_an_error()
    {
        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => EnrichAsync(Listed(), _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Contains("senior-net-developer-101", failure.Message);
    }

    [Theory]
    [InlineData("https://jobs.example.test/vacancies/senior-net-developer-101/")]
    [InlineData("http://careers.eleks.com/vacancies/senior-net-developer-101/")]
    [InlineData("https://careers.eleks.com.evil.test/vacancies/senior-net-developer-101/")]
    [InlineData("https://user:secret@careers.eleks.com/vacancies/senior-net-developer-101/")]
    [InlineData("https://careers.eleks.com/about/")]
    [InlineData("not a link")]
    public async Task Enrich_requests_nothing_for_a_link_that_is_not_a_vacancy_page_of_the_site(string url)
    {
        var handler = new StubHandler(_ => Ok(Fixture("eleks-vacancy-page.html")));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => SourceFor(handler).EnrichAsync(Listed(url), CancellationToken.None));

        Assert.Contains("senior-net-developer-101", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Enrich_fails_for_a_page_larger_than_three_megabytes()
    {
        var huge = "<html><body><div class=\"vacancy__text\">" + new string('a', 3 * 1024 * 1024) + "</div></body></html>";

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => EnrichAsync(Listed(), _ => Ok(huge)));

        Assert.Contains("3 MB", failure.Message);
    }

    [Fact]
    public void Reading_of_pages_follows_the_settings()
    {
        var settings = SettingsFor();
        var source = SourceFor(new StubHandler(_ => Ok("")), settings);

        Assert.True(source.IsEnabled);
        Assert.Equal(15, source.MaxPerCycle);

        settings.Sources.Eleks.ReadVacancyPages = false;
        settings.Sources.Eleks.MaxPagesPerCycle = 7;

        Assert.False(source.IsEnabled);
        Assert.Equal(7, source.MaxPerCycle);
    }
}
