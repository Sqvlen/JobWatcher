using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using JobWatcher.Domain;
using JobWatcher.Filtering;
using JobWatcher.Ratings;
using JobWatcher.Sources;
using JobWatcher.Storage;
using JobWatcher.Telegram;
using JobWatcher.Watching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests;

public sealed class CompositionTests
{
    // The real src/JobWatcher/appsettings.json, linked into the test output by the test project.
    private const string ShippedSettingsFile = "shipped-appsettings.json";

    // Fake secrets. The SECRET parts are what the log test looks for, so URL escaping cannot hide a leak.
    private const string BotToken = "123456:TOKEN-SECRET-VALUE";
    private const string TokenSecret = "TOKEN-SECRET-VALUE";
    private const string JoobleKey = "JOOBLE-SECRET-KEY-VALUE";
    private const string JobsPipeKey = "JOBSPIPE-SECRET-KEY-VALUE";
    private const string ChatId = "-100500";

    // The names the typed HttpClients are registered under (the type name), used to replace their primary handlers.
    private const string TelegramClient = nameof(TelegramNotifier);
    private const string DouClient = nameof(DouRssSource);
    private const string JoobleClient = nameof(JoobleSource);
    private const string DjinniClient = nameof(DjinniSource);
    private const string JobsPipeClient = nameof(JobsPipeSource);
    private const string EleksClient = nameof(EleksSource);

