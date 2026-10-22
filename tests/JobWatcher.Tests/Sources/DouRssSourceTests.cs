using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Parsing;
using JobWatcher.Sources;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

public sealed class DouRssSourceTests
{
    private const string FeedUrl = "https://jobs.dou.ua/vacancies/feeds/?category=.NET";
    private const string DefaultLink = "https://jobs.dou.ua/companies/acme/vacancies/100/?utm_source=jobsrss";

    // A real response of the .NET category feed, trimmed to ten representative items (see docs/sources/dou.md).
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dou-dotnet.xml");

    // Vacancy numbers of the fixture items that the tests below rely on.
    private const string Freitty = "366693";
    private const string NixQa = "377422";
    private const string Teambase = "377095";
    private const string PersonalitiesMiddle = "339168";
    private const string PersonalitiesJunior = "339171";
    private const string ScaleCenter = "354938";
    private const string Darnytsia = "372638";
    private const string DraftKings = "376604";
    private const string Fulcrum = "324014";
    private const string AListwareReact = "376563";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri, request.Headers.UserAgent.ToString()));

            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Ok(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };

    private static HttpResponseMessage OkBytes(byte[] xml) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(xml) };

    private static DouRssSource SourceReturning(StubHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new WatcherOptions { Sources = { Dou = { FeedUrl = FeedUrl } } }));

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(Func<HttpResponseMessage> respond) =>
        SourceReturning(new StubHandler(respond)).FetchAsync(CancellationToken.None);

    private static Task<IReadOnlyList<Vacancy>> FetchFixtureAsync() =>
        FetchAsync(() => OkBytes(File.ReadAllBytes(FixturePath)));

    private static async Task<Vacancy> FixtureVacancyAsync(string sourceId) =>
        (await FetchFixtureAsync()).Single(v => v.SourceId == sourceId);

    private static XElement Item(string title, string? link = DefaultLink, string? description = "<p>text</p>", string? guid = null) =>
        new("item",
            new XElement("title", title),
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
    public async Task Source_is_Dou_on_the_adapter_and_every_vacancy()
    {
        var source = SourceReturning(new StubHandler(() => OkBytes(File.ReadAllBytes(FixturePath))));

        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(VacancySource.Dou, source.Source);
        Assert.Equal("DOU", source.Source.DisplayName());
        Assert.NotEmpty(vacancies);
        Assert.All(vacancies, v => Assert.Equal(VacancySource.Dou, v.Source));
    }

    [Fact]
    public async Task Maps_every_item_to_vacancy()
    {
        var itemCount = XDocument.Load(FixturePath).Descendants("item").Count();

        var vacancies = await FetchFixtureAsync();

        Assert.True(itemCount > 0);
        Assert.Equal(itemCount, vacancies.Count);
    }

    [Fact]
    public async Task First_item_fields_match()
    {
        var first = (await FetchFixtureAsync())[0];

        Assert.Equal(VacancySource.Dou, first.Source);
        Assert.Equal("366693", first.SourceId);
        Assert.Equal("Backend .NET Developer", first.Title);
        Assert.Equal("FREITTY", first.Company);
        Assert.Equal("https://jobs.dou.ua/companies/freitty/vacancies/366693/?utm_source=jobsrss", first.Url);
        Assert.Null(first.Location);
        Assert.Null(first.Salary);
        Assert.Equal(WorkMode.Remote, first.WorkMode);
        Assert.Null(first.RemoteRestrictedTo);
        Assert.Null(first.MinYearsExperience);
        Assert.False(first.IsJunior);
    }

    [Fact]
    public async Task Description_is_the_html_of_the_feed_item()
    {
        var expected = XDocument.Load(FixturePath).Descendants("item").Select(i => (string?)i.Element("description"));

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(expected, vacancies.Select(v => v.Description));
        Assert.Contains("<ul><li>", vacancies.Single(v => v.SourceId == PersonalitiesMiddle).Description);
    }

    [Fact]
    public async Task Title_is_decoded_because_the_feed_escapes_html_twice()
    {
        var vacancy = await FixtureVacancyAsync(AListwareReact);

        Assert.Equal("Senior Full-Stack (React & C#)", vacancy.Title);
        Assert.Equal("A-listware", vacancy.Company);
    }

    [Fact]
    public async Task Title_is_everything_before_the_last_в_so_the_company_stays_correct()
    {
        var vacancy = await FetchSingleAsync(Item("Розробник в команду продукту в Acme, Київ"));

        Assert.Equal("Розробник в команду продукту", vacancy.Title);
        Assert.Equal("Acme", vacancy.Company);
        Assert.Equal("Київ", vacancy.Location);
    }

    [Fact]
    public async Task Company_name_with_a_comma_stays_whole_when_the_link_slug_spells_it()
    {
        var vacancy = await FixtureVacancyAsync(PersonalitiesMiddle);

        Assert.Equal("Personalities, HR Company", vacancy.Company);
        Assert.Equal("Львів", vacancy.Location);
    }

    [Fact]
    public async Task Legal_form_after_a_comma_belongs_to_the_company()
    {
        var vacancy = await FixtureVacancyAsync(DraftKings);

        Assert.Equal("DraftKings, Inc.", vacancy.Company);
        Assert.Null(vacancy.Location);
    }

    [Fact]
    public async Task A_place_is_not_taken_for_part_of_the_company_name_unless_the_slug_spells_it_exactly()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Dev в Acme, Kyiv",
            link: "https://jobs.dou.ua/companies/acme-kyiv-group/vacancies/100/?utm_source=jobsrss"));

        Assert.Equal("Acme", vacancy.Company);
        Assert.Equal("Kyiv", vacancy.Location);
    }

    [Fact]
    public async Task Location_lists_the_cities_as_the_feed_does()
    {
        var vacancy = await FixtureVacancyAsync(NixQa);

        Assert.Equal("Senior Automation QA (C#) Engineer (#5973)", vacancy.Title);
        Assert.Equal("N-iX", vacancy.Company);
        Assert.Equal("Київ, Львів, Дніпро, Вінниця, Івано-Франківськ, Тернопіль", vacancy.Location);
    }

    [Fact]
    public async Task Remote_item_has_WorkMode_Remote()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == Freitty).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == NixQa).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == Teambase).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == DraftKings).WorkMode);
    }

    [Fact]
    public async Task Item_with_only_cities_is_Office_and_item_without_places_is_Unknown()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(WorkMode.Office, vacancies.Single(v => v.SourceId == PersonalitiesMiddle).WorkMode);
        Assert.Equal(WorkMode.Office, vacancies.Single(v => v.SourceId == Darnytsia).WorkMode);
        Assert.Equal(WorkMode.Unknown, vacancies.Single(v => v.SourceId == ScaleCenter).WorkMode);
        Assert.Null(vacancies.Single(v => v.SourceId == ScaleCenter).Location);
    }

    [Fact]
    public async Task Salary_is_null_when_feed_has_none()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Null(vacancies.Single(v => v.SourceId == Freitty).Salary);
        Assert.Null(vacancies.Single(v => v.SourceId == NixQa).Salary);
        Assert.Null(vacancies.Single(v => v.SourceId == PersonalitiesMiddle).Salary);
        Assert.Null(vacancies.Single(v => v.SourceId == Darnytsia).Salary);
    }

    [Fact]
    public async Task Salary_is_the_text_the_feed_states()
    {
        var vacancies = await FetchFixtureAsync();

        var range = vacancies.Single(v => v.SourceId == Teambase).Salary;
        Assert.Equal("$2500–4000", range);
        Assert.Equal("$700", vacancies.Single(v => v.SourceId == ScaleCenter).Salary);

        var parsed = SalaryParser.Parse(range);
        Assert.NotNull(parsed);
        Assert.Equal(2500m, parsed.Min);
        Assert.Equal(4000m, parsed.Max);
        Assert.Equal("USD", parsed.Currency);
    }

    [Theory]
    [InlineData("Dev в Acme, до $3500, Львів, віддалено", "до $3500", "Львів")]
    [InlineData("Dev в Acme, від $1200", "від $1200", null)]
    [InlineData("Dev в Acme, Київ, 3000–4500 грн", "3000–4500 грн", "Київ")]
    public async Task Salary_is_found_wherever_the_feed_puts_it(string title, string salary, string? location)
    {
        var vacancy = await FetchSingleAsync(Item(title));

        Assert.Equal(salary, vacancy.Salary);
        Assert.Equal(location, vacancy.Location);
    }

    [Fact]
    public async Task A_place_with_digits_is_not_taken_for_a_salary()
    {
        var vacancy = await FetchSingleAsync(Item("Dev в Acme, Zone 51, віддалено"));

        Assert.Null(vacancy.Salary);
        Assert.Equal("Zone 51", vacancy.Location);
    }

    [Theory]
    [InlineData("Dev в Acme, Remote Ukraine", "Ukraine")]
    [InlineData("Dev в Acme, remote europe", "Europe")]
    [InlineData("Dev в Acme, Remote EU", "EU")]
    [InlineData("Dev в Acme, Remote USA", "US")]
    [InlineData("Dev в Acme, Ukraine (remote)", "Ukraine")]
    [InlineData("Dev в Acme, Remote", null)]
    [InlineData("Dev в Acme, віддалено", null)]
    [InlineData("Dev в Acme, Київ, віддалено", null)]
    [InlineData("Dev в Acme, Remote Poland", null)]
    [InlineData("Dev в Acme, Remote EU only", "EU")]
    [InlineData("Dev в Acme, Remote US only", "US")]
    [InlineData("Dev в Acme, Ukraine (remote), Poland (remote)", null)]
    public async Task RemoteRestrictedTo_is_an_exact_region_or_null(string title, string? expected)
    {
        var vacancy = await FetchSingleAsync(Item(title));

        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Equal(expected, vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task Remote_regions_stated_per_country_stay_in_the_location()
    {
        var vacancy = await FetchSingleAsync(Item("Dev в Acme, Poland (remote), Ukraine (remote), Львів"));

        Assert.Equal("Poland (remote), Ukraine (remote), Львів", vacancy.Location);
    }

    [Theory]
    [InlineData("Junior .NET Developer", true)]
    [InlineData("JUNIOR C# Engineer", true)]
    [InlineData("Junior+ / Middle .NET Developer", true)]
    [InlineData("Jr. C# Developer", true)]
    [InlineData("Trainee Backend Developer", true)]
    [InlineData("C# Intern", true)]
    [InlineData("C# Internship Programme", true)]
    [InlineData("Джуніор .NET розробник", true)]
    [InlineData("Стажер C#", true)]
    [InlineData("Senior .NET Developer", false)]
    [InlineData("Internal Tools .NET Developer", false)]
    [InlineData("Juniper Networks Integration Engineer", false)]
    [InlineData("Middle .NET Developer", false)]
    public async Task IsJunior_only_for_whole_junior_words_in_the_title(string title, bool expected)
    {
        var vacancy = await FetchSingleAsync(Item($"{title} в Acme, Київ"));

        Assert.Equal(expected, vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_looks_at_the_title_not_the_company_or_description()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Senior .NET Developer в Junior Games, Київ",
            description: "<p>We hire interns and juniors too.</p>"));

        Assert.False(vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_is_set_for_the_junior_fixture_item_only()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal([PersonalitiesJunior], vacancies.Where(v => v.IsJunior).Select(v => v.SourceId));
    }

    [Fact]
    public async Task MinYears_parsed_from_description()
    {
        var vacancies = await FetchFixtureAsync();

        // "5+ years of experience in testing (with at least 3 years ...)": the largest minimum wins.
        Assert.Equal(5, vacancies.Single(v => v.SourceId == NixQa).MinYearsExperience);
        Assert.Equal(4, vacancies.Single(v => v.SourceId == Fulcrum).MinYearsExperience);
        Assert.Equal(2, vacancies.Single(v => v.SourceId == Darnytsia).MinYearsExperience);
        Assert.Null(vacancies.Single(v => v.SourceId == PersonalitiesMiddle).MinYearsExperience);
    }

    // The requirements headings of these items are "What you need", "What You’ll Bring" and "Required
    // Experience and Skills:". The years are the largest minimum among the first four requirement items:
    // Teambase "3+ years of commercial C# / .NET experience (5+ for Senior)" reads as 3 (the "(5+ for Senior)"
    // has no year word), A-listware "3+ years ... React" and "2+ years ... ASP.NET" as 3.
    [Fact]
    public async Task MinYears_is_read_from_the_requirements_headings_of_the_real_items()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(3, vacancies.Single(v => v.SourceId == Teambase).MinYearsExperience);
        Assert.Equal(5, vacancies.Single(v => v.SourceId == DraftKings).MinYearsExperience);
        Assert.Equal(3, vacancies.Single(v => v.SourceId == AListwareReact).MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_ignores_years_in_the_description_outside_the_requirements()
    {
        var vacancy = await FetchSingleAsync(Item(
            "Dev в Acme, Київ",
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

        var fromRequirements = await FetchSingleAsync(Item("Dev в Acme, Київ", description: "<p>A 2 year old startup.</p>" + requirements));
        var fromTitle = await FetchSingleAsync(Item("Senior .NET Developer (6+ years) в Acme, Київ", description: requirements));

        Assert.Equal(3, fromRequirements.MinYearsExperience);
        Assert.Equal(6, fromTitle.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_null_when_the_item_has_no_description()
    {
        var vacancy = await FetchSingleAsync(Item("Dev в Acme, Київ", description: null));

        Assert.Null(vacancy.MinYearsExperience);
        Assert.Null(vacancy.Description);
    }

    [Fact]
    public async Task SourceId_is_the_vacancy_number_of_the_link_not_the_guid_that_changes_on_every_republish()
    {
        var before = await FetchSingleAsync(Item("Dev в Acme", guid: "https://jobs.dou.ua/companies/acme/vacancies/100/?1792600064"));
        var after = await FetchSingleAsync(Item("Dev в Acme", guid: "https://jobs.dou.ua/companies/acme/vacancies/100/?1792999999"));

        Assert.Equal("100", before.SourceId);
        Assert.Equal(before.SourceId, after.SourceId);
    }

    [Fact]
    public async Task Item_without_a_usable_link_or_title_is_skipped_while_the_others_are_kept()
    {
        var vacancies = await FetchAsync(() => Ok(Feed(
            Item("Dev в Acme, Київ"),
            Item("Dev в NoLink, Київ", link: null),
            Item("Dev в NoNumber, Київ", link: "https://jobs.dou.ua/companies/nonumber/"),
            Item("A title without the company marker"),
            Item(" в Acme, Київ"),
            Item("Dev в "))));

        Assert.Equal("Acme", Assert.Single(vacancies).Company);
    }

    [Fact]
    public async Task Empty_feed_returns_empty_list()
    {
        var vacancies = await FetchAsync(() => Ok(Feed()));

        Assert.Empty(vacancies);
    }

    [Fact]
    public async Task HTTP_error_throws()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => FetchAsync(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    }

    [Fact]
    public async Task Invalid_xml_throws()
    {
        await Assert.ThrowsAnyAsync<XmlException>(
            () => FetchAsync(() => Ok("<rss version=\"2.0\"><channel><item><title>Dev в Acme")));
    }

    [Fact]
    public async Task Document_type_declaration_is_refused()
    {
        const string withDtd = "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x \"boom\">]><rss version=\"2.0\"><channel/></rss>";

        await Assert.ThrowsAnyAsync<XmlException>(() => FetchAsync(() => Ok(withDtd)));
    }

    [Fact]
    public async Task Valid_xml_that_is_not_a_feed_throws()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchAsync(() => Ok("<html><body>Just a moment...</body></html>")));
    }

    [Fact]
    public async Task Feed_with_items_but_none_readable_throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(() => Ok(Feed(
            Item("A title without the company marker"),
            Item("Another one", link: null)))));

        Assert.Contains("2", exception.Message);
    }

    [Fact]
    public async Task Requests_the_configured_feed_and_introduces_itself()
    {
        var handler = new StubHandler(() => Ok(Feed()));

        await SourceReturning(handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(FeedUrl, request.Uri!.ToString());
        Assert.StartsWith("JobWatcher/", request.UserAgent);
    }
}
