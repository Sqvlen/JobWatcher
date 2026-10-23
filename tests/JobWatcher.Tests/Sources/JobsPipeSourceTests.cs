// FIXTURE NOTE: Fixtures/jobspipe-response.json has the field names, value types and shapes of real JobsPipe search
// responses (read with a free key on 2026-10-23, see docs/sources/jobspipe.md), but its vacancies, companies, links
// and descriptions are made up or rewritten, so that no employer's text is copied into the repository.
// Tests never touch the network: every request goes to a stub HttpMessageHandler.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Parsing;
using JobWatcher.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Sources;

public sealed class JobsPipeSourceTests
{
    private const string ApiKey = "jp_live_test-key-0123456789";
    private const string SearchUrl = "https://api.jobspipe.dev/v1/jobs/search";

    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jobspipe-response.json");

    // Ids of the fixture jobs that the tests below rely on (a number, and strings of the form "<country>-<n>").
    private const string UsRemote = "48210731";
    private const string SpainRemote = "es-7731";
    private const string IndiaRemote = "in-5520";
    private const string UkRemote = "gb-9034";
    private const string UkraineRemote = "ua-3318";
    private const string NoCountry = "anywhere-2201";
    private const string SwitzerlandRemote = "ch-6147";
    private const string KyivHybrid = "ua-3319";
    private const string PolandJunior = "pl-8002";

    private sealed record Recorded(HttpMethod Method, Uri? Uri, string ContentType, string UserAgent, string? Authorization, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public string[] Strings(string property) =>
            Json.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
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
                request.Headers.Authorization?.ToString(),
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
        private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

        private long ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => ticks;

        public override DateTimeOffset GetUtcNow() => Start.AddMilliseconds(ticks);

        public void Advance(TimeSpan by) => ticks += (long)by.TotalMilliseconds;
    }

    private static WatcherOptions SettingsFor(
        string apiKey = ApiKey,
        string keyword = ".NET",
        string[]? synonyms = null,
        string? baseUrl = null,
        int? minFetchIntervalMinutes = null,
        int? maxAgeDays = null,
        int? pageSize = null,
        string[]? excludedSources = null)
    {
        var settings = new WatcherOptions
        {
            Profile = { Keyword = keyword, Synonyms = synonyms ?? ["C#", "ASP.NET", "dotnet"] },
            Sources = { JobsPipe = { Enabled = true, ApiKey = apiKey, ExcludedSources = excludedSources ?? ["linkedin"] } },
        };
        var jobsPipe = settings.Sources.JobsPipe;
        jobsPipe.BaseUrl = baseUrl ?? jobsPipe.BaseUrl;
        // The tests of the interval run on an hour, whatever the default is: ShippedDefaultsTests pins the default itself.
        jobsPipe.MinFetchIntervalMinutes = minFetchIntervalMinutes ?? 60;
        jobsPipe.MaxAgeDays = maxAgeDays ?? jobsPipe.MaxAgeDays;
        jobsPipe.PageSize = pageSize ?? jobsPipe.PageSize;

        return settings;
    }

    private static JobsPipeSource SourceFor(
        StubHandler handler,
        WatcherOptions? settings = null,
        TimeProvider? time = null,
        ILogger<JobsPipeSource>? log = null) =>
        new(new HttpClient(handler), Options.Create(settings ?? SettingsFor()), time, log);

    private static string FixtureJson() => File.ReadAllText(FixturePath);

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(Func<Recorded, HttpResponseMessage> respond) =>
        SourceFor(new StubHandler(respond)).FetchAsync(CancellationToken.None);

    private static Task<IReadOnlyList<Vacancy>> FetchFixtureAsync() => FetchAsync(_ => Ok(FixtureJson()));

    private static async Task<Vacancy> FixtureVacancyAsync(string sourceId) =>
        (await FetchFixtureAsync()).Single(v => v.SourceId == sourceId);

