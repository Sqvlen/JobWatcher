using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Sources;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

public sealed class DjinniSourceTests
{
    private const string FeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";
    private const string DefaultLink = "https://djinni.co/jobs/100-dev/";

    // A real response of the .NET feed, trimmed to ten representative items (see docs/sources/djinni.md).
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "djinni-dotnet.xml");

    // Vacancy numbers of the fixture items that the tests below rely on, in the order of the feed.
    private const string TeamsEngineer = "847830";
    private const string Junior = "852012";
    private const string FullStack = "854237";
    private const string Middle = "831444";
    private const string JuniorMiddle = "840994";
    private const string Senior = "835298";
    private const string EngineerUkraine = "790553";
    private const string Rozrobnik = "852667";
    private const string Trainee = "188528";
    private const string Principal = "285359";

    private static readonly string[] FixtureIds =
        [TeamsEngineer, Junior, FullStack, Middle, JuniorMiddle, Senior, EngineerUkraine, Rozrobnik, Trainee, Principal];

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri, request.Headers.UserAgent.ToString()));

            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Ok(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };

    private static HttpResponseMessage OkBytes(byte[] xml) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(xml) };

    private static DjinniSource SourceReturning(StubHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new WatcherOptions { Sources = { Djinni = { FeedUrl = FeedUrl } } }));

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(Func<HttpResponseMessage> respond) =>
        SourceReturning(new StubHandler(respond)).FetchAsync(CancellationToken.None);

    private static Task<IReadOnlyList<Vacancy>> FetchFixtureAsync() =>
        FetchAsync(() => OkBytes(File.ReadAllBytes(FixturePath)));

    private static async Task<Vacancy> FixtureVacancyAsync(string sourceId) =>
        (await FetchFixtureAsync()).Single(v => v.SourceId == sourceId);

    private static XElement Item(string? title, string? link = DefaultLink, string? description = "<p>text</p>", string? guid = null) =>
        new("item",
            title is null ? null : new XElement("title", title),
            link is null ? null : new XElement("link", link),
            description is null ? null : new XElement("description", description),
            new XElement("guid", guid ?? link ?? "no-link"));

    private static string Feed(params XElement[] items) =>
        new XElement("rss", new XAttribute("version", "2.0"), new XElement("channel", new XElement("title", "Test"), items)).ToString();

    private static async Task<Vacancy> FetchSingleAsync(XElement item)
    {
        var vacancies = await FetchAsync(() => Ok(Feed(item)));

        return Assert.Single(vacancies);
    }

    [Fact]
    public async Task Source_is_Djinni_on_the_adapter_and_every_vacancy()
    {
        var source = SourceReturning(new StubHandler(() => OkBytes(File.ReadAllBytes(FixturePath))));

        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(VacancySource.Djinni, source.Source);
        Assert.Equal("Djinni", source.Source.DisplayName());
        Assert.NotEmpty(vacancies);
        Assert.All(vacancies, v => Assert.Equal(VacancySource.Djinni, v.Source));
    }

    [Fact]
    public async Task Maps_every_item_to_vacancy()
    {
        var itemCount = XDocument.Load(FixturePath).Descendants("item").Count();

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(10, itemCount);
        Assert.Equal(itemCount, vacancies.Count);
        Assert.Equal(FixtureIds, vacancies.Select(v => v.SourceId));
    }

    [Fact]
    public async Task First_item_fields_match()
    {
        var first = (await FetchFixtureAsync())[0];

        Assert.Equal(VacancySource.Djinni, first.Source);
        Assert.Equal("847830", first.SourceId);
        Assert.Equal(".NET Engineer", first.Title);
        Assert.Equal(DjinniSource.UnknownCompany, first.Company);
        Assert.Equal("https://djinni.co/jobs/847830-net-engineer/", first.Url);
        Assert.Null(first.Salary);
        Assert.Null(first.Location);
        Assert.Equal(WorkMode.Unknown, first.WorkMode);
        Assert.Null(first.RemoteRestrictedTo);
        Assert.Equal(3, first.MinYearsExperience);
        Assert.False(first.IsJunior);
        Assert.StartsWith("<p>We’re looking for a <strong>.NET Engineer</strong>", first.Description);
    }

    [Fact]
    public async Task Description_is_the_html_of_the_feed_item()
    {
        var expected = XDocument.Load(FixturePath).Descendants("item").Select(i => (string?)i.Element("description"));

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(expected, vacancies.Select(v => v.Description));
        Assert.Contains("<ul><li>", vacancies.Single(v => v.SourceId == Middle).Description);
    }

    // The feed has no company field, and the watch cycle drops a vacancy whose company is blank, so the company
    // is a visible "not specified" text rather than an empty string (docs/sources/djinni.md).
    [Fact]
    public async Task Company_is_the_not_specified_text_because_the_feed_has_none()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal("не вказано", DjinniSource.UnknownCompany);
        Assert.All(vacancies, v => Assert.Equal(DjinniSource.UnknownCompany, v.Company));
    }

    [Fact]
    public async Task Fields_the_feed_does_not_carry_are_left_unknown_for_every_real_item()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.All(vacancies, v =>
        {
            Assert.Null(v.Salary);
            Assert.Null(v.Location);
            Assert.Equal(WorkMode.Unknown, v.WorkMode);
            Assert.Null(v.RemoteRestrictedTo);
        });
    }

    // Real items state a place and a work model in free text: "Location: Galway, Ireland ... Hybrid" (Principal),
    // "work in Lviv or Kyiv, remote work possibility" (Trainee), "(Ukraine)" in the title (EngineerUkraine). That
    // is prose written by the employer, not a field of the feed, so none of it is read as a place or a work mode.
    [Fact]
    public async Task Places_and_work_models_written_in_the_text_of_real_items_are_not_read()
    {
        var vacancies = await FetchFixtureAsync();

        foreach (var id in new[] { Principal, Trainee, EngineerUkraine })
        {
            var vacancy = vacancies.Single(v => v.SourceId == id);
            Assert.Null(vacancy.Location);
            Assert.Equal(WorkMode.Unknown, vacancy.WorkMode);
            Assert.Null(vacancy.RemoteRestrictedTo);
        }
    }

    [Fact]
    public async Task Nothing_is_guessed_from_the_title_or_description()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Remote .NET Developer, $5000, Київ (Europe only)",
            description: "<p>Salary: $5000/month. Fully remote, Ukraine or EU. Office in Київ.</p>"));

        Assert.Null(vacancy.Salary);
        Assert.Null(vacancy.Location);
        Assert.Equal(WorkMode.Unknown, vacancy.WorkMode);
        Assert.Null(vacancy.RemoteRestrictedTo);
        Assert.Equal("Remote .NET Developer, $5000, Київ (Europe only)", vacancy.Title);
    }

    [Fact]
    public async Task Title_and_url_are_the_text_of_the_item_without_surrounding_whitespace()
    {
        var vacancy = await FetchSingleAsync(Item(
            "  Розробник (С#)\n",
            link: "  https://djinni.co/jobs/852667-rozrobnik-s/ \n"));

        Assert.Equal("Розробник (С#)", vacancy.Title);
        Assert.Equal("https://djinni.co/jobs/852667-rozrobnik-s/", vacancy.Url);
    }

    [Fact]
    public async Task Real_titles_in_other_languages_and_with_brackets_are_kept_as_they_are()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(".NET Engineer, (Ukraine)", vacancies.Single(v => v.SourceId == EngineerUkraine).Title);
        Assert.Equal("C# програміст( trainee/junior/middle )", vacancies.Single(v => v.SourceId == Trainee).Title);
        Assert.Equal(
            "Principal Software Developer/ Architect (C#) (Ireland, Galway)",
            vacancies.Single(v => v.SourceId == Principal).Title);
    }

    [Theory]
    [InlineData("Junior .NET Developer", true)]
    [InlineData("JUNIOR C# Engineer", true)]
    [InlineData("Junior/Middle .NET Developer (C#)", true)]
    [InlineData("Jr. C# Developer", true)]
    [InlineData("Trainee Backend Developer", true)]
    [InlineData("C# Intern", true)]
    [InlineData("Джуніор .NET розробник", true)]
    [InlineData("Стажер C#", true)]
    [InlineData("Senior .NET Developer", false)]
    [InlineData("Internal Tools .NET Developer", false)]
    [InlineData("Juniper Networks Integration Engineer", false)]
    [InlineData("Middle .NET Developer", false)]
    public async Task IsJunior_only_for_whole_junior_words_in_the_title(string title, bool expected)
    {
        var vacancy = await FetchSingleAsync(Item(title));

        Assert.Equal(expected, vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_looks_at_the_title_not_the_description()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Senior .NET Developer",
            description: "<p>We hire interns and juniors too. Junior Games is our sister studio.</p>"));

        Assert.False(vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_is_set_for_the_junior_fixture_items_only()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal([Junior, JuniorMiddle, Trainee], vacancies.Where(v => v.IsJunior).Select(v => v.SourceId));
    }

    // The requirements headings of these items are "What we expect" (TeamsEngineer), "Requirements" (FullStack,
    // Senior), "Кваліфікаційні вимоги:" (Middle) and "Що потрібно" (JuniorMiddle). The years are the largest
    // minimum among the title and the first four requirement items, e.g. Middle "3+ років досвіду роботи з C# та
    // .NET" or JuniorMiddle "1-2 роки досвіду бекенд-розробки" (read as 1).
    [Fact]
    public async Task MinYears_is_read_from_the_requirement_items_of_the_real_items()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(3, vacancies.Single(v => v.SourceId == TeamsEngineer).MinYearsExperience);
        Assert.Equal(5, vacancies.Single(v => v.SourceId == FullStack).MinYearsExperience);
        Assert.Equal(3, vacancies.Single(v => v.SourceId == Middle).MinYearsExperience);
        Assert.Equal(1, vacancies.Single(v => v.SourceId == JuniorMiddle).MinYearsExperience);
        Assert.Equal(5, vacancies.Single(v => v.SourceId == Senior).MinYearsExperience);
    }

    // Junior and Rozrobnik list requirements without a number of years, and Trainee has no requirements list.
    // Principal ("10+ years of combined experience" after "What We’re Looking For") states years, but not in a list
    // item, so no item is taken as a requirement: years are never read from the whole description (see
    // MinYears_ignores_years_in_the_description_outside_the_requirements).
    [Fact]
    public async Task MinYears_is_null_when_the_requirement_items_state_no_years()
    {
        var vacancies = await FetchFixtureAsync();

        foreach (var id in new[] { Junior, Rozrobnik, Trainee, Principal })
        {
            Assert.Null(vacancies.Single(v => v.SourceId == id).MinYearsExperience);
        }
    }

    // EngineerUkraine lists "5 - 8+ years" under the plain line "What we’re looking for", a heading the requirements
    // extractor knows since the final fix wave. The experience parser reads the "8+" of that range.
    [Fact]
    public async Task MinYears_is_read_under_a_what_were_looking_for_heading()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(8, vacancies.Single(v => v.SourceId == EngineerUkraine).MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_ignores_years_in_the_description_outside_the_requirements()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Dev",
            description: "<p>We are a 2 year old startup with a 10 years long roadmap.</p>"
                + "<p><strong>Responsibilities:</strong></p><ul><li>Build things for 7 years of customers</li></ul>"
                + "<p><strong>Requirements:</strong></p><ul><li>C# and SQL</li></ul>"
                + "<p><strong>We offer:</strong></p><ul><li>15 years of stability</li></ul>"));

        Assert.Null(vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_reads_the_requirement_items_and_the_title()
    {
        const string requirements = "<p><strong>Requirements:</strong></p><ul><li>3+ years of C#</li></ul>";

        var fromRequirements = await FetchSingleAsync(Item("Dev", description: "<p>A 2 year old startup.</p>" + requirements));
        var fromTitle = await FetchSingleAsync(Item("Senior .NET Developer (6+ years)", description: requirements));

        Assert.Equal(3, fromRequirements.MinYearsExperience);
        Assert.Equal(6, fromTitle.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_null_when_the_item_has_no_description()
    {
        var vacancy = await FetchSingleAsync(Item("Dev", description: null));

        Assert.Null(vacancy.MinYearsExperience);
        Assert.Null(vacancy.Description);
    }

    [Fact]
    public async Task A_blank_description_is_null()
    {
        var vacancy = await FetchSingleAsync(Item("Dev", description: "   "));

        Assert.Null(vacancy.Description);
    }

    [Theory]
    [InlineData("https://djinni.co/jobs/847830-net-engineer/", "847830")]
    [InlineData("https://djinni.co/jobs/100-dev-ops-engineer-2/", "100")]
    [InlineData("https://djinni.co/jobs/100/", "100")]
    [InlineData("https://djinni.co/jobs/100", "100")]
    [InlineData("https://djinni.co/jobs/100-dev/?utm_source=rss", "100")]
    public async Task SourceId_is_the_vacancy_number_of_the_link_not_the_guid(string link, string expected)
    {
        var vacancy = await FetchSingleAsync(Item("Dev", link: link, guid: "some-other-guid-" + Guid.NewGuid()));

        Assert.Equal(expected, vacancy.SourceId);
        Assert.Equal(link, vacancy.Url);
    }

    [Fact]
    public async Task SourceId_stays_the_same_when_the_title_and_slug_of_the_vacancy_change()
    {
        var before = await FetchSingleAsync(Item("Dev", link: "https://djinni.co/jobs/100-dev/"));
        var after = await FetchSingleAsync(Item("Senior Dev", link: "https://djinni.co/jobs/100-senior-dev/"));

        Assert.Equal(before.SourceId, after.SourceId);
    }

    [Fact]
    public async Task Item_without_a_usable_link_or_title_is_skipped_while_the_others_are_kept()
    {
        var vacancies = await FetchAsync(() => Ok(Feed(
            Item("Dev", link: "https://djinni.co/jobs/100-dev/"),
            Item("NoLink", link: null),
            Item("NoNumber", link: "https://djinni.co/jobs/europe-remote/"),
            Item("RssLink", link: "https://djinni.co/jobs/rss/"),
            Item("   ", link: "https://djinni.co/jobs/101-blank-title/"),
            Item(null, link: "https://djinni.co/jobs/102-no-title/"))));

        Assert.Equal("100", Assert.Single(vacancies).SourceId);
    }

    [Fact]
    public async Task Empty_feed_returns_empty_list()
    {
        var vacancies = await FetchAsync(() => Ok(Feed()));

        Assert.Empty(vacancies);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HTTP_error_throws(HttpStatusCode status)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(() => new HttpResponseMessage(status)));
    }

    [Fact]
    public async Task Invalid_xml_throws()
    {
        await Assert.ThrowsAnyAsync<XmlException>(
            () => FetchAsync(() => Ok("<rss version=\"2.0\"><channel><item><title>Dev")));
    }

    [Fact]
    public async Task Document_type_declaration_is_refused()
    {
        const string withDtd = "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x \"boom\">]><rss version=\"2.0\"><channel/></rss>";

        await Assert.ThrowsAnyAsync<XmlException>(() => FetchAsync(() => Ok(withDtd)));
    }

    [Fact]
    public async Task An_html_page_instead_of_the_feed_throws()
    {
        // What a login wall or a bot check would answer with; the source reports it and does not try to get past it.
        const string page = "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>Checking your browser</body></html>";

        await Assert.ThrowsAnyAsync<XmlException>(() => FetchAsync(() => Ok(page)));
    }

    [Fact]
    public async Task Valid_xml_that_is_not_a_feed_throws()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchAsync(() => Ok("<html><body>Just a moment...</body></html>")));
    }

    [Fact]
    public async Task A_feed_without_a_channel_throws()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(() => Ok("<rss version=\"2.0\"/>")));
    }

    [Fact]
    public async Task Feed_with_items_but_none_readable_throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(() => Ok(Feed(
            Item("No number", link: "https://djinni.co/jobs/europe-remote/"),
            Item("Another one", link: null)))));

        Assert.Contains("2", exception.Message);
    }

    [Fact]
    public async Task Requests_the_configured_feed_once_and_introduces_itself()
    {
        var handler = new StubHandler(() => Ok(Feed()));

        await SourceReturning(handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(FeedUrl, request.Uri!.ToString());
        Assert.StartsWith("JobWatcher/", request.UserAgent);
        Assert.Contains("Djinni", request.UserAgent);
    }
}