    private static readonly string DouFixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dou-dotnet.xml");
    private static readonly string DjinniFixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "djinni-dotnet.xml");

    // A real vacancy page (remote, Ukraine, $2000–2500, 3 years), trimmed to its JSON-LD block; see DjinniVacancyPageTests.
    private static readonly string DjinniPageFixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "djinni-page-854603-remote-ua-salary.html");

    // The fixture item the "new" vacancy of the end-to-end test is cloned from: remote, no region limit, a .NET role.
    private const string ClonedFixtureVacancyId = "366693";
    private const string NewVacancyId = "999001";
    private const string NewVacancyTitle = "Quantum Ledger .NET Engineer";
    private const string NewVacancyLink = "https://jobs.dou.ua/companies/freitty/vacancies/999001/?utm_source=jobsrss";

    // The same for Djinni: the fixture item cloned is a .NET role with no years above the profile's minimum.
    private const string ClonedDjinniVacancyId = "847830";
    private const string NewDjinniVacancyId = "999002";
    private const string NewDjinniVacancyTitle = "Quantum Ledger .NET Engineer (Djinni)";
    private const string NewDjinniVacancyLink = "https://djinni.co/jobs/999002-quantum-ledger-net-engineer/";
    private const string DjinniFeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";

    // ---- configuration and container helpers ----

    // DOU and Jooble are switched on here explicitly and Djinni off: the shipped appsettings.json enables DOU and
    // Djinni, and a user enables Jooble together with its API key. The tests that need Djinni switch it on.
    private static Dictionary<string, string?> ValidSettings(string dataDir) => new()
    {
        ["JobWatcher:Telegram:BotToken"] = BotToken,
        ["JobWatcher:Telegram:ChatId"] = ChatId,
        ["JobWatcher:DataDir"] = dataDir,
        ["JobWatcher:Sources:Dou:Enabled"] = "true",
        ["JobWatcher:Sources:Jooble:Enabled"] = "true",
        ["JobWatcher:Sources:Jooble:ApiKey"] = JoobleKey,
        ["JobWatcher:Sources:Djinni:Enabled"] = "false",
        ["JobWatcher:Sources:Eleks:Enabled"] = "false",
    };

    // What users get: the shipped appsettings.json with the secrets and the test's own settings on top.
    private static IConfiguration BuildConfig(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ShippedSettingsFile, optional: false)
            .AddInMemoryCollection(settings)
            .Build();

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddJobWatcher(BuildConfig(settings));
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "jobwatcher-tests-" + Guid.NewGuid().ToString("N"));

    private static void DeleteIfExists(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void ReplacePrimaryHandler(IServiceCollection services, string clientName, HttpMessageHandler handler) =>
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler);

    private static Dictionary<string, string?> WithSources(
        string dataDir, bool dou, bool jooble, bool djinni = false, bool linkedIn = false, bool eleks = false)
    {
        var settings = ValidSettings(dataDir);
        settings["JobWatcher:Sources:Dou:Enabled"] = dou.ToString();
        settings["JobWatcher:Sources:Jooble:Enabled"] = jooble.ToString();
        settings["JobWatcher:Sources:Djinni:Enabled"] = djinni.ToString();
        settings["JobWatcher:Sources:LinkedIn:Enabled"] = linkedIn.ToString();
        settings["JobWatcher:Sources:Eleks:Enabled"] = eleks.ToString();

        return settings;
    }

    // ---- stubs ----

    private sealed record RecordedRequest(HttpMethod Method, string Url, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public StubHandler(Func<HttpResponseMessage> respond)
            : this(_ => respond())
        {
        }

        public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body));

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Rss(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };

    private static StubHandler TelegramAccepting() =>
        new(() => Json("""{"ok":true,"result":{"message_id":1}}"""));

    /// <summary>Records every log entry of every category and level, with its state values and active scopes.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        public sealed record LogEntry(string Category, LogLevel Level, string Text);

        private sealed class RecordingLogger(string category, RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder();
                text.AppendLine(formatter(state, exception));
                Describe(text, state);
                if (exception is not null)
                {
                    text.AppendLine(exception.ToString());
                }

                owner._scopes.ForEachScope((scope, sb) => Describe(sb, scope), text);
                owner._entries.Enqueue(new LogEntry(category, logLevel, text.ToString()));
            }

            private static void Describe(StringBuilder text, object? state)
            {
                text.AppendLine(state?.ToString());
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        text.AppendLine($"{key}={value}");
                    }
                }
            }
        }
    }

    // ---- composition ----

    [Fact]
    public async Task Container_resolves_IWatchCycle_with_valid_config()
    {
        await using var provider = BuildProvider(ValidSettings(NewTempDir()));

        Assert.IsType<WatchCycle>(provider.GetRequiredService<IWatchCycle>());
    }

    [Fact]
    public async Task Services_are_built_from_the_options()
    {
        var dataDir = NewTempDir();
        var settings = ValidSettings(dataDir);
        settings["JobWatcher:FailureAlertThreshold"] = "5";
        settings["JobWatcher:Profile:Keyword"] = "Rust";
        try
        {
            await using var provider = BuildProvider(settings);

            Assert.Equal(5, provider.GetRequiredService<SourceHealth>().FailureThreshold);
            Assert.IsType<TelegramNotifier>(provider.GetRequiredService<IMessageNotifier>());
            Assert.IsType<NullCompanyRatingProvider>(provider.GetRequiredService<ICompanyRatingProvider>());

            var filter = provider.GetRequiredService<VacancyFilter>();
            Assert.True(filter.Matches(RemoteVacancy("Rust Developer")));
            Assert.False(filter.Matches(RemoteVacancy(".NET Developer")));

            var store = Assert.IsType<SqliteSeenStore>(provider.GetRequiredService<ISeenStore>());
            await store.AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(dataDir, "seen.db")));
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    [Fact]
    public async Task Hosted_service_is_registered()
    {
        await using var provider = BuildProvider(ValidSettings(NewTempDir()));

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is WatcherService);
    }

    [Theory]
    [InlineData(true, false, false, false, "Dou")]
    [InlineData(false, true, false, false, "Jooble")]
    [InlineData(false, false, true, false, "Djinni")]
    [InlineData(false, false, false, true, "LinkedIn")]
    [InlineData(true, true, false, false, "Dou,Jooble")]
    [InlineData(true, true, true, false, "Dou,Jooble,Djinni")]
    [InlineData(true, true, true, true, "Dou,Jooble,Djinni,LinkedIn")]
    public async Task Only_enabled_sources_are_registered(
        bool dou, bool jooble, bool djinni, bool linkedIn, string expectedSources)
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou, jooble, djinni, linkedIn));

        var sources = provider.GetServices<IVacancySource>().Select(source => source.Source);

        Assert.Equal(expectedSources.Split(',').Select(Enum.Parse<VacancySource>), sources);
    }

    [Fact]
    public async Task Shipped_config_runs_on_dou_djinni_and_eleks_with_only_the_telegram_settings()
    {
        var settings = new Dictionary<string, string?>
        {
            ["JobWatcher:Telegram:BotToken"] = BotToken,
            ["JobWatcher:Telegram:ChatId"] = ChatId,
        };
        await using var provider = BuildProvider(settings);

        Assert.IsType<WatchCycle>(provider.GetRequiredService<IWatchCycle>());
        Assert.Equal(
            [VacancySource.Dou, VacancySource.Djinni, VacancySource.Eleks],
            provider.GetServices<IVacancySource>().Select(source => source.Source));
    }

    [Fact]
    public async Task Disabled_sources_are_not_registered()
    {
        // Every source is off. The options validator, which rejects a configuration without any source, runs when
        // the host starts; the container built here does not run it, so this checks the registration alone.
        await using var provider = BuildProvider(
            WithSources(NewTempDir(), dou: false, jooble: false, djinni: false, linkedIn: false));

        Assert.Empty(provider.GetServices<IVacancySource>());
        Assert.Null(provider.GetService<DouRssSource>());
        Assert.Null(provider.GetService<JoobleSource>());
        Assert.Null(provider.GetService<DjinniSource>());
        Assert.Null(provider.GetService<EleksSource>());
    }

    [Fact]
    public async Task LinkedIn_disabled_is_not_registered()
    {
        // The other sources are on, so a source named LinkedIn would stand out; none may exist.
        await using var provider = BuildProvider(
            WithSources(NewTempDir(), dou: true, jooble: true, djinni: true, linkedIn: false));

        var sources = provider.GetServices<IVacancySource>().ToList();

        Assert.DoesNotContain(sources, source => source.Source == VacancySource.LinkedIn);
        Assert.DoesNotContain(sources, source => source is DisabledSource);
    }

    [Fact]
    public async Task LinkedIn_enabled_without_access_registers_DisabledSource()
    {
        // LinkedIn offers no lawful way to read its vacancies (docs/sources/linkedin.md), so enabling it must not be
        // a silent no-op: exactly one source named LinkedIn exists, and it says why it is not connected.
        await using var provider = BuildProvider(
            WithSources(NewTempDir(), dou: false, jooble: false, djinni: false, linkedIn: true));

        var source = Assert.Single(provider.GetServices<IVacancySource>());

        Assert.IsType<DisabledSource>(source);
        Assert.Equal(VacancySource.LinkedIn, source.Source);
        var failure = await Assert.ThrowsAsync<SourceNotConnectedException>(() => source.FetchAsync(CancellationToken.None));
        Assert.Equal(VacancySource.LinkedIn, failure.SourceKind);
        Assert.StartsWith("Source LinkedIn is not connected: ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("docs/sources/linkedin.md", failure.Reason);
        // Short, plain Ukrainian: it is shown to the user in Telegram as it is.
        Assert.Matches("[А-Яа-яІіЇїЄєҐґ]{3,}", failure.Reason);
        Assert.True(failure.Reason.Length <= 250, $"The reason is {failure.Reason.Length} characters long.");
        // A single instance, like every other source.
        Assert.Same(source, Assert.Single(provider.GetServices<IVacancySource>()));
    }

    [Fact]
    public async Task LinkedIn_enabled_makes_the_watch_cycle_send_the_not_connected_notice_once()
    {
        var dataDir = NewTempDir();
        try
        {
            var telegram = TelegramAccepting();
            await using var provider = BuildProvider(
                WithSources(dataDir, dou: false, jooble: false, djinni: false, linkedIn: true),
                services => ReplacePrimaryHandler(services, TelegramClient, telegram));
            var linkedIn = Assert.Single(provider.GetServices<IVacancySource>());
            var reason = (await Assert.ThrowsAsync<SourceNotConnectedException>(
                () => linkedIn.FetchAsync(CancellationToken.None))).Reason;
            var cycle = provider.GetRequiredService<IWatchCycle>();

            await cycle.RunOnceAsync(CancellationToken.None);
            await cycle.RunOnceAsync(CancellationToken.None);

            // Two cycles, one message: the user is told once, not on every poll.
            var sent = Assert.Single(telegram.Requests);
            Assert.Equal($"https://api.telegram.org/bot{BotToken}/sendMessage", sent.Url);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal(ChatId, body.RootElement.GetProperty("chat_id").GetString());
            var text = body.RootElement.GetProperty("text").GetString()!;
            Assert.StartsWith("ℹ️ Джерело LinkedIn не підключено: ", text);
            Assert.Contains(reason, text);
            Assert.Contains("docs/sources/linkedin.md", text);
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    [Fact]
    public async Task Each_source_is_a_single_instance()
    {
        // JoobleSource remembers when it last fetched, to protect the request quota of the API key.
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: true, jooble: true, djinni: true));

        var first = provider.GetServices<IVacancySource>().ToList();
        var second = provider.GetServices<IVacancySource>().ToList();

        Assert.Equal(3, first.Count);
        Assert.True(first.Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second)));
    }

    [Fact]
    public async Task Djinni_is_one_instance_that_is_both_the_source_and_the_reader_of_its_vacancy_pages()
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: false, jooble: false, djinni: true));

        var source = Assert.Single(provider.GetServices<IVacancySource>());

        var djinni = Assert.IsType<DjinniSource>(source);
        Assert.IsAssignableFrom<IVacancyEnricher>(source);
        Assert.True(djinni.IsEnabled);
        Assert.Equal(15, djinni.MaxPerCycle);
        // The watch cycle asks the registered source itself, so the time of its last page request is kept.
        Assert.Same(source, Assert.Single(provider.GetServices<IVacancySource>()));
    }

    /// <summary>Runs the configuration of a named client's handler on a handler the test chooses.</summary>
    private sealed class InspectingHandlerBuilder(IServiceProvider services, HttpMessageHandler primary) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = primary;

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services => services;

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    // The anonymous cookie Djinni sets on the feed response must not travel with the requests for vacancy pages, and a
    // redirect must not take a request away from djinni.co: the client answers a redirect with a failure instead.
    [Fact]
    public async Task The_djinni_client_keeps_no_cookies_and_follows_no_redirects()
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: false, jooble: false, djinni: true));
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(DjinniClient);
        using var sockets = new SocketsHttpHandler();
        using var classic = new HttpClientHandler();

        foreach (HttpMessageHandler handler in new HttpMessageHandler[] { sockets, classic })
        {
            var builder = new InspectingHandlerBuilder(provider, handler) { Name = DjinniClient };
            foreach (var action in options.HttpMessageHandlerBuilderActions)
            {
                action(builder);
            }
        }

        Assert.False(sockets.UseCookies);
        Assert.False(classic.UseCookies);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(classic.AllowAutoRedirect);
    }

    [Fact]
    public async Task Http_clients_have_a_30_second_timeout()
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: true, jooble: true, djinni: true, eleks: true));
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (var client in new[] { TelegramClient, DouClient, JoobleClient, DjinniClient, EleksClient })
        {
            Assert.Equal(TimeSpan.FromSeconds(30), factory.CreateClient(client).Timeout);
        }
    }

    [Fact]
    public async Task Invalid_config_fails_on_start()
    {
        var settings = ValidSettings(NewTempDir());
        settings["JobWatcher:Telegram:BotToken"] = "";
        var dou = new StubHandler(() => Rss(File.ReadAllText(DouFixturePath)));
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddJobWatcher(BuildConfig(settings));
        ReplacePrimaryHandler(builder.Services, DouClient, dou);
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(failure.Failures, message => message.Contains("JobWatcher__Telegram__BotToken"));
        // The first watch cycle runs as soon as the service starts, so no request means it never started.
        Assert.Empty(dou.Requests);
    }

    // ---- security: secrets in request URLs must stay out of the logs ----

    [Fact]
    public async Task Request_logs_never_contain_the_bot_token_or_the_jooble_key()
    {
        var recorder = new RecordingLoggerProvider();
        var telegram = TelegramAccepting();
        var jooble = new StubHandler(() => Json("""{"totalCount":0,"jobs":[]}"""));
        var dou = new StubHandler(() => Rss(File.ReadAllText(DouFixturePath)));
        await using var provider = BuildProvider(ValidSettings(NewTempDir()), services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
            ReplacePrimaryHandler(services, TelegramClient, telegram);
            ReplacePrimaryHandler(services, JoobleClient, jooble);
            ReplacePrimaryHandler(services, DouClient, dou);
        });

        await provider.GetRequiredService<IMessageNotifier>().SendAsync("hello", CancellationToken.None);
        foreach (var source in provider.GetServices<IVacancySource>())
        {
            await source.FetchAsync(CancellationToken.None);
        }

        // Guards against a vacuous pass: the secrets really travelled in the URLs ...
        Assert.Contains(TokenSecret, Assert.Single(telegram.Requests).Url);
        Assert.Contains(JoobleKey, jooble.Requests[0].Url);
        // ... and the recorder does see the HTTP logs of a client that keeps its default logging (DOU, no secret).
        Assert.Contains(
            recorder.Entries,
            entry => entry.Category.Contains(DouClient) && entry.Text.Contains("jobs.dou.ua"));

        Assert.DoesNotContain(recorder.Entries, entry => entry.Text.Contains(TokenSecret));
        Assert.DoesNotContain(recorder.Entries, entry => entry.Text.Contains(JoobleKey));
    }

    [Fact]
    public async Task Jooble_from_the_container_logs_its_skip_line_through_the_container_logger_without_the_key()
    {
        var recorder = new RecordingLoggerProvider();
        var jooble = new StubHandler(() => Json("""{"totalCount":0,"jobs":[]}"""));
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: false, jooble: true), services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(recorder));
            ReplacePrimaryHandler(services, JoobleClient, jooble);
        });
        var source = provider.GetRequiredService<JoobleSource>();

        await source.FetchAsync(CancellationToken.None);
        await source.FetchAsync(CancellationToken.None);

        // Two calls, one real fetch (two requests); the second call says why it made none.
        Assert.Equal(2, jooble.Requests.Count);
        var skip = Assert.Single(recorder.Entries, entry => entry.Category == typeof(JoobleSource).FullName);
        Assert.Equal(LogLevel.Information, skip.Level);
        Assert.Contains("Jooble: no request this time, the next one is due after ", skip.Text);
        Assert.Contains("(MinFetchIntervalMinutes=720)", skip.Text);
        Assert.DoesNotContain(recorder.Entries, entry => entry.Text.Contains(JoobleKey));
    }

    // ---- JobsPipe ----

    // Only JobsPipe is on: its key is the secret the log test looks for.
    private static Dictionary<string, string?> WithJobsPipe(string dataDir)
    {
        var settings = WithSources(dataDir, dou: false, jooble: false);
        settings["JobWatcher:Sources:JobsPipe:Enabled"] = "true";
        settings["JobWatcher:Sources:JobsPipe:ApiKey"] = JobsPipeKey;
        // The end-to-end test passes an hour on the clock between two searches, whatever the shipped interval is.
        settings["JobWatcher:Sources:JobsPipe:MinFetchIntervalMinutes"] = "60";

        return settings;
    }

    [Fact]
    public async Task JobsPipe_enabled_is_registered_as_one_source_instance()
    {
        await using var provider = BuildProvider(WithJobsPipe(NewTempDir()));

        var source = Assert.Single(provider.GetServices<IVacancySource>());

        Assert.IsType<JobsPipeSource>(source);
        Assert.Equal(VacancySource.JobsPipe, source.Source);
        // The source remembers when it last searched, to protect the quota of the key, so every cycle must get the same one.
        Assert.Same(source, Assert.Single(provider.GetServices<IVacancySource>()));
    }

    [Fact]
    public async Task JobsPipe_is_off_unless_it_is_switched_on()
    {
        // The shipped appsettings.json keeps it off: it needs the user's own key.
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: true, jooble: false, djinni: true));

        Assert.Null(provider.GetService<JobsPipeSource>());
        Assert.DoesNotContain(provider.GetServices<IVacancySource>(), source => source.Source == VacancySource.JobsPipe);
    }

    [Fact]
    public async Task The_jobspipe_client_has_a_30_second_timeout()
    {
        await using var provider = BuildProvider(WithJobsPipe(NewTempDir()));

        Assert.Equal(TimeSpan.FromSeconds(30), provider.GetRequiredService<IHttpClientFactory>().CreateClient(JobsPipeClient).Timeout);
    }

    [Fact]
    public async Task Request_logs_never_contain_the_jobspipe_key()
    {
        var recorder = new RecordingLoggerProvider();
        string? authorization = null;
        var jobsPipe = new StubHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();

            return Json("""{"data":[]}""");
        });
        await using var provider = BuildProvider(WithJobsPipe(NewTempDir()), services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
            ReplacePrimaryHandler(services, JobsPipeClient, jobsPipe);
        });

        await provider.GetRequiredService<JobsPipeSource>().FetchAsync(CancellationToken.None);

        // Guards against a vacuous pass: the key really travelled, in the header and not in the address ...
        Assert.Equal($"Bearer {JobsPipeKey}", authorization);
        Assert.DoesNotContain(JobsPipeKey, Assert.Single(jobsPipe.Requests).Url);
        // ... and the recorder does see the HTTP logs of this client (its default logging stays on: no secret is in the URL).
        Assert.Contains(
            recorder.Entries,
            entry => entry.Category.Contains(JobsPipeClient) && entry.Text.Contains("api.jobspipe.dev"));

        Assert.DoesNotContain(recorder.Entries, entry => entry.Text.Contains(JobsPipeKey));
    }

    [Fact]
    public async Task End_to_end_a_new_jobspipe_vacancy_is_announced_once_with_its_requirements()
    {
        const string baseline = """{"id":"old-1","job_title":"Old .NET job","company":"Old Co","url":"https://example.test/old","remote":true}""";
        const string fresh = """
            {"id":"new-1","job_title":"Quantum Ledger .NET Engineer","company":"Quantum Ledger","location":"Berlin, Germany",
             "country_code":"DE","country":"Germany","remote":true,"url":"https://example.test/jobs/new-1","salary_string":"€70K - €90K a year",
             "description":"We build ledgers.\n\nRequirements\n\n- 4+ years of C# and .NET.\n\n- Experience with PostgreSQL.\n\nBenefits\n\n- Coffee."}
            """;
        var dataDir = NewTempDir();
        try
        {
            var telegram = TelegramAccepting();
            var response = $$"""{"data":[{{baseline}}]}""";
            var jobsPipe = new StubHandler(() => Json(response));
            // The source's clock: the interval between two searches is passed on it, not waited for.
            var clock = new ManualTimeProvider();
            await using var provider = BuildProvider(WithJobsPipe(dataDir), services =>
            {
                ReplacePrimaryHandler(services, TelegramClient, telegram);
                ReplacePrimaryHandler(services, JobsPipeClient, jobsPipe);
                services.AddSingleton<TimeProvider>(clock);
            });
            var cycle = provider.GetRequiredService<IWatchCycle>();

            // Cycle 1: the existing vacancy is only remembered; the user gets nothing.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Empty(telegram.Requests);

            // Cycle 2, after the interval: JobsPipe lists one more vacancy.
            clock.Advance(TimeSpan.FromMinutes(60));
            response = $$"""{"data":[{{fresh}},{{baseline}}]}""";
            await cycle.RunOnceAsync(CancellationToken.None);
            var sent = Assert.Single(telegram.Requests);
            using var body = JsonDocument.Parse(sent.Body);
            var text = body.RootElement.GetProperty("text").GetString()!;
            Assert.Equal(
                string.Join(
                    '\n',
                    "🆕 <b>Quantum Ledger .NET Engineer</b>",
                    "🏢 Quantum Ledger",
                    "💰 €70K - €90K a year",
                    "📍 Berlin, Germany · Remote",
                    "",
                    "📋 Вимоги:",
                    "• 4+ years of C# and .NET.",
                    "• Experience with PostgreSQL.",
                    "",
                    "🔗 <a href=\"https://example.test/jobs/new-1\">Відкрити вакансію (JobsPipe)</a>"),
                text);

            // Cycle 3, after the interval: the same answer again; the vacancy was recorded after it was sent.
            clock.Advance(TimeSpan.FromMinutes(60));
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Single(telegram.Requests);
            Assert.Equal(3, jobsPipe.Requests.Count);
            Assert.True(await provider.GetRequiredService<ISeenStore>().ContainsAsync(VacancySource.JobsPipe, "new-1", CancellationToken.None));
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    // ---- Eleks ----

    private const string EleksListUrl = "https://careers.eleks.com/vacancies/?technology=21,27&location=206,98";
    private const string NewEleksVacancyLink = "https://careers.eleks.com/vacancies/senior-net-developer-101/";

    private static string EleksList(params string[] cards) =>
        "<html><body><div class=\"vacancies-list\">" + string.Concat(cards) + "</div></body></html>";

    private static string EleksCard(string slug, string title, string location) =>
        $"<a class=\"vacancy-item\" href=\"https://careers.eleks.com/vacancies/{slug}/\">"
        + $"<div class=\"vacancy-item__title\">{title}</div><div class=\"vacancy-item__location\">{location}</div>"
        + "<div class=\"vacancy-item__technology\">.NET</div></a>";

    [Fact]
    public async Task Eleks_is_one_instance_that_is_both_the_source_and_the_reader_of_its_vacancy_pages()
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: false, jooble: false, eleks: true));

        var source = Assert.Single(provider.GetServices<IVacancySource>());

        var eleks = Assert.IsType<EleksSource>(source);
        Assert.IsAssignableFrom<IVacancyEnricher>(source);
        Assert.True(eleks.IsEnabled);
        Assert.Equal(15, eleks.MaxPerCycle);
        // The source remembers when it last fetched the list, so every cycle must get the same one.
        Assert.Same(source, Assert.Single(provider.GetServices<IVacancySource>()));
    }

    // Nothing the site sets may travel with a later request, and a redirect must not take a request away from
    // careers.eleks.com: the client answers a redirect with a failure instead.
    [Fact]
    public async Task The_eleks_client_keeps_no_cookies_and_follows_no_redirects()
    {
        await using var provider = BuildProvider(WithSources(NewTempDir(), dou: false, jooble: false, eleks: true));
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(EleksClient);
        using var sockets = new SocketsHttpHandler();
        using var classic = new HttpClientHandler();

        foreach (HttpMessageHandler handler in new HttpMessageHandler[] { sockets, classic })
        {
            var builder = new InspectingHandlerBuilder(provider, handler) { Name = EleksClient };
            foreach (var action in options.HttpMessageHandlerBuilderActions)
            {
                action(builder);
            }
        }

        Assert.False(sockets.UseCookies);
        Assert.False(classic.UseCookies);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(classic.AllowAutoRedirect);
    }

    [Fact]
    public async Task End_to_end_the_first_eleks_vacancy_after_an_empty_list_is_announced_once_with_its_requirements_and_without_a_city_of_the_profile()
    {
        var dataDir = NewTempDir();
        try
        {
            var telegram = TelegramAccepting();

            // As on the real site today: nothing is listed yet.
            var list = EleksList();
            var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "eleks-vacancy-page.html"));
            var eleks = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath == "/vacancies/" ? list : page, Encoding.UTF8, "text/html"),
            });
            // The source's clock: the day between two fetches and the gap before a page request are passed on it, not waited for.
            var clock = new ManualTimeProvider();
            await using var provider = BuildProvider(WithSources(dataDir, dou: false, jooble: false, eleks: true), services =>
            {
                ReplacePrimaryHandler(services, TelegramClient, telegram);
                ReplacePrimaryHandler(services, EleksClient, eleks);
                services.AddSingleton<TimeProvider>(clock);
            });
            var cycle = provider.GetRequiredService<IWatchCycle>();

            // Cycle 1: the list is empty; the user gets nothing and no page is read.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Empty(telegram.Requests);
            Assert.Single(eleks.Requests);

            // Cycle 2, a day later: the first vacancy ever listed appears, in Lviv, which is not the profile's city; Eleks's
            // own list chose it, so it is announced (not swallowed as a baseline), with the requirements of its page.
            clock.Advance(TimeSpan.FromDays(1));
            list = EleksList(EleksCard("senior-net-developer-101", "Senior .NET Developer", "Lviv, Ukraine"));
            await cycle.RunOnceAsync(CancellationToken.None);
            var sent = Assert.Single(telegram.Requests);
            using var body = JsonDocument.Parse(sent.Body);
            var text = body.RootElement.GetProperty("text").GetString()!;
            Assert.Equal(
                string.Join(
                    '\n',
                    "🆕 <b>Senior .NET Developer</b>",
                    "🏢 ELEKS",
                    "💰 не вказано",
                    "📍 Lviv, Ukraine",
                    "",
                    "📋 Вимоги:",
                    "• 7+ years of experience in software development",
                    "• 4+ years of experience in .NET",
                    "• Experience with Azure &amp; Docker",
                    "• Advanced level of English",
                    "",
                    $"🔗 <a href=\"{NewEleksVacancyLink}\">Відкрити вакансію (Eleks)</a>"),
                text);

            // Cycle 3, another day later: the same list; the vacancy was recorded after it was sent, so nothing goes out
            // and its page is not read again.
            clock.Advance(TimeSpan.FromDays(1));
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Single(telegram.Requests);
            Assert.Equal(
                [EleksListUrl, EleksListUrl, NewEleksVacancyLink, EleksListUrl],
                eleks.Requests.Select(request => request.Url));

            // The page request came the configured 2 seconds after the list request of its cycle.
            Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
            Assert.True(await provider.GetRequiredService<ISeenStore>().ContainsAsync(VacancySource.Eleks, "senior-net-developer-101", CancellationToken.None));
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    // ---- end to end ----

    [Fact]
    public async Task End_to_end_a_new_vacancy_is_announced_once_after_a_silent_first_run()
    {
        var dataDir = NewTempDir();
        try
        {
            var telegram = TelegramAccepting();
            var feed = File.ReadAllText(DouFixturePath);
            var dou = new StubHandler(() => Rss(feed));
            var settings = WithSources(dataDir, dou: true, jooble: false);
            await using var provider = BuildProvider(settings, services =>
            {
                ReplacePrimaryHandler(services, TelegramClient, telegram);
                ReplacePrimaryHandler(services, DouClient, dou);
            });
            var cycle = provider.GetRequiredService<IWatchCycle>();

            // Cycle 1: the existing vacancies are only remembered; the user gets nothing.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Empty(telegram.Requests);

            // Cycle 2: the feed now has one more matching vacancy.
            feed = WithExtraVacancy(feed);
            await cycle.RunOnceAsync(CancellationToken.None);
            var sent = Assert.Single(telegram.Requests);
            Assert.Equal(HttpMethod.Post, sent.Method);
            Assert.Equal($"https://api.telegram.org/bot{BotToken}/sendMessage", sent.Url);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal(ChatId, body.RootElement.GetProperty("chat_id").GetString());
            Assert.Equal("HTML", body.RootElement.GetProperty("parse_mode").GetString());
            var text = body.RootElement.GetProperty("text").GetString()!;
            Assert.Contains(NewVacancyTitle, text);
            Assert.Contains(NewVacancyLink, text);
            Assert.Contains("Відкрити вакансію (DOU)", text);

            // Cycle 3: the same feed again; the vacancy was recorded after it was sent, so nothing more goes out.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Single(telegram.Requests);
            Assert.Equal(3, dou.Requests.Count);
            Assert.True(await provider.GetRequiredService<ISeenStore>().ContainsAsync(VacancySource.Dou, NewVacancyId, CancellationToken.None));
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    // The fixture feed plus one item cloned from a fixture vacancy that passes the profile, with a new number and title.
    private static string WithExtraVacancy(string feed)
    {
        var document = XDocument.Parse(feed);
        var channel = document.Root!.Element("channel")!;
        var template = channel.Elements("item")
            .Single(item => item.Element("link")!.Value.Contains($"/vacancies/{ClonedFixtureVacancyId}/"));

        var extra = new XElement(template);
        extra.Element("title")!.Value = $"{NewVacancyTitle} в FREITTY, віддалено";
        extra.Element("link")!.Value = NewVacancyLink;
        channel.Elements("item").First().AddBeforeSelf(extra);

        return document.ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public async Task End_to_end_a_new_djinni_vacancy_is_announced_once_with_the_details_of_its_page()
    {
        var dataDir = NewTempDir();
        try
        {
            var telegram = TelegramAccepting();
            var feed = File.ReadAllText(DjinniFixturePath);
            var page = File.ReadAllText(DjinniPageFixturePath);
            var djinni = new StubHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/jobs/rss/", StringComparison.Ordinal)
                ? Rss(feed)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "text/html") });
            // The source's clock: the polite gap between the feed and the page request is waited on it, not slept.
            var clock = new ManualTimeProvider();
            await using var provider = BuildProvider(WithSources(dataDir, dou: false, jooble: false, djinni: true), services =>
            {
                ReplacePrimaryHandler(services, TelegramClient, telegram);
                ReplacePrimaryHandler(services, DjinniClient, djinni);
                services.AddSingleton<TimeProvider>(clock);
            });
            var cycle = provider.GetRequiredService<IWatchCycle>();

            // Cycle 1: the existing vacancies are only remembered; the user gets nothing and no page is read.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Empty(telegram.Requests);
            Assert.Single(djinni.Requests);

            // Cycle 2: the feed now has one more matching vacancy; its page names the company, salary and place.
            feed = WithExtraDjinniVacancy(feed);
            await cycle.RunOnceAsync(CancellationToken.None);
            var sent = Assert.Single(telegram.Requests);
            using var body = JsonDocument.Parse(sent.Body);
            var text = body.RootElement.GetProperty("text").GetString()!;
            Assert.Contains(NewDjinniVacancyTitle, text);
            Assert.Contains(NewDjinniVacancyLink, text);
            Assert.Contains("Відкрити вакансію (Djinni)", text);
            Assert.Contains("🏢 \u0406nfinitytechnologies", text);
            Assert.Contains("$2000–2500", text);
            Assert.DoesNotContain("🏢 не вказано", text);

            // Cycle 3: the same feed again; nothing more goes out and no page is read again.
            await cycle.RunOnceAsync(CancellationToken.None);
            Assert.Single(telegram.Requests);
            Assert.Equal(
                [DjinniFeedUrl, DjinniFeedUrl, NewDjinniVacancyLink, DjinniFeedUrl],
                djinni.Requests.Select(request => request.Url));

            // The page request came the configured 2 seconds after the feed request of its cycle.
            Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
            Assert.True(await provider.GetRequiredService<ISeenStore>().ContainsAsync(VacancySource.Djinni, NewDjinniVacancyId, CancellationToken.None));
        }
        finally
        {
            DeleteIfExists(dataDir);
        }
    }

    // The fixture feed plus one item cloned from a fixture vacancy that passes the profile, with a new number and title.
    private static string WithExtraDjinniVacancy(string feed)
    {
        var document = XDocument.Parse(feed);
        var channel = document.Root!.Element("channel")!;
        var template = channel.Elements("item")
            .Single(item => item.Element("link")!.Value.Contains($"/jobs/{ClonedDjinniVacancyId}-"));

        var extra = new XElement(template);
        extra.Element("title")!.Value = NewDjinniVacancyTitle;
        extra.Element("link")!.Value = NewDjinniVacancyLink;
        extra.Element("guid")!.Value = NewDjinniVacancyLink;
        channel.Elements("item").First().AddBeforeSelf(extra);

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static Vacancy RemoteVacancy(string title) => new(
        Source: VacancySource.Dou,
        SourceId: "1",
        Title: title,
        Company: "Acme",
        Url: "https://example.com/vacancies/1",
        Salary: null,
        Location: null,
        WorkMode: WorkMode.Remote,
        RemoteRestrictedTo: null,
        MinYearsExperience: null,
        IsJunior: false,
        Description: null);
}