    // A job object as the API returns it; null omits the property. `id` is JSON text (a number or a string).
    private static JsonObject Job(
        string? id = "\"100\"",
        string? title = "Dev",
        string? company = "Acme",
        string? location = "Remote",
        string? url = "https://example.test/jobs/100",
        string? description = "A job.",
        bool? remote = true,
        string? arrangement = null,
        string? countryCode = null,
        string? country = null)
    {
        var job = new JsonObject();
        AddIfSet(job, "id", id is null ? null : JsonNode.Parse(id));
        AddIfSet(job, "job_title", title);
        AddIfSet(job, "company", company);
        AddIfSet(job, "location", location);
        AddIfSet(job, "url", url);
        AddIfSet(job, "description", description);
        AddIfSet(job, "remote", remote);
        AddIfSet(job, "work_arrangement", arrangement);
        AddIfSet(job, "country_code", countryCode);
        AddIfSet(job, "country", country);

        return job;
    }

    private static void AddIfSet(JsonObject job, string name, JsonNode? value)
    {
        if (value is not null)
        {
            job[name] = value;
        }
    }

    private static void AddIfSet(JsonObject job, string name, string? value) => AddIfSet(job, name, value is null ? null : JsonValue.Create(value));

    private static void AddIfSet(JsonObject job, string name, bool? value) => AddIfSet(job, name, value is null ? null : JsonValue.Create(value.Value));

    private static string Response(params JsonObject[] jobs) =>
        new JsonObject { ["metadata"] = new JsonObject { ["credits_charged"] = jobs.Length }, ["data"] = new JsonArray(jobs.Select(job => (JsonNode)job).ToArray()) }
            .ToJsonString();

    private static async Task<Vacancy> FetchSingleAsync(JsonObject job) =>
        Assert.Single(await FetchAsync(_ => Ok(Response(job))));

