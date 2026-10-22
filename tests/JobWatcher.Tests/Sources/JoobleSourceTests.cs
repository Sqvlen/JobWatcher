// SYNTHETIC FIXTURE WARNING: Fixtures/jooble-response.json is NOT a captured Jooble response. No Jooble API key
// was available when this adapter was written, so the fixture was built by hand from the field names of the
// official Jooble REST API documentation (see docs/sources/jooble.md); its vacancies, companies and ids are made
// up. The tests prove that the adapter reads the DOCUMENTED format; they cannot prove that real responses look
// like it. A smoke check with a real key (docs/sources/jooble.md, "Перевірка з реальним ключем") is still open.
// Tests never touch the network: every request goes to a stub HttpMessageHandler.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Formatting;
using JobWatcher.Parsing;
using JobWatcher.Sources;
using JobWatcher.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

public sealed class JoobleSourceTests
{
    private const string ApiKey = "test-key-123";
    private const string Endpoint = "https://jooble.org/api/test-key-123";
    private const string DefaultLink = "https://ua.jooble.org/jdp/100";

    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jooble-response.json");

    // Ids of the fixture jobs that the tests below rely on (the Jooble ids are 64-bit numbers).
    private const string KyivSenior = "6402917753301214820";
    private const string RemoteMiddle = "-3735906425358868903";
    private const string RemoteEurope = "1154820937462018775";
    private const string JuniorLviv = "8827301945560112934";
    private const string RemoteUkraine = "2290174465830091273";
    private const string RemotePoland = "5517203388914402561";
    private const string Sparse = "7036641128894556102";
    private const string NoLocation = "4410298376601113847";

    // Salary strings of the fixture that SalaryParser is known not to read. Empty: it reads all of them.
    private static readonly string[] SalaryParserExceptions = [];

    private sealed record Recorded(HttpMethod Method, Uri? Uri, string ContentType, string UserAgent, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public string? Keywords => Json.GetProperty("keywords").GetString();

        public string? Location => Json.GetProperty("location").GetString();
    }

    private sealed class StubHandler(Func<Recorded, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The content is read here because the request (and its content) is disposed once the call returns.
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var recorded = new Recorded(
                request.Method,
                request.RequestUri,
                request.Content?.Headers.ContentType?.ToString() ?? "",
                request.Headers.UserAgent.ToString(),
                body);
            Requests.Add(recorded);

            return respond(recorded);
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // A clock that only moves when a test says so. One timestamp tick is one millisecond.
    private sealed class FakeTime : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

        private long ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => ticks;

        public override DateTimeOffset GetUtcNow() => Start.AddMilliseconds(ticks);

        public void Advance(TimeSpan by) => ticks += (long)by.TotalMilliseconds;
    }

    private static WatcherOptions SettingsFor(
        string apiKey = ApiKey,
        string[]? cities = null,
        string keyword = ".NET",
        string? baseUrl = null,
        int? minFetchIntervalMinutes = null)
    {
        var settings = new WatcherOptions
        {
            Profile = { Keyword = keyword, Cities = cities ?? ["Київ", "Kyiv"] },
            Sources = { Jooble = { ApiKey = apiKey } },
        };
        if (baseUrl is not null)
        {
            settings.Sources.Jooble.BaseUrl = baseUrl;
        }

        if (minFetchIntervalMinutes is { } minutes)
        {
            settings.Sources.Jooble.MinFetchIntervalMinutes = minutes;
        }

        return settings;
    }

    private static JoobleSource SourceFor(
        StubHandler handler,
        string apiKey = ApiKey,
        string[]? cities = null,
        string keyword = ".NET",
        string? baseUrl = null,
        int? minFetchIntervalMinutes = null,
        TimeProvider? time = null,
        ILogger<JoobleSource>? log = null) =>
        new(
            new HttpClient(handler),
            Options.Create(SettingsFor(apiKey, cities, keyword, baseUrl, minFetchIntervalMinutes)),
            time,
            log);

    private static string FixtureJson() => File.ReadAllText(FixturePath);

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(Func<Recorded, HttpResponseMessage> respond) =>
        SourceFor(new StubHandler(respond)).FetchAsync(CancellationToken.None);

    private static Task<IReadOnlyList<Vacancy>> FetchFixtureAsync() => FetchAsync(_ => Ok(FixtureJson()));

    private static async Task<Vacancy> FixtureVacancyAsync(string sourceId) =>
        (await FetchFixtureAsync()).Single(v => v.SourceId == sourceId);

    private static IEnumerable<JsonElement> FixtureJobs() =>
        JsonDocument.Parse(FixtureJson()).RootElement.GetProperty("jobs").EnumerateArray();

    // A job object as the API returns it; null omits the property. `id` is JSON text (a number or a string).
    private static JsonObject Job(
        string? id = "100",
        string? title = "Dev",
        string? location = "Київ",
        string? type = "Full-time",
        string? snippet = "A job.",
        string? salary = null,
        string? company = "Acme",
        string? link = DefaultLink)
    {
        var job = new JsonObject();
        AddIfSet(job, "id", id is null ? null : JsonNode.Parse(id));
        AddIfSet(job, "title", title);
        AddIfSet(job, "location", location);
        AddIfSet(job, "snippet", snippet);
        AddIfSet(job, "salary", salary);
        AddIfSet(job, "type", type);
        AddIfSet(job, "link", link);
        AddIfSet(job, "company", company);

        return job;
    }