    [Fact]
    public async Task Source_is_JobsPipe_on_the_adapter_and_every_vacancy()
    {
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())));

        var vacancies = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(VacancySource.JobsPipe, source.Source);
        Assert.NotEmpty(vacancies);
        Assert.All(vacancies, vacancy => Assert.Equal(VacancySource.JobsPipe, vacancy.Source));
    }

    [Fact]
    public async Task Posts_one_search_to_the_search_endpoint()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(SearchUrl, request.Uri!.AbsoluteUri);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.StartsWith("JobWatcher/", request.UserAgent);
    }

    [Theory]
    [InlineData("https://api.jobspipe.dev", "https://api.jobspipe.dev/v1/jobs/search")]
    [InlineData("https://api.jobspipe.dev/", "https://api.jobspipe.dev/v1/jobs/search")]
    [InlineData("https://proxy.example/jp/", "https://proxy.example/jp/v1/jobs/search")]
    public async Task Requests_go_to_the_configured_base_url(string baseUrl, string expected)
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, SettingsFor(baseUrl: baseUrl)).FetchAsync(CancellationToken.None);

        Assert.Equal(expected, Assert.Single(handler.Requests).Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task The_key_travels_as_a_bearer_token_and_never_in_the_url_or_the_body()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler).FetchAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        Assert.DoesNotContain(ApiKey, request.Uri!.AbsoluteUri);
        Assert.DoesNotContain(ApiKey, request.Body);
    }

    [Fact]
    public async Task Asks_for_remote_vacancies_with_the_profile_terms_the_age_and_the_page_size()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, SettingsFor(maxAgeDays: 7, pageSize: 10)).FetchAsync(CancellationToken.None);

        var body = Assert.Single(handler.Requests).Json;
        Assert.True(body.GetProperty("remote").GetBoolean());
        Assert.Equal(7, body.GetProperty("posted_at_max_age_days").GetInt32());
        Assert.Equal(10, body.GetProperty("limit").GetInt32());
        Assert.Equal([".NET", "C#", "ASP.NET", "dotnet"], Assert.Single(handler.Requests).Strings("job_title_or"));
    }

    [Fact]
    public async Task Asks_for_nothing_else_so_that_no_filter_narrows_the_search_by_accident()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler).FetchAsync(CancellationToken.None);

        var names = Assert.Single(handler.Requests).Json.EnumerateObject().Select(property => property.Name).Order();
        Assert.Equal(
            new[] { "job_title_or", "limit", "posted_at_max_age_days", "remote", "source_not" }.Order(),
            names);
    }

    [Fact]
    public async Task The_title_terms_are_the_keyword_and_the_synonyms_without_repeats_or_blanks()
    {
        var handler = new StubHandler(_ => Ok(Response()));
        var settings = SettingsFor(keyword: " .NET ", synonyms: ["C#", ".net", "  ", "dotnet", "C#"]);

        await SourceFor(handler, settings).FetchAsync(CancellationToken.None);

        Assert.Equal([".NET", "C#", "dotnet"], Assert.Single(handler.Requests).Strings("job_title_or"));
    }

    [Fact]
    public async Task Leaves_out_the_excluded_sources()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, SettingsFor(excludedSources: ["linkedin", "Indeed"])).FetchAsync(CancellationToken.None);

        Assert.Equal(["linkedin", "Indeed"], Assert.Single(handler.Requests).Strings("source_not"));
    }

    [Fact]
    public async Task Sends_no_source_filter_when_nothing_is_excluded()
    {
        var handler = new StubHandler(_ => Ok(Response()));

        await SourceFor(handler, SettingsFor(excludedSources: [])).FetchAsync(CancellationToken.None);

        Assert.False(Assert.Single(handler.Requests).Json.TryGetProperty("source_not", out _));
    }

    [Fact]
    public async Task Maps_every_usable_job_of_the_fixture_once_in_the_order_of_the_response()
    {
        var vacancies = await FetchFixtureAsync();

        Assert.Equal(
            [UsRemote, SpainRemote, IndiaRemote, UkRemote, UkraineRemote, NoCountry, SwitzerlandRemote, KyivHybrid, PolandJunior],
            vacancies.Select(v => v.SourceId));
    }

    [Fact]
    public async Task Maps_job_fields_to_vacancy()
    {
        var vacancy = await FixtureVacancyAsync(UsRemote);

        Assert.Equal("Senior Full-Stack ASP.NET Core Developer", vacancy.Title);
        Assert.Equal("Example Health Systems", vacancy.Company);
        Assert.Equal("https://www.indeed.com/viewjob?jk=0000000000000001", vacancy.Url);
        Assert.Equal("United States", vacancy.Location);
        Assert.Equal(WorkMode.Remote, vacancy.WorkMode);
        Assert.StartsWith("Example Health Systems builds software", vacancy.Description);
    }

    [Fact]
    public async Task The_link_is_the_posting_url_and_falls_back_to_the_source_url()
    {
        var withUrl = await FetchSingleAsync(Job(url: "https://example.test/a"));
        var job = Job(url: null);
        job["source_url"] = "https://example.test/b";
        var withSourceUrl = await FetchSingleAsync(job);

        Assert.Equal("https://example.test/a", withUrl.Url);
        Assert.Equal("https://example.test/b", withSourceUrl.Url);
    }

    [Fact]
    public async Task The_salary_is_the_text_of_the_posting_when_it_has_one()
    {
        var vacancy = await FixtureVacancyAsync(UkRemote);

        Assert.Equal("£60K - £75K a year", vacancy.Salary);
    }

    [Fact]
    public async Task Without_salary_text_the_posted_annual_range_is_written_out_as_a_yearly_salary()
    {
        var vacancy = await FixtureVacancyAsync(UsRemote);

        Assert.Equal("$68076–118188 / year", vacancy.Salary);
        Assert.Equal(new SalaryRange(68076m, 118188m, "USD", SalaryPeriod.Year), SalaryParser.Parse(vacancy.Salary));
    }

    [Fact]
    public async Task Without_any_posted_pay_the_salary_is_null()
    {
        var vacancy = await FixtureVacancyAsync(SpainRemote);

        Assert.Null(vacancy.Salary);
    }

    [Fact]
    public async Task The_benchmark_estimates_are_never_shown_as_the_salary()
    {
        var job = Job();
        job["estimated_min_annual_salary_usd"] = 90000;
        job["estimated_max_annual_salary_usd"] = 140000;
        job["salary_currency"] = "USD";

        var vacancy = await FetchSingleAsync(job);

        Assert.Null(vacancy.Salary);
    }

    [Fact]
    public async Task An_annual_range_of_the_estimated_type_is_a_benchmark_and_not_the_salary()
    {
        var job = Job();
        job["salary_type"] = "estimated";
        job["salary_currency"] = "USD";
        job["min_annual_salary"] = 90000;
        job["max_annual_salary"] = 140000;

        var vacancy = await FetchSingleAsync(job);

        Assert.Null(vacancy.Salary);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_or_blank_description_is_null(string? description)
    {
        var vacancy = await FetchSingleAsync(Job(description: description));

        Assert.Null(vacancy.Description);
    }

    [Fact]
    public async Task The_description_is_kept_as_published()
    {
        const string text = "Intro.\n\nRequirements\n\n- Item one\n\n- Item two";

        var vacancy = await FetchSingleAsync(Job(description: text));

        Assert.Equal(text, vacancy.Description);
    }

    [Fact]
    public async Task The_requirements_of_a_plain_text_description_are_found_by_the_extractor()
    {
        var vacancy = await FixtureVacancyAsync(UsRemote);

        var requirements = RequirementsExtractor.Extract(vacancy.Description);

        Assert.Equal(
            [
                "5+ years of professional experience developing web applications using C# and ASP.NET Core MVC.",
                "Strong knowledge of SQL Server and T-SQL.",
                "Experience with Azure and CI/CD pipelines.",
            ],
            requirements);
    }

    [Theory]
    [InlineData(true, null, WorkMode.Remote)]
    [InlineData(false, null, WorkMode.Office)]
    [InlineData(null, "remote", WorkMode.Remote)]
    [InlineData(null, "hybrid", WorkMode.Office)]
    [InlineData(null, "onsite", WorkMode.Office)]
    [InlineData(false, "hybrid", WorkMode.Office)]
    [InlineData(null, null, WorkMode.Unknown)]
    public async Task WorkMode_follows_the_remote_flag_and_the_work_arrangement(bool? remote, string? arrangement, WorkMode expected)
    {
        var vacancy = await FetchSingleAsync(Job(remote: remote, arrangement: arrangement));

        Assert.Equal(expected, vacancy.WorkMode);
    }

    [Theory]
    [InlineData(UsRemote, "US")]
    [InlineData(UkRemote, "UK")]
    [InlineData(UkraineRemote, "Ukraine")]
    [InlineData(SpainRemote, "EU")]
    [InlineData(PolandJunior, "EU")]
    [InlineData(SwitzerlandRemote, "Europe")]
    [InlineData(IndiaRemote, "India")]
    [InlineData(NoCountry, null)]
    [InlineData(KyivHybrid, null)]
    public async Task RemoteRestrictedTo_is_the_region_of_the_country_the_remote_job_sits_in(string sourceId, string? expected)
    {
        var vacancy = await FixtureVacancyAsync(sourceId);

        Assert.Equal(expected, vacancy.RemoteRestrictedTo);
    }

    [Fact]
    public async Task A_country_the_profile_does_not_know_is_named_so_that_the_filter_can_reject_it()
    {
        var named = await FetchSingleAsync(Job(countryCode: "AU", country: "Australia"));
        var codeOnly = await FetchSingleAsync(Job(countryCode: "BR"));

        Assert.Equal("Australia", named.RemoteRestrictedTo);
        Assert.Equal("BR", codeOnly.RemoteRestrictedTo);
    }

    [Theory]
    [InlineData("ua")]
    [InlineData(" UA ")]
    public async Task The_country_code_is_read_whatever_its_case_and_spacing(string code)
    {
        var vacancy = await FetchSingleAsync(Job(countryCode: code, country: "Ukraine"));

        Assert.Equal("Ukraine", vacancy.RemoteRestrictedTo);
    }

    [Theory]
    [InlineData("Junior .NET Developer", true)]
    [InlineData("Jr C# Developer", true)]
    [InlineData("Senior .NET Developer", false)]
    [InlineData("Junior+ / Middle .NET Developer", true)]
    public async Task IsJunior_looks_at_the_title(string title, bool expected)
    {
        var vacancy = await FetchSingleAsync(Job(title: title));

        Assert.Equal(expected, vacancy.IsJunior);
    }

    [Fact]
    public async Task IsJunior_is_not_taken_from_the_description()
    {
        var vacancy = await FetchSingleAsync(Job(description: "We also take on interns and junior staff."));

        Assert.False(vacancy.IsJunior);
    }

    [Fact]
    public async Task MinYears_is_read_from_the_requirement_items_of_the_description()
    {
        Assert.Equal(5, (await FixtureVacancyAsync(UsRemote)).MinYearsExperience);
        Assert.Equal(3, (await FixtureVacancyAsync(IndiaRemote)).MinYearsExperience);
    }

    [Fact]
    public async Task MinYears_is_null_when_nothing_is_stated()
    {
        Assert.Null((await FixtureVacancyAsync(UkraineRemote)).MinYearsExperience);
        Assert.Null((await FixtureVacancyAsync(NoCountry)).MinYearsExperience);
    }

    [Theory]
    [InlineData("\"abc-1\"", "abc-1")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    [InlineData("\"https://himalayas.app/companies/x/jobs/y-1\"", "https://himalayas.app/companies/x/jobs/y-1")]
    public async Task SourceId_is_the_id_as_the_api_sent_it(string id, string expected)
    {
        var vacancy = await FetchSingleAsync(Job(id: id));

        Assert.Equal(expected, vacancy.SourceId);
    }

    [Fact]
    public async Task Jobs_without_a_usable_id_title_or_web_link_are_skipped_while_the_others_are_kept()
    {
        var response = Response(
            Job(id: "\"keep-1\""),
            Job(id: null),
            Job(id: "\"  \""),
            Job(id: "\"no-title\"", title: null),
            Job(id: "\"blank-title\"", title: "   "),
            Job(id: "\"no-link\"", url: null),
            Job(id: "\"bad-link\"", url: "ftp://example.test/job"),
            Job(id: "\"relative-link\"", url: "/jobs/1"),
            Job(id: "\"keep-2\""));

        var vacancies = await FetchAsync(_ => Ok(response));

        Assert.Equal(["keep-1", "keep-2"], vacancies.Select(v => v.SourceId));
    }

    [Fact]
    public async Task The_same_id_twice_in_one_response_is_one_vacancy()
    {
        var response = Response(Job(id: "\"7\"", title: "First"), Job(id: "\"7\"", title: "Second"));

        var vacancies = await FetchAsync(_ => Ok(response));

        Assert.Equal("First", Assert.Single(vacancies).Title);
    }

    [Fact]
    public async Task An_empty_list_of_jobs_returns_an_empty_list()
    {
        var vacancies = await FetchAsync(_ => Ok(Response()));

        Assert.Empty(vacancies);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401", "API key")]
    [InlineData(HttpStatusCode.PaymentRequired, "402", "next UTC month")]
    [InlineData(HttpStatusCode.TooManyRequests, "429", "per second")]
    [InlineData(HttpStatusCode.GatewayTimeout, "504", "narrow")]
    [InlineData(HttpStatusCode.InternalServerError, "500", "InternalServerError")]
    public async Task An_error_status_throws_and_says_what_it_means(HttpStatusCode status, string code, string expectedText)
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(_ => new HttpResponseMessage(status)));

        Assert.Contains($"HTTP {code}", exception.Message);
        Assert.Contains(expectedText, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task The_error_text_of_the_api_is_part_of_the_message()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"Invalid search filters: unknown filter: 'x'\"}", Encoding.UTF8, "application/json"),
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(_ => response));

        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Invalid search filters: unknown filter: 'x'", exception.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{\"error\":\"unexpected\"")]
    public async Task An_error_body_that_is_not_json_is_ignored_and_the_status_still_says_what_happened(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(_ => response));

        Assert.Contains("HTTP 502", exception.Message);
        Assert.DoesNotContain(body, exception.Message);
    }

    [Fact]
    public async Task A_very_long_api_error_text_is_cut()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"{{\"error\":\"{new string('x', 5000)}\"}}", Encoding.UTF8, "application/json"),
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(_ => response));

        Assert.True(exception.Message.Length < 500, $"message is {exception.Message.Length} characters long");
    }

    [Fact]
    public async Task A_network_error_throws_HttpRequestException_without_the_original_as_inner_exception()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused", null, null));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => SourceFor(handler).FetchAsync(CancellationToken.None));

        Assert.Contains("JobsPipe request failed", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"data\":")]
    public async Task A_body_that_is_not_json_throws_InvalidDataException(string body)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(body)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"error\":\"nothing\"}")]
    public async Task Valid_json_that_is_not_a_search_response_throws_InvalidDataException(string body)
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(body)));

        Assert.Contains("list of jobs", exception.Message);
    }

    [Fact]
    public async Task Jobs_but_none_readable_throws_InvalidDataException()
    {
        var response = Response(Job(id: null), Job(title: null), Job(url: null));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(_ => Ok(response)));

        Assert.Contains("None of the 3 jobs", exception.Message);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("network")]
    [InlineData("invalid-json")]
    [InlineData("timeout")]
    public async Task Exception_text_never_contains_the_api_key(string scenario)
    {
        var handler = new StubHandler(request => scenario switch
        {
            "unauthorized" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "network" => throw new HttpRequestException($"failed: {request.Authorization} {request.Uri}"),
            "timeout" => throw new TaskCanceledException($"timeout {request.Authorization}", new TimeoutException()),
            _ => Ok("not json"),
        });
        var source = SourceFor(handler, time: new FakeTime());

        var first = await Assert.ThrowsAnyAsync<Exception>(() => source.FetchAsync(CancellationToken.None));
        var replay = await Assert.ThrowsAnyAsync<Exception>(() => source.FetchAsync(CancellationToken.None));

        Assert.DoesNotContain(ApiKey, first.ToString());
        Assert.DoesNotContain(ApiKey, replay.ToString());
    }

    [Fact]
    public async Task Second_fetch_inside_the_interval_makes_no_request_and_returns_empty()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        var first = await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(59));
        var second = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_after_the_interval_asks_JobsPipe_again()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(60));
        var again = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(9, again.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_interval_is_configurable_and_restarts_with_every_real_fetch()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => Ok(FixtureJson()));
        var source = SourceFor(handler, SettingsFor(minFetchIntervalMinutes: 5), time);

        await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(4));
        var inside = await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var after = await source.FetchAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(4));
        var insideAgain = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.NotEmpty(after);
        Assert.Empty(insideAgain);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failed_fetch_still_starts_the_interval_and_inside_it_the_failure_is_thrown_again()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired));
        var source = SourceFor(handler, time: time);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(30));
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));
        var replayAgain = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Contains("HTTP 402", replay.Message);
        Assert.Contains("2026-10-23T09:00:00Z", replay.Message);
        Assert.Contains("HTTP 402", replayAgain.Message);
    }

    [Fact]
    public async Task After_a_failure_the_next_real_fetch_replaces_the_remembered_outcome()
    {
        var time = new FakeTime();
        var fail = true;
        var handler = new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout) : Ok(FixtureJson()));
        var source = SourceFor(handler, time: time);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(60));
        fail = false;
        var recovered = await source.FetchAsync(CancellationToken.None);
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(recovered);
        Assert.Empty(inside);
    }

    [Fact]
    public async Task A_timeout_while_the_caller_has_not_cancelled_is_a_failure_named_by_its_type_that_the_interval_remembers()
    {
        var time = new FakeTime();
        var handler = new StubHandler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));
        var source = SourceFor(handler, time: time);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Contains("TaskCanceledException", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_and_is_not_remembered_as_a_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cancellation.Cancel();

            throw new OperationCanceledException(cancellation.Token);
        });
        var source = SourceFor(handler, time: new FakeTime());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(cancellation.Token));
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(inside);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_call_inside_the_interval_logs_one_skip_line_with_the_due_time_and_the_interval()
    {
        var time = new FakeTime();
        var log = new RecordingLogger<JobsPipeSource>();
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())), time: time, log: log);

        await source.FetchAsync(CancellationToken.None);
        Assert.Empty(log.Entries);
        time.Advance(TimeSpan.FromMinutes(10));
        await source.FetchAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("JobsPipe: no request this time", entry.Message);
        Assert.Contains("2026-10-23T09:00:00", entry.Message);
        Assert.Contains("MinFetchIntervalMinutes=60", entry.Message);
        Assert.DoesNotContain(ApiKey, entry.Message);
    }

    [Fact]
    public async Task A_replayed_failure_adds_no_skip_line()
    {
        var log = new RecordingLogger<JobsPipeSource>();
        var source = SourceFor(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)), time: new FakeTime(), log: log);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(CancellationToken.None));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Without_a_logger_a_call_inside_the_interval_still_returns_an_empty_list_quietly()
    {
        var source = SourceFor(new StubHandler(_ => Ok(FixtureJson())), time: new FakeTime());

        var first = await source.FetchAsync(CancellationToken.None);
        var inside = await source.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Empty(inside);
    }
}