    private static void AddIfSet(JsonObject job, string name, JsonNode? value)
    {
        if (value is not null)
        {
            job[name] = value;
        }
    }

    private static string Response(params JsonObject[] jobs) =>
        $$"""{"totalCount":{{jobs.Length}},"jobs":[{{string.Join(",", jobs.Select(j => j.ToJsonString()))}}]}""";

    private static async Task<Vacancy> FetchSingleAsync(JsonObject job)
    {
        var json = Response(job);

        return Assert.Single(await FetchAsync(_ => Ok(json)));
    }

    [Fact]
    public async Task Source_is_Jooble_on_the_adapter_and_every_vacancy()
    {
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())));

        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(VacancySource.Jooble, source.Source);
        Assert.Equal("Jooble", source.Source.DisplayName());
        Assert.NotEmpty(vacancies);
        Assert.All(vacancies, v => Assert.Equal(VacancySource.Jooble, v.Source));
    }

    [Fact]
    public async Task Maps_job_fields_to_vacancy()
    {
        var first = (await FetchFixtureAsync())[0];

        Assert.Equal(
            new Vacancy(
                VacancySource.Jooble,
                "6402917753301214820",
                "Senior .NET Developer",
                "Northwind Software",
                "https://ua.jooble.org/jdp/6402917753301214820",
                "4,000 - 5,500 USD",
                "Київ",
                WorkMode.Office,
                null,
                null,
                false,
                "Ми шукаємо досвідченого&nbsp;<b>.NET</b>&nbsp;розробника в команду продукту. "
                + "Вимоги: 5+ років досвіду з <b>C#</b>, ASP.NET Core, SQL Server &amp; Azure. Ми пропонуємо ..."),
            first);
    }

    [Fact]
    public async Task Maps_every_job_of_the_fixture_once_in_the_order_of_the_response()
    {
        var expected = FixtureJobs().Select(j => j.GetProperty("id").GetRawText());

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(8, vacancies.Count);
        Assert.Equal(expected, vacancies.Select(v => v.SourceId));
    }

    [Fact]
    public async Task Salary_is_text_as_returned_and_null_when_empty()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal("4,000 - 5,500 USD", vacancies.Single(v => v.SourceId == KyivSenior).Salary);
        Assert.Equal("від 60 000 грн", vacancies.Single(v => v.SourceId == RemoteUkraine).Salary);
        Assert.Equal("$3,000 - $4,000 per month", vacancies.Single(v => v.SourceId == RemotePoland).Salary);

        // "salary": "" in the response, and no "salary" property at all.
        Assert.Null(vacancies.Single(v => v.SourceId == RemoteMiddle).Salary);
        Assert.Null(vacancies.Single(v => v.SourceId == Sparse).Salary);
    }

    [Fact]
    public async Task Salary_of_every_fixture_job_is_its_salary_text_or_null_when_blank()
    {
        var expected = FixtureJobs().Select(j =>
            j.TryGetProperty("salary", out var salary) && !string.IsNullOrWhiteSpace(salary.GetString()) ? salary.GetString() : null);

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(expected, vacancies.Select(v => v.Salary));
    }

    [Fact]
    public async Task A_blank_salary_is_null()
    {
        var vacancy = await FetchSingleAsync(Job(salary: "   "));

        Assert.Null(vacancy.Salary);
    }

    [Fact]
    public async Task Fixture_salary_strings_are_recognised_by_SalaryParser()
    {
        var salaries = (await FetchFixtureAsync()).Select(v => v.Salary).OfType<string>().ToList();

        Assert.NotEmpty(salaries);
        foreach (var salary in salaries.Where(s => !SalaryParserExceptions.Contains(s)))
        {
            Assert.True(SalaryParser.Parse(salary) is not null, $"SalaryParser does not read \"{salary}\"");
        }

        var range = SalaryParser.Parse("4,000 - 5,500 USD");
        Assert.NotNull(range);
        Assert.Equal((4000m, 5500m, "USD"), (range.Min, range.Max, range.Currency));
        var openEnded = SalaryParser.Parse("від 60 000 грн");
        Assert.NotNull(openEnded);
        Assert.Equal((60000m, (decimal?)null, "UAH"), (openEnded.Min, openEnded.Max, openEnded.Currency));
    }

    [Fact]
    public async Task Description_is_the_snippet()
    {
        var expected = FixtureJobs().Select(j => j.GetProperty("snippet").GetString());

        var vacancies = await FetchFixtureAsync();

        Assert.Equal(expected, vacancies.Select(v => v.Description));
        Assert.Contains("<b>.NET</b>&nbsp;", vacancies.Single(v => v.SourceId == KyivSenior).Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_missing_or_blank_snippet_gives_no_description(string? snippet)
    {
        var vacancy = await FetchSingleAsync(Job(snippet: snippet));

        Assert.Null(vacancy.Description);
    }

    [Fact]
    public async Task Location_is_the_location_text_or_null_when_blank()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal("Київ", vacancies.Single(v => v.SourceId == KyivSenior).Location);
        Assert.Equal("Remote, Europe", vacancies.Single(v => v.SourceId == RemoteEurope).Location);
        Assert.Null(vacancies.Single(v => v.SourceId == NoLocation).Location);
    }

    [Fact]
    public async Task A_job_without_a_company_is_kept_with_the_not_specified_placeholder()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(Placeholders.NotSpecified, vacancies.Single(v => v.SourceId == Sparse).Company);
        Assert.Equal("ТОВ «Софт Плюс»", vacancies.Single(v => v.SourceId == JuniorLviv).Company);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_missing_or_blank_company_becomes_the_not_specified_placeholder(string? company)
    {
        var vacancy = await FetchSingleAsync(Job(company: company));

        Assert.Equal(Placeholders.NotSpecified, vacancy.Company);
    }

    [Fact]
    public async Task Jooble_Djinni_and_the_formatter_show_the_same_not_specified_text()
    {
        var jooble = (await FetchSingleAsync(Job(company: null))).Company;
        var djinni = DjinniSource.UnknownCompany;
        var salaryLine = MessageFormatter.Format(MakeVacancyWithoutSalary(), [], null)
            .Split('\n')
            .Single(line => line.StartsWith("💰 ", StringComparison.Ordinal));

        Assert.Equal("не вказано", Placeholders.NotSpecified);
        Assert.Equal(Placeholders.NotSpecified, jooble);
        Assert.Equal(Placeholders.NotSpecified, djinni);
        Assert.Equal($"💰 {Placeholders.NotSpecified}", salaryLine);
    }

    private static Vacancy MakeVacancyWithoutSalary() => new(
        VacancySource.Jooble, "1", "Dev", "Acme", "https://ua.jooble.org/jdp/1", Salary: null, Location: null, WorkMode.Unknown,
        RemoteRestrictedTo: null, MinYearsExperience: null, IsJunior: false, Description: null);

    [Fact]
    public async Task Merges_results_of_both_queries_and_dedupes_by_id()
    {
        var kyiv = Response(
            Job(id: "1", title: "Kyiv only", link: "https://ua.jooble.org/jdp/1"),
            Job(id: "2", title: "In both (kyiv copy)", link: "https://ua.jooble.org/jdp/2"));
        var remote = Response(
            Job(id: "2", title: "In both (remote copy)", link: "https://ua.jooble.org/jdp/2"),
            Job(id: "\"3\"", title: "Remote only", link: "https://ua.jooble.org/jdp/3"),
            Job(id: "1", title: "Kyiv only again", link: "https://ua.jooble.org/jdp/1"));
        var handler = new StubHandler(request => Ok(request.Location == "remote" ? remote : kyiv));

        var vacancies = await SourceFor(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(["1", "2", "3"], vacancies.Select(v => v.SourceId));
        Assert.Equal(["Kyiv only", "In both (kyiv copy)", "Remote only"], vacancies.Select(v => v.Title));
    }

    [Fact]
    public async Task The_same_job_under_a_numeric_and_a_string_id_is_one_vacancy()
    {
        var kyiv = Response(Job(id: "77"));
        var remote = Response(Job(id: "\"77\""));

        var vacancies = await FetchAsync(request => Ok(request.Location == "remote" ? remote : kyiv));

        Assert.Equal("77", Assert.Single(vacancies).SourceId);
    }

    [Fact]
    public async Task Sends_two_queries_to_the_keyed_endpoint_with_keywords_and_location()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, cities: ["Львів", "Kyiv"], keyword: "C#").FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.Uri!.AbsoluteUri);
            Assert.StartsWith("application/json", request.ContentType);
            Assert.StartsWith("JobWatcher/", request.UserAgent);
            Assert.Equal("C#", request.Keywords);
        });
        Assert.Equal(["Львів", "remote"], handler.Requests.Select(request => request.Location));
    }

    [Fact]
    public async Task Reads_only_the_first_page_of_each_query()
    {
        // totalCount is 41 but the fixture lists 8 jobs: a pager would ask for more pages.
        var handler = new StubHandler(_ => Ok(FixtureJson()));

        await SourceFor(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.True(
                !request.Json.TryGetProperty("page", out var page) || page.ToString() == "1",
                "only the first page is requested");
        });
    }

    public static TheoryData<string[]> NoUsableCity => new() { Array.Empty<string>(), new[] { "" }, new[] { "  ", "Kyiv" } };

    [Theory]
    [MemberData(nameof(NoUsableCity))]
    public async Task Asks_for_Київ_when_the_first_city_is_missing_or_blank(string[] cities)
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, cities: cities).FetchAsync(CancellationToken.None);

        Assert.Equal(["Київ", "remote"], handler.Requests.Select(request => request.Location));
    }

    [Fact]
    public async Task Sends_cyrillic_text_as_utf8_json()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, cities: ["Київ"]).FetchAsync(CancellationToken.None);

        Assert.Contains("Київ", handler.Requests[0].Body);
    }

    [Fact]
    public async Task The_api_key_is_escaped_in_the_path()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, apiKey: " a b/c ").FetchAsync(CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.Equal("https://jooble.org/api/a%20b%2Fc", request.Uri!.AbsoluteUri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_api_key_throws_SourceNotConnectedException(string apiKey)
    {
        var handler = new StubHandler(_ => Ok(FixtureJson()));

        var exception = await Assert.ThrowsAsync<SourceNotConnectedException>(
            () => SourceFor(handler, apiKey: apiKey).FetchAsync(CancellationToken.None));

        Assert.Equal(VacancySource.Jooble, exception.SourceKind);
        Assert.StartsWith("Source Jooble is not connected: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("JobWatcher__Sources__Jooble__ApiKey", exception.Reason);
        Assert.Contains("https://ua.jooble.org/api/about", exception.Reason);
        Assert.Contains("jooble.org covers only US vacancies", exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Dev", "Київ", "Full-time", WorkMode.Office)]
    [InlineData("Dev", "", "Full-time", WorkMode.Unknown)]
    [InlineData("Dev", null, null, WorkMode.Unknown)]
    [InlineData("Dev", "Remote", "Full-time", WorkMode.Remote)]
    [InlineData("Dev", "Київ або віддалено", "Full-time", WorkMode.Remote)]
    [InlineData("Remote .NET Developer", "Київ", "Full-time", WorkMode.Remote)]
    [InlineData(".NET Developer (віддалено)", "Київ", "Full-time", WorkMode.Remote)]
    [InlineData("Dev", "Київ", "Remote", WorkMode.Remote)]
    [InlineData("Dev", "Київ", "Дистанційна робота", WorkMode.Remote)]
    [InlineData("Dev", "Київ", "Full-time, Remotely", WorkMode.Remote)]
    [InlineData("Дистанційно: .NET Developer", "Львів", "Full-time", WorkMode.Remote)]
    [InlineData("Remoteness Analyst", "Київ", "Full-time", WorkMode.Office)]
    [InlineData("Dev", "Київ", "Part-time", WorkMode.Office)]
    public async Task WorkMode_is_Remote_when_type_location_or_title_say_so(
        string title, string? location, string? type, WorkMode expected)
    {
        var vacancy = await FetchSingleAsync(Job(title: title, location: location, type: type));

        Assert.Equal(expected, vacancy.WorkMode);
    }

    [Fact]
    public async Task Remote_in_the_snippet_alone_does_not_make_a_vacancy_remote()
    {
        var vacancy = await FetchSingleAsync(Job(snippet: "Fully remote work is possible after probation."));

        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
    }

    [Fact]
    public async Task WorkMode_of_the_fixture_jobs()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(WorkMode.Office, vacancies.Single(v => v.SourceId == KyivSenior).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == RemoteMiddle).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == RemoteEurope).WorkMode);
        Assert.Equal(WorkMode.Office, vacancies.Single(v => v.SourceId == JuniorLviv).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == RemoteUkraine).WorkMode);
        Assert.Equal(WorkMode.Remote, vacancies.Single(v => v.SourceId == RemotePoland).WorkMode);
        Assert.Equal(WorkMode.Office, vacancies.Single(v => v.SourceId == Sparse).WorkMode);
        Assert.Equal(WorkMode.Unknown, vacancies.Single(v => v.SourceId == NoLocation).WorkMode);
    }

    [Theory]
    [InlineData("Remote", null)]
    [InlineData("Remote, Europe", "Europe")]
    [InlineData("Remote Europe", "Europe")]
    [InlineData("remote - europe", "Europe")]
    [InlineData("Європа (віддалено)", "Europe")]
    [InlineData("Ukraine (remote)", "Ukraine")]
    [InlineData("Україна", "Ukraine")]
    [InlineData("Remote EU", "EU")]
    [InlineData("Remote, USA", "US")]
    [InlineData("United States", "US")]
    [InlineData("Remote / UK", "UK")]
    [InlineData("Remote, Poland", null)]
    [InlineData("Remote EU only", "EU")]
    [InlineData("Remote, US only", "US")]
    [InlineData("Remote, Europe, Ukraine", null)]
    [InlineData("Київ, віддалено", null)]
    [InlineData("", null)]
    public async Task RemoteRestrictedTo_is_an_exact_region_or_null(string location, string? expected)
    {
        var vacancy = await FetchSingleAsync(Job(title: "Remote Dev", location: location));

        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.Equal(expected, vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task RemoteRestrictedTo_of_the_fixture_jobs()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal("Europe", vacancies.Single(v => v.SourceId == RemoteEurope).RemoteRestrictedTo);
        Assert.Equal("Ukraine", vacancies.Single(v => v.SourceId == RemoteUkraine).RemoteRestrictedTo);
        Assert.Null(vacancies.Single(v => v.SourceId == RemoteMiddle).RemoteRestrictedTo);
        Assert.Null(vacancies.Single(v => v.SourceId == RemotePoland).RemoteRestrictedTo);
        Assert.Null(vacancies.Single(v => v.SourceId == KyivSenior).RemoteRestrictedTo);
    }

    [Fact]
    public async Task An_office_job_is_not_restricted_to_a_region()
    {
        var vacancy = await FetchSingleAsync(Job(title: "Dev", location: "Ukraine"));

        Assert.Equal(WorkMode.Office, vacancy.WorkMode);
        Assert.Null(vacancy.RemoteRestrictedTo);
    }

    [Theory]
    [InlineData("Junior .NET Developer", true)]
    [InlineData("JUNIOR C# Engineer", true)]
    [InlineData("Junior+ / Middle .NET Developer", true)]
    [InlineData("<b>Junior</b> C# Developer", true)]
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
        var vacancy = await FetchSingleAsync(Job(title: title));

        Assert.Equal(expected, vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_looks_at_the_title_not_the_company_or_snippet()
    {
        var vacancy = await FetchSingleAsync(Job(
            title: "Senior .NET Developer",
            company: "Junior Games",
            snippet: "We hire interns and juniors too."));

        Assert.False(vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_is_set_for_the_junior_fixture_job_only()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal([JuniorLviv], vacancies.Where(v => v.IsJunior).Select(v => v.SourceId));
    }

    [Fact]
    public async Task MinYears_is_read_from_the_title()
    {
        var vacancy = await FetchSingleAsync(Job(title: "Senior .NET Developer (6+ years)"));

        Assert.Equal(6, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_read_from_the_requirement_items_of_the_snippet()
    {
        var vacancy = await FetchSingleAsync(Job(
            snippet: "<p>A 2 year old startup.</p><p><strong>Requirements:</strong></p><ul><li>3+ years of C#</li><li>2 years of SQL</li></ul>"));

        Assert.Equal(3, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_the_largest_of_the_title_and_the_requirement_items()
    {
        var vacancy = await FetchSingleAsync(Job(
            title: "Senior .NET Developer (6+ years)",
            snippet: "<p><strong>Requirements:</strong></p><ul><li>3+ years of C#</li></ul>"));

        Assert.Equal(6, vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_ignores_years_in_the_snippet_outside_the_requirements()
    {
        var vacancy = await FetchSingleAsync(Job(
            snippet: "<p>We are a 2 year old startup with a 10 years long roadmap.</p>"
                + "<p><strong>Responsibilities:</strong></p><ul><li>Build things for 7 years of customers</li></ul>"
                + "<p><strong>Requirements:</strong></p><ul><li>C# and SQL</li></ul>"
                + "<p><strong>We offer:</strong></p><ul><li>15 years of stability</li></ul>"));

        Assert.Null(vacancy.MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_null_when_nothing_is_found_in_the_fixture()
    {
        // The first fixture snippet says "Вимоги: 5+ років ..." as running text, not as a requirement item.
        var vacancies = await FetchFixtureAsync();

        Assert.All(vacancies, v => Assert.Null(v.MinYearsExperience));
    }

    [Fact]
    public async Task MinYears_is_null_without_a_snippet()
    {
        var vacancy = await FetchSingleAsync(Job(snippet: null));

        Assert.Null(vacancy.MinYearsExperience);
        Assert.Null(vacancy.Description);
    }

    [Theory]
    [InlineData("6402917753301214820")]
    [InlineData("-3735906425358868903")]
    [InlineData("42")]
    public async Task SourceId_of_a_numeric_id_is_its_digits(string id)
    {
        var vacancy = await FetchSingleAsync(Job(id: id));

        Assert.Equal(id, vacancy.SourceId);
    }

    [Fact]
    public async Task SourceId_of_a_string_id_is_that_string()
    {
        var vacancy = await FetchSingleAsync(Job(id: "\" abc-1 \""));

        Assert.Equal("abc-1", vacancy.SourceId);
    }

    [Fact]
    public async Task SourceId_without_an_id_is_a_stable_hash_of_the_link()
    {
        var first = await FetchSingleAsync(Job(id: null, link: "https://ua.jooble.org/jdp/7036641128894556102"));
        var again = await FetchSingleAsync(Job(id: null, link: "https://ua.jooble.org/jdp/7036641128894556102"));
        var other = await FetchSingleAsync(Job(id: null, link: DefaultLink));

        Assert.Equal("link-344ba50be8ca0c21", first.SourceId);
        Assert.Equal(first.SourceId, again.SourceId);
        Assert.Equal("link-dc2f5d73c4e904ab", other.SourceId);
    }

    [Fact]
    public async Task A_job_without_an_id_that_both_queries_return_is_one_vacancy()
    {
        var json = Response(Job(id: null));

        var vacancies = await FetchAsync(_ => Ok(json));

        Assert.Single(vacancies);
    }

    [Fact]
    public async Task Job_without_a_usable_title_or_link_is_skipped_while_the_others_are_kept()
    {
        var json = Response(
            Job(id: "1", title: null, link: "https://ua.jooble.org/jdp/1"),
            Job(id: "2", title: "   ", link: "https://ua.jooble.org/jdp/2"),
            Job(id: "3", link: null),
            Job(id: "4", link: "not a url"),
            Job(id: "5", link: "javascript:alert(1)"),
            Job(id: "6", link: "ftp://ua.jooble.org/jdp/6"),
            Job(id: "7", title: "Good one", link: "https://ua.jooble.org/jdp/7"))
            .Replace("""]}""", """,42,null,"text",[1]]}""");

        var vacancies = await FetchAsync(_ => Ok(json));

        var vacancy = Assert.Single(vacancies);
        Assert.Equal("7", vacancy.SourceId);
        Assert.Equal("Good one", vacancy.Title);
    }

    [Fact]
    public async Task Empty_jobs_list_returns_empty_list()
    {
        var handler = new StubHandler(_ => Ok("""{"totalCount":0,"jobs":[]}"""));

        var vacancies = await SourceFor(handler).FetchAsync(CancellationToken.None);

        Assert.Empty(vacancies);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task One_empty_query_and_one_with_jobs_returns_the_jobs()
    {
        var json = Response(Job(id: "1"));

        var vacancies = await FetchAsync(request => Ok(request.Location == "remote" ? """{"totalCount":0,"jobs":[]}""" : json));

        Assert.Equal("1", Assert.Single(vacancies).SourceId);
    }

    [Fact]
    public async Task HTTP_error_throws()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => FetchAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task Access_denied_says_that_the_key_is_wrong_or_for_another_jooble_domain()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => FetchAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("403", exception.Message);
        Assert.Contains("domain", exception.Message);
    }

    [Fact]
    public async Task Network_error_throws_HttpRequestException()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => FetchAsync(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused")));
    }

    [Fact]
    public async Task A_failing_second_query_throws_instead_of_returning_half_of_the_vacancies()
    {
        var json = Response(Job(id: "1"));

        await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(request =>
            request.Location == "remote" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(json)));
    }

    [Theory]
    [InlineData("<html><body>Just a moment...</body></html>")]
    [InlineData("")]
    [InlineData("{\"totalCount\":1,\"jobs\":[{\"id\":1,")]
    public async Task Invalid_json_throws(string body)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(body)));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"error":"quota exceeded"}""")]
    [InlineData("""{"totalCount":0}""")]
    [InlineData("""{"totalCount":1,"jobs":{"id":1}}""")]
    [InlineData("""{"totalCount":1,"jobs":null}""")]
    public async Task Valid_json_that_is_not_a_search_response_throws(string body)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(body)));
    }

    [Fact]
    public async Task Jobs_but_none_readable_throws()
    {
        var json = Response(Job(title: null), Job(id: "2", link: null));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(json)));

        Assert.Contains("2", exception.Message);
    }

    [Theory]
    [InlineData("http500")]
    [InlineData("http403")]
    [InlineData("http404")]
    [InlineData("network")]
    [InlineData("invalid-json")]
    [InlineData("empty-body")]
    [InlineData("not-a-search-response")]
    [InlineData("none-mappable")]
    [InlineData("second-query-fails")]
    [InlineData("throttled-after:http500")]
    [InlineData("throttled-after:http403")]
    [InlineData("throttled-after:network")]
    [InlineData("throttled-after:invalid-json")]
    [InlineData("throttled-after:second-query-fails")]
    [InlineData("handler-throws-other")]
    [InlineData("handler-throws-timeout")]
    [InlineData("throttled-after:handler-throws-other")]
    [InlineData("throttled-after:handler-throws-timeout")]
    public async Task Exception_text_never_contains_api_key(string scenario)
    {
        const string secret = "SECRET-KEY-0123456789abcdef";
        const string throttledPrefix = "throttled-after:";

        // "throttled-after:X" checks the exception of the call INSIDE the interval that follows a fetch that failed as X.
        var throttled = scenario.StartsWith(throttledPrefix, StringComparison.Ordinal);
        var failure = throttled ? scenario[throttledPrefix.Length..] : scenario;
        HttpResponseMessage Respond(Recorded request)
        {
            // Everything a failing server, proxy or the HTTP stack could echo back contains the key.
            var echo = $"upstream error for {request.Uri}";
            Assert.Contains(secret, echo);

            return failure switch
            {
                "http500" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(echo) },
                "http403" => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(echo) },
                "http404" => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(echo) },
                "network" => throw new HttpRequestException(
                    HttpRequestError.NameResolutionError,
                    $"No such host is known ({request.Uri})",
                    new InvalidOperationException($"inner failure at {request.Uri}")),
                "invalid-json" => Ok($"<html>{echo}</html>"),
                "empty-body" => Ok(""),
                "not-a-search-response" => Ok($$"""{"error":"{{echo}}"}"""),
                "none-mappable" => Ok($$"""{"totalCount":1,"jobs":[{"id":1,"title":"{{echo}}"}]}"""),
                // Exceptions of the framework, with the URL in the message and in an inner exception.
                "handler-throws-other" => throw new IOException(
                    $"disk says no for {request.Uri}",
                    new InvalidOperationException($"inner failure at {request.Uri}")),
                "handler-throws-timeout" => throw new TaskCanceledException(
                    $"timed out for {request.Uri}",
                    new TimeoutException($"timeout at {request.Uri}")),
                "second-query-fails" => request.Location == "remote"
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(echo) }
                    : Ok(Response(Job(id: "1"))),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        // The configured base URL changes where the key goes, not whether it may be shown.
        var time = new FakeTime();
        var handler = new StubHandler(Respond);
        var source = SourceFor(handler, apiKey: secret, baseUrl: "https://ua.jooble.org/api/", time: time);
        var requestsBefore = 0;
        if (throttled)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => source.FetchAsync(CancellationToken.None));
            requestsBefore = handler.Requests.Count;
            time.Advance(TimeSpan.FromMinutes(5));
        }

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => source.FetchAsync(CancellationToken.None));

        if (throttled)
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Contains("failed", exception.Message);
            Assert.Equal(requestsBefore, handler.Requests.Count);
        }

        // ToString covers the message, the stack trace and every inner exception.
        Assert.DoesNotContain(secret, exception.ToString());
        Assert.DoesNotContain(secret, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("https://ua.jooble.org/api", "https://ua.jooble.org/api/test-key-123")]
    [InlineData("https://ua.jooble.org/api/", "https://ua.jooble.org/api/test-key-123")]
    [InlineData("https://ua.jooble.org/api///", "https://ua.jooble.org/api/test-key-123")]
    [InlineData("https://uk.jooble.org/api", "https://uk.jooble.org/api/test-key-123")]
    public async Task Requests_go_to_the_configured_base_url(string baseUrl, string expected)
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, baseUrl: baseUrl).FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(expected, request.Uri!.AbsoluteUri));
    }

    [Fact]
    public void The_default_base_url_is_the_jooble_org_api()
    {
        Assert.Equal("https://jooble.org/api", new JoobleOptions().BaseUrl);
        Assert.Equal(720, new JoobleOptions().MinFetchIntervalMinutes);
    }

    [Fact]
    public async Task Second_fetch_inside_the_interval_makes_no_request_and_returns_empty()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        var first = await source.FetchAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        time.Advance(TimeSpan.FromMinutes(719));
        var second = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_after_the_interval_asks_Jooble_again()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(720));
        var again = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(8, again.Count);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task The_interval_restarts_with_every_real_fetch()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(720));
        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(719));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task The_interval_is_configurable()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, minFetchIntervalMinutes: 5, time: time);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(4));
        var inside = await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var after = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.NotEmpty(after);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failed_first_fetch_still_starts_the_interval()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source = SourceFor(handler, time: time);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(60));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
        time.Advance(TimeSpan.FromMinutes(660));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_fetch_that_failed_on_the_second_query_still_starts_the_interval()
    {
        var time = new FakeTime();
        var json = Response(Job(id: "1"));
        var handler = new StubHandler(request =>
            request.Location == "remote" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(json));
        var source = SourceFor(handler, time: time);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Inside_the_interval_after_a_failed_fetch_the_call_throws_with_the_failure_and_the_next_attempt()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source = SourceFor(handler, time: time);

        var original = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(90));
        var throttled = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        // A new exception, not the stored one, without an inner exception. The fetch started at 08:00 UTC, so the
        // next attempt is due 720 minutes later, whenever inside the interval the call is made.
        Assert.NotSame(original, throttled);
        Assert.Null(throttled.InnerException);
        Assert.Contains("failed", throttled.Message);
        Assert.Contains(original.Message.TrimEnd('.'), throttled.Message);
        Assert.Contains("2026-10-22T20:00:00Z", throttled.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Every_call_inside_the_interval_after_a_failure_throws_so_the_down_alert_can_fire()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var source = SourceFor(handler, time: time);
        var health = new SourceHealth(failureThreshold: 3);
        var alertDue = false;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => source.FetchAsync(CancellationToken.None));
            alertDue = health.RecordFailure(source.Source);
            time.Advance(TimeSpan.FromMinutes(15));
        }

        Assert.True(alertDue);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task After_a_failure_the_next_real_fetch_replaces_the_remembered_outcome()
    {
        var time = new FakeTime();
        var fail = true;
        var handler = new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        fail = false;
        time.Advance(TimeSpan.FromMinutes(720));
        var fetched = await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(fetched);
        Assert.Empty(inside);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task After_a_success_a_later_failure_is_what_the_calls_inside_the_next_interval_report()
    {
        var time = new FakeTime();
        var fail = false;
        var handler = new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await source.FetchAsync(CancellationToken.None);
        fail = true;
        time.Advance(TimeSpan.FromMinutes(720));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(15));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failure_that_is_not_ours_is_reported_by_its_type_name_only()
    {
        const string secret = "SECRET-KEY-0123456789abcdef";
        var time = new FakeTime();
        var handler = new StubHandler(request => throw new IOException($"disk says no for {request.Uri}"));
        var source = SourceFor(handler, apiKey: secret, time: time);

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        var throttled = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        // The framework's exception is replaced by one built here: its text, stack trace and inner exceptions are gone.
        Assert.Contains("IOException", first.Message);
        Assert.Null(first.InnerException);
        Assert.DoesNotContain(secret, first.ToString());
        Assert.Contains("IOException", throttled.Message);
        Assert.DoesNotContain(secret, throttled.ToString());
    }

    [Fact]
    public async Task A_timeout_while_the_caller_has_not_cancelled_is_a_failure_and_is_sanitised()
    {
        const string secret = "SECRET-KEY-0123456789abcdef";
        var handler = new StubHandler(request => throw new TaskCanceledException(
            $"The request was canceled due to the configured HttpClient.Timeout (https://x/{request.Uri})",
            new TimeoutException("The operation was canceled.")));
        var source = SourceFor(handler, apiKey: secret);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Contains("TaskCanceledException", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString());
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_unchanged_and_is_not_remembered_as_a_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cancellation.Cancel();

            throw new OperationCanceledException(cancellation.Token);
        });
        var source = SourceFor(handler, time: new FakeTime());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(cancellation.Token));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.IsNotType<HttpRequestException>(exception);
        Assert.Empty(inside);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_interval_is_measured_from_the_start_of_the_fetch()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ =>
        {
            time.Advance(TimeSpan.FromMinutes(721));

            return Ok(Response());
        });
        var source = SourceFor(handler, time: time);

        await source.FetchAsync(CancellationToken.None);
        await source.FetchAsync(CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Missing_api_key_throws_immediately_whatever_the_interval_says()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var settings = SettingsFor();
        var source = new JoobleSource(new HttpClient(handler), Options.Create(settings), time);

        await source.FetchAsync(CancellationToken.None);
        settings.Sources.Jooble.ApiKey = "";
        var exception = await Assert.ThrowsAsync<SourceNotConnectedException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Contains("JobWatcher__Sources__Jooble__ApiKey", exception.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_missing_key_uses_no_quota_so_it_does_not_start_the_interval()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var settings = SettingsFor(apiKey: "");
        var source = new JoobleSource(new HttpClient(handler), Options.Create(settings), time);

        await Assert.ThrowsAsync<SourceNotConnectedException>(() => source.FetchAsync(CancellationToken.None));
        settings.Sources.Jooble.ApiKey = ApiKey;
        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(vacancies);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- the log line of a call that makes no request ----

    private const string SkipTemplate =
        "Jooble: no request this time, the next one is due after {NextFetch:O} (MinFetchIntervalMinutes={Minutes})";

    private static void AssertSkipLine(RecordingLogger<JoobleSource>.LogEntry entry, string nextDue, int minutes)
    {
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal($"Jooble: no request this time, the next one is due after {nextDue} (MinFetchIntervalMinutes={minutes})", entry.Message);
        Assert.Equal(SkipTemplate, entry.Fields[RecordingLogger<JoobleSource>.TemplateField]);
        var due = Assert.IsType<DateTime>(entry.Fields["NextFetch"]);
        Assert.Equal(DateTimeKind.Utc, due.Kind);
        Assert.Equal(minutes, Assert.IsType<int>(entry.Fields["Minutes"]));
    }

    [Fact]
    public async Task A_call_inside_the_interval_logs_the_skip_line_with_the_time_the_next_request_is_due()
    {
        var time = new FakeTime();
        var log = new RecordingLogger<JoobleSource>();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time, log: log);

        // A real fetch logs nothing. It started at 08:00 UTC, so the next one is due 720 minutes later.
        await source.FetchAsync(CancellationToken.None);
        Assert.Empty(log.Entries);
        time.Advance(TimeSpan.FromMinutes(15));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.Equal(2, handler.Requests.Count);
        AssertSkipLine(Assert.Single(log.Entries), "2026-10-22T20:00:00.0000000Z", 720);
    }

    [Fact]
    public async Task Every_call_inside_the_interval_logs_one_skip_line_and_a_real_fetch_none()
    {
        var time = new FakeTime();
        var log = new RecordingLogger<JoobleSource>();
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())), time: time, log: log);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(690));
        await source.FetchAsync(CancellationToken.None);
        Assert.Equal(2, log.Entries.Count);
        time.Advance(TimeSpan.FromMinutes(15));
        await source.FetchAsync(CancellationToken.None);

        // The first two calls are inside the first interval (due 20:00), the real fetch at 20:00 logs nothing and
        // starts the next interval, and the call after it names the new due time.
        Assert.Collection(
            log.Entries,
            first => AssertSkipLine(first, "2026-10-22T20:00:00.0000000Z", 720),
            second => AssertSkipLine(second, "2026-10-22T20:00:00.0000000Z", 720),
            third => AssertSkipLine(third, "2026-10-23T08:00:00.0000000Z", 720));
    }

    [Fact]
    public async Task The_skip_line_names_the_configured_interval()
    {
        var time = new FakeTime();
        var log = new RecordingLogger<JoobleSource>();
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())), minFetchIntervalMinutes: 5, time: time, log: log);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(4));
        await source.FetchAsync(CancellationToken.None);

        AssertSkipLine(Assert.Single(log.Entries), "2026-10-22T08:05:00.0000000Z", 5);
    }

    [Fact]
    public async Task A_replayed_failure_inside_the_interval_still_throws_and_adds_no_skip_line()
    {
        var time = new FakeTime();
        var log = new RecordingLogger<JoobleSource>();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source = SourceFor(handler, time: time, log: log);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(15));
        var replayed = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        // The exception already names the due time and the caller logs it as a warning.
        Assert.Contains("2026-10-22T20:00:00Z", replayed.Message);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Without_a_logger_a_call_inside_the_interval_still_returns_an_empty_list_quietly()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time, log: null);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_skip_line_shows_neither_the_api_key_nor_any_url()
    {
        const string secret = "SECRET-KEY-0123456789abcdef";
        var time = new FakeTime();
        var log = new RecordingLogger<JoobleSource>();
        var source = SourceFor(
            new StubHandler(_ => Ok(FixtureJson())), apiKey: secret, baseUrl: "https://ua.jooble.org/api/", time: time, log: log);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        await source.FetchAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Matches(
            @"^Jooble: no request this time, the next one is due after \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d+Z \(MinFetchIntervalMinutes=\d+\)$",
            entry.Message);
        Assert.DoesNotContain(secret, entry.Message);
        Assert.DoesNotContain("jooble.org", entry.Message);
        Assert.All(entry.Fields.Values, value => Assert.DoesNotContain(secret, value?.ToString()));
    }
}
