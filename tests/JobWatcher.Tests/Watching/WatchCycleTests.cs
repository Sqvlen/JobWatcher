using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Filtering;
using JobWatcher.Ratings;
using JobWatcher.Sources;
using JobWatcher.Storage;
using JobWatcher.Telegram;
using JobWatcher.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Watching;

public sealed class WatchCycleTests
{
    private const string TelegramFailure = "Telegram sendMessage failed with HTTP 500: boom";

    private static readonly ProfileOptions Profile = new()
    {
        Keyword = ".NET",
        Synonyms = ["C#", "ASP.NET", "dotnet"],
        MinYearsExperience = 3,
        Cities = ["Київ", "Kyiv"],
        AllowedRemoteRegions = ["Ukraine", "Europe", "EU", "Worldwide"],
    };

    // A vacancy that passes the profile filter; its title and url carry the id.
    private static Vacancy Matching(VacancySource source, string id, string? description = null) => new(
        Source: source,
        SourceId: id,
        Title: $".NET Developer {id}",
        Company: "Acme",
        Url: $"https://example.com/{Slug(source)}/{id}",
        Salary: null,
        Location: null,
        WorkMode: WorkMode.Remote,
        RemoteRestrictedTo: null,
        MinYearsExperience: null,
        IsJunior: false,
        Description: description);

    // The lower-case member name, as the vacancy links of the fakes carry it.
    private static string Slug(VacancySource source) => source.ToString().ToLowerInvariant();

    private static Vacancy NonMatching(VacancySource source, string id) =>
        Matching(source, id) with { Title = $"Java Developer {id}" };

    private static bool IsVacancyMessage(string html) => html.StartsWith("🆕", StringComparison.Ordinal);

    private static bool IsOperationalMessage(string html) => !IsVacancyMessage(html);

    private sealed class FakeSource(VacancySource source) : IVacancySource
    {
        public VacancySource Source => source;

        public List<Vacancy> Vacancies { get; set; } = [];

        public Exception? Failure { get; set; }

        /// <summary>Runs inside the fetch, before the failure is thrown.</summary>
        public Action? OnFetch { get; set; }

        public int FetchCount { get; private set; }

        public Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct)
        {
            FetchCount++;
            OnFetch?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult<IReadOnlyList<Vacancy>>(Vacancies.ToList());
        }
    }

    /// <summary>A source that can also read the details of its vacancies, as Djinni does from the vacancy page.</summary>
    private sealed class FakeEnrichingSource(VacancySource source) : IVacancySource, IVacancyEnricher
    {
        public VacancySource Source => source;

        public List<Vacancy> Vacancies { get; set; } = [];

        public bool IsEnabled { get; set; } = true;

        public int MaxPerCycle { get; set; } = 15;

        /// <summary>What the details say; it may throw, as a page that cannot be read does.</summary>
        public Func<Vacancy, Vacancy> Details { get; set; } = vacancy => vacancy;

        /// <summary>The ids whose details were asked for, in order.</summary>
        public List<string> Enriched { get; } = [];

        public Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Vacancy>>(Vacancies.ToList());

        public Task<Vacancy> EnrichAsync(Vacancy vacancy, CancellationToken ct)
        {
            Enriched.Add(vacancy.SourceId);
            try
            {
                return Task.FromResult(Details(vacancy));
            }
            catch (Exception ex)
            {
                return Task.FromException<Vacancy>(ex);
            }
        }
    }

    private sealed class FakeStore(List<string> order) : ISeenStore
    {
        private readonly Dictionary<VacancySource, HashSet<string>> _seen = [];

        public Exception? AddFailure { get; set; }

        /// <summary>How many times the watch cycle called the store (preloading and inspecting by a test do not count).</summary>
        public int Calls { get; private set; }

        public void Preload(VacancySource source, params string[] ids) => SeenSet(source).UnionWith(ids);

        public IReadOnlyList<string> SeenIds(VacancySource source) =>
            SeenSet(source).Order(StringComparer.Ordinal).ToList();

        public bool IsSeen(VacancySource source, string id) => SeenSet(source).Contains(id);

        public Task<bool> HasAnyAsync(VacancySource source, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(SeenSet(source).Count > 0);
        }

        public Task<bool> ContainsAsync(VacancySource source, string sourceId, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(SeenSet(source).Contains(sourceId));
        }

        public Task AddAsync(VacancySource source, IEnumerable<string> sourceIds, CancellationToken ct)
        {
            Calls++;
            var ids = sourceIds.ToList();
            if (AddFailure is not null)
            {
                throw AddFailure;
            }

            foreach (var id in ids)
            {
                SeenSet(source).Add(id);
                order.Add($"add:{source}/{id}");
            }

            return Task.CompletedTask;
        }

        private HashSet<string> SeenSet(VacancySource source)
        {
            if (!_seen.TryGetValue(source, out var ids))
            {
                _seen[source] = ids = [];
            }

            return ids;
        }
    }

    private sealed class FakeNotifier(List<string> order) : IMessageNotifier
    {
        /// <summary>Messages the notifier delivered.</summary>
        public List<string> Sent { get; } = [];

        /// <summary>Every call, delivered or not.</summary>
        public int Attempts { get; private set; }

        public Func<string, bool> FailWhen { get; set; } = _ => false;

        public IEnumerable<string> VacancyMessages => Sent.Where(IsVacancyMessage);

        public IEnumerable<string> OperationalMessages => Sent.Where(IsOperationalMessage);

        public bool WasSent(VacancySource source, string id) =>
            VacancyMessages.Any(message => message.Contains($"/{Slug(source)}/{id}\"", StringComparison.Ordinal));

        public Task SendAsync(string html, CancellationToken ct)
        {
            Attempts++;
            if (FailWhen(html))
            {
                throw new TelegramDeliveryException(TelegramFailure);
            }

            Sent.Add(html);
            order.Add("send");

            return Task.CompletedTask;
        }
    }

    private sealed class FakeRatings : ICompanyRatingProvider
    {
        public Func<string, CompanyRating?> Lookup { get; set; } = _ => null;

        public List<string> Requested { get; } = [];

        public Task<CompanyRating?> GetAsync(string company, CancellationToken ct)
        {
            Requested.Add(company);

            return Task.FromResult(Lookup(company));
        }
    }

    private sealed class Harness
    {
        public Harness(
            IEnumerable<IVacancySource> sources, int? maxMessages = null, int failureThreshold = 3, ProfileOptions? profile = null)
        {
            Store = new FakeStore(Order);
            Notifier = new FakeNotifier(Order);
            Health = new SourceHealth(failureThreshold);
            Cycle = new WatchCycle(
                sources,
                new VacancyFilter(profile ?? Profile),
                Store,
                Ratings,
                Notifier,
                Health,
                Options.Create(new WatcherOptions { MaxMessagesPerCycle = maxMessages, Profile = profile ?? Profile }),
                Log);
        }

        public SourceHealth Health { get; }

        /// <summary>The order in which messages were delivered and ids recorded.</summary>
        public List<string> Order { get; } = [];

        public FakeStore Store { get; }

        public FakeNotifier Notifier { get; }

        public FakeRatings Ratings { get; } = new();

        public RecordingLogger<WatchCycle> Log { get; } = new();

        public WatchCycle Cycle { get; }

        public Task RunAsync() => Cycle.RunOnceAsync(CancellationToken.None);

        public async Task RunAsync(int times)
        {
            for (var i = 0; i < times; i++)
            {
                await RunAsync();
            }
        }
    }

    [Fact]
    public async Task First_run_marks_all_seen_and_sends_nothing()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2"), NonMatching(VacancySource.Dou, "3")] };
        var h = new Harness([dou]);

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(["1", "2", "3"], h.Store.SeenIds(VacancySource.Dou));
    }

    [Fact]
    public async Task First_run_is_per_source()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "10")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Dou, "1");

        await h.RunAsync();

        Assert.Single(h.Notifier.VacancyMessages);
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
        Assert.False(h.Notifier.WasSent(VacancySource.Jooble, "10"));
        Assert.Equal(["10"], h.Store.SeenIds(VacancySource.Jooble));
    }

    [Fact]
    public async Task Empty_first_fetch_keeps_source_in_first_run_mode()
    {
        var dou = new FakeSource(VacancySource.Dou);
        var h = new Harness([dou]);

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.False(await h.Store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));

        dou.Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")];
        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(["1", "2"], h.Store.SeenIds(VacancySource.Dou));

        dou.Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2"), Matching(VacancySource.Dou, "3")];
        await h.RunAsync();

        Assert.Single(h.Notifier.VacancyMessages);
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "3"));
    }

    [Fact]
    public async Task Invalid_only_first_fetch_keeps_source_in_first_run_mode()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1") with { Title = " " }] };
        var h = new Harness([dou]);

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.False(await h.Store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));

        dou.Vacancies = [Matching(VacancySource.Dou, "2")];
        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(["2"], h.Store.SeenIds(VacancySource.Dou));
    }

    [Fact]
    public async Task Second_cycle_sends_only_new_matching_vacancies()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        await h.RunAsync();

        dou.Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2"), Matching(VacancySource.Dou, "3"), NonMatching(VacancySource.Dou, "4")];
        await h.RunAsync();

        Assert.Single(h.Notifier.Sent);
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "3"));
        Assert.Equal(["1", "2", "3", "4"], h.Store.SeenIds(VacancySource.Dou));

        await h.RunAsync();

        Assert.Single(h.Notifier.Sent);
    }

    [Fact]
    public async Task Non_matching_new_vacancy_is_marked_seen_not_sent()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1"), NonMatching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "1");

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "2"));
    }

    [Fact]
    public async Task Marks_seen_only_after_successful_send()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync();

        Assert.False(h.Store.IsSeen(VacancySource.Dou, "1"));
        Assert.Empty(h.Order);

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();

        Assert.Equal(["send", "add:Dou/1"], h.Order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failing_add_after_successful_send_resends_next_cycle(bool cancelled)
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        Exception failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("disk full");
        h.Store.AddFailure = failure;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync());

        Assert.Same(failure, thrown);
        Assert.Single(h.Notifier.VacancyMessages);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "1"));

        h.Store.AddFailure = null;
        await h.RunAsync();

        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "1"));
    }

    [Fact]
    public async Task Rating_provider_exception_still_sends_without_rating()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Ratings.Lookup = _ => throw new InvalidOperationException("rating service down");

        await h.RunAsync();

        var message = Assert.Single(h.Notifier.VacancyMessages);
        Assert.DoesNotContain("⭐", message);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "1"));
    }

    [Fact]
    public async Task Message_contains_rating_and_requirements()
    {
        const string description = "<h3>Requirements</h3><ul><li>5 years of C#</li></ul>";
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1", description)] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Ratings.Lookup = _ => new CompanyRating(4.2, "Test");

        await h.RunAsync();

        var message = Assert.Single(h.Notifier.VacancyMessages);
        Assert.Contains("⭐ 4.2 (Test)", message);
        Assert.Contains("• 5 years of C#", message);
        Assert.Equal(["Acme"], h.Ratings.Requested);
    }

    [Fact]
    public async Task Vacancy_with_unspecified_company_is_sent_without_asking_for_a_rating()
    {
        var unknown = Matching(VacancySource.Dou, "1") with { Company = Placeholders.NotSpecified };
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), unknown, Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Ratings.Lookup = _ => new CompanyRating(4.2, "Test");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "1"));
        Assert.DoesNotContain("⭐", h.Notifier.VacancyMessages.Single(m => m.Contains("/dou/1\"", StringComparison.Ordinal)));
        Assert.Equal(["Acme"], h.Ratings.Requested);
    }

    [Fact]
    public async Task Source_failure_below_threshold_sends_no_alert_and_other_sources_still_run()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new HttpRequestException("boom") };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync(2);

        Assert.Empty(h.Notifier.OperationalMessages);
        Assert.Equal(2, dou.FetchCount);
        Assert.Single(h.Notifier.VacancyMessages);
        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "1"));
    }

    [Fact]
    public async Task Alert_sent_at_threshold()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new HttpRequestException("boom") };
        var h = new Harness([dou]);

        await h.RunAsync(2);
        Assert.Empty(h.Notifier.Sent);

        await h.RunAsync();
        Assert.Equal(["⚠️ Джерело DOU не відповідає (3 невдалих спроб поспіль)"], h.Notifier.Sent);

        await h.RunAsync(2);
        Assert.Single(h.Notifier.Sent);
    }

    [Fact]
    public async Task Failed_down_alert_is_retried_next_cycle_and_not_repeated_once_delivered()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new HttpRequestException("boom") };
        var h = new Harness([dou], failureThreshold: 2);
        h.Notifier.FailWhen = IsOperationalMessage;

        await h.RunAsync();
        Assert.Equal(0, h.Notifier.Attempts);

        await h.RunAsync();
        Assert.Equal(1, h.Notifier.Attempts);
        Assert.Empty(h.Notifier.Sent);

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();
        Assert.Equal(["⚠️ Джерело DOU не відповідає (2 невдалих спроб поспіль)"], h.Notifier.Sent);

        await h.RunAsync(2);
        Assert.Single(h.Notifier.Sent);
    }

    [Fact]
    public async Task Failed_not_connected_notice_is_retried_next_cycle_and_not_repeated_once_delivered()
    {
        var h = new Harness([new DisabledSource(VacancySource.Djinni, "no public API")]);
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync();
        Assert.Equal(1, h.Notifier.Attempts);
        Assert.Empty(h.Notifier.Sent);

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();
        Assert.Equal(["ℹ️ Джерело Djinni не підключено: no public API"], h.Notifier.Sent);

        await h.RunAsync(2);
        Assert.Single(h.Notifier.Sent);
    }

    [Fact]
    public async Task Successful_fetch_resets_failure_streak()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new HttpRequestException("boom") };
        var h = new Harness([dou]);
        await h.RunAsync(2);

        dou.Failure = null;
        dou.Vacancies = [Matching(VacancySource.Dou, "1")];
        await h.RunAsync();

        dou.Failure = new HttpRequestException("boom");
        await h.RunAsync(2);

        Assert.Empty(h.Notifier.Sent);

        await h.RunAsync();

        Assert.Single(h.Notifier.OperationalMessages);
    }

    [Fact]
    public async Task NotConnected_source_sends_notice_once_and_is_skipped()
    {
        var djinni = new DisabledSource(VacancySource.Djinni, "no public API");
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };

        // Threshold 1: had a not-connected source counted as a failure, it would raise the outage alert.
        var h = new Harness([djinni, jooble], failureThreshold: 1);
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync(3);

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Equal("ℹ️ Джерело Djinni не підключено: no public API", notice);
        Assert.Single(h.Notifier.VacancyMessages);
        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "1"));
    }

    [Fact]
    public async Task Telegram_failure_leaves_vacancy_unseen_and_stops_cycle()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync();

        Assert.Equal(1, h.Notifier.Attempts);
        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "1"));
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "2"));
        Assert.Equal(0, jooble.FetchCount);

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "1"));
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "1"));
        Assert.True(h.Store.IsSeen(VacancySource.Jooble, "1"));
    }

    [Fact]
    public async Task Telegram_failure_is_logged_with_message_only()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync();

        Assert.Contains(h.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(TelegramFailure));
        Assert.All(h.Log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task No_limit_configured_sends_all_new()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0")] };
        dou.Vacancies.AddRange(Enumerable.Range(1, 25).Select(i => Matching(VacancySource.Dou, i.ToString())));
        var h = new Harness([dou], maxMessages: null);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.Equal(25, h.Notifier.VacancyMessages.Count());
        Assert.Equal(26, h.Store.SeenIds(VacancySource.Dou).Count);
    }

    [Fact]
    public async Task Sends_at_most_MaxMessagesPerCycle_when_set_and_rest_next_cycle()
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies = [Matching(VacancySource.Dou, "0"), .. Enumerable.Range(1, 5).Select(i => Matching(VacancySource.Dou, i.ToString())), NonMatching(VacancySource.Dou, "6")],
        };
        var h = new Harness([dou], maxMessages: 2);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
        Assert.Equal(["0", "1", "2", "6"], h.Store.SeenIds(VacancySource.Dou));

        await h.RunAsync();

        Assert.Equal(4, h.Notifier.VacancyMessages.Count());
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "3"));
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "4"));

        await h.RunAsync();

        Assert.Equal(5, h.Notifier.VacancyMessages.Count());
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "5"));
    }

    [Fact]
    public async Task MaxMessagesPerCycle_is_shared_by_all_sources()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "10")] };
        var h = new Harness([dou, jooble], maxMessages: 2);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync();

        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
        Assert.False(h.Notifier.WasSent(VacancySource.Jooble, "10"));
        Assert.False(h.Store.IsSeen(VacancySource.Jooble, "10"));

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "10"));
    }

    [Theory]
    [InlineData("SourceId", "")]
    [InlineData("SourceId", "  ")]
    [InlineData("Title", "")]
    [InlineData("Title", "  ")]
    [InlineData("Company", "")]
    [InlineData("Company", "  ")]
    [InlineData("Url", "")]
    [InlineData("Url", "  ")]
    public async Task Skips_vacancies_with_blank_required_fields(string field, string blank)
    {
        var valid = Matching(VacancySource.Dou, "bad");
        var invalid = field switch
        {
            "SourceId" => valid with { SourceId = blank },
            "Title" => valid with { Title = blank },
            "Company" => valid with { Company = blank },
            "Url" => valid with { Url = blank },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), invalid, Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.Single(h.Notifier.VacancyMessages);
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.Equal(["0", "1"], h.Store.SeenIds(VacancySource.Dou));
    }

    [Fact]
    public async Task Duplicate_source_ids_in_one_fetch_sent_once()
    {
        // Same SourceId, different title: the records are not equal, so only the id decides they are duplicates.
        var repost = Matching(VacancySource.Dou, "1") with { Title = ".NET Developer 1 (repost)" };
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1"), repost, Matching(VacancySource.Dou, "2")],
        };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
        var sent = Assert.Single(h.Notifier.VacancyMessages, message => message.Contains("/dou/1\"", StringComparison.Ordinal));
        Assert.Contains("<b>.NET Developer 1</b>", sent);
    }

    [Fact]
    public async Task Poison_vacancy_gets_notice_and_is_marked_seen_after_three_failures()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = html => html.Contains("/dou/bad\"", StringComparison.Ordinal);

        await h.RunAsync(2);

        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));

        await h.RunAsync();

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Equal("⚠️ Не вдалося надіслати вакансію .NET Developer bad (Acme): https://example.com/dou/bad", notice);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "bad"));
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));

        await h.RunAsync();

        Assert.Equal(2, h.Notifier.Sent.Count);
    }

    [Fact]
    public async Task Poison_vacancy_not_marked_seen_when_notice_also_fails()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync(3);

        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "2"));

        // Telegram recovers, but the poison vacancy still cannot be sent: the notice is retried on its next failure.
        h.Notifier.FailWhen = html => html.Contains("/dou/bad\"", StringComparison.Ordinal);
        await h.RunAsync();

        Assert.Single(h.Notifier.OperationalMessages);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "bad"));
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
    }

    [Fact]
    public async Task Failure_counter_is_per_vacancy()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = IsVacancyMessage;
        await h.RunAsync(2);

        // Another vacancy starts failing: its own count starts from zero.
        dou.Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "other")];
        await h.RunAsync(2);

        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "other"));

        await h.RunAsync();

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Contains("other", notice);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "other"));
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));
    }

    [Fact]
    public async Task Failure_counter_is_per_source_and_id()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");
        h.Notifier.FailWhen = IsVacancyMessage;
        await h.RunAsync(2);

        // The same id in another source: dou's vacancy is gone, jooble's fails for the first time.
        dou.Vacancies = [Matching(VacancySource.Dou, "0")];
        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Jooble, "1"));
    }

    // Makes building the message of the vacancies with the given ids throw, as a formatter bug would.
    private static void FailPreparationOf(Harness h, params string[] ids)
    {
        var compose = h.Cycle.Compose;
        h.Cycle.Compose = (vacancy, rating) => ids.Contains(vacancy.SourceId)
            ? throw new InvalidOperationException($"formatter bug {vacancy.SourceId}")
            : compose(vacancy, rating);
    }

    [Fact]
    public async Task Vacancy_whose_preparation_fails_does_not_block_the_others()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");
        FailPreparationOf(h, "bad");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "1"));
        Assert.Empty(h.Notifier.OperationalMessages);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));
        Assert.Contains(
            h.Log.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(nameof(InvalidOperationException))
                && entry.Message.Contains("formatter bug bad"));
    }

    [Fact]
    public async Task Vacancy_whose_preparation_keeps_failing_is_reported_once_and_marked_seen()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        FailPreparationOf(h, "bad");

        await h.RunAsync(2);

        Assert.Empty(h.Notifier.OperationalMessages);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));

        await h.RunAsync();

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Equal("⚠️ Не вдалося надіслати вакансію .NET Developer bad (Acme): https://example.com/dou/bad", notice);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "bad"));

        await h.RunAsync(2);

        Assert.Single(h.Notifier.OperationalMessages);
        Assert.Single(h.Notifier.VacancyMessages);
    }

    [Fact]
    public async Task Vacancy_whose_preparation_keeps_failing_stays_unseen_while_the_notice_fails()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        FailPreparationOf(h, "bad");
        h.Notifier.FailWhen = IsOperationalMessage;

        await h.RunAsync(3);

        Assert.Equal(1, h.Notifier.Attempts);
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();

        Assert.Single(h.Notifier.OperationalMessages);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "bad"));
    }

    [Fact]
    public async Task Preparation_failure_counter_is_per_vacancy()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        FailPreparationOf(h, "bad", "other");
        await h.RunAsync(2);

        // Another vacancy starts failing: its own count starts from zero.
        dou.Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "other")];
        await h.RunAsync(2);

        Assert.Empty(h.Notifier.Sent);

        await h.RunAsync();

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Contains("other", notice);
        Assert.True(h.Store.IsSeen(VacancySource.Dou, "other"));
        Assert.False(h.Store.IsSeen(VacancySource.Dou, "bad"));
    }

    [Theory]
    [InlineData(VacancySource.Dou, "DOU")]
    [InlineData(VacancySource.Djinni, "Djinni")]
    [InlineData(VacancySource.Jooble, "Jooble")]
    [InlineData(VacancySource.LinkedIn, "LinkedIn")]
    public async Task Down_alert_names_source_by_display_name(VacancySource kind, string name)
    {
        var source = new FakeSource(kind) { Failure = new HttpRequestException("boom") };
        var h = new Harness([source], failureThreshold: 1);

        await h.RunAsync();

        Assert.Equal([$"⚠️ Джерело {name} не відповідає (1 невдалих спроб поспіль)"], h.Notifier.Sent);
    }

    [Fact]
    public async Task NotConnected_notice_escapes_reason()
    {
        var h = new Harness([new DisabledSource(VacancySource.LinkedIn, "needs <login> & more")]);

        await h.RunAsync();

        Assert.Equal(["ℹ️ Джерело LinkedIn не підключено: needs &lt;login&gt; &amp; more"], h.Notifier.Sent);
    }

    [Fact]
    public async Task Poison_notice_escapes_vacancy_text()
    {
        var poison = Matching(VacancySource.Dou, "bad") with
        {
            Title = "C# <Lead> & Co",
            Company = "R&D <Lab>",
            Url = "https://example.com/dou/bad?a=1&b=2",
        };
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), poison] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = IsVacancyMessage;

        await h.RunAsync(3);

        var notice = Assert.Single(h.Notifier.OperationalMessages);
        Assert.Equal(
            "⚠️ Не вдалося надіслати вакансію C# &lt;Lead&gt; &amp; Co (R&amp;D &lt;Lab&gt;): https://example.com/dou/bad?a=1&amp;b=2",
            notice);
    }

    [Fact]
    public async Task Operational_message_failure_does_not_crash_cycle()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new HttpRequestException("boom") };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble], failureThreshold: 1);
        h.Store.Preload(VacancySource.Jooble, "0");
        h.Notifier.FailWhen = IsOperationalMessage;

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Jooble, "1"));
        Assert.Contains(h.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(TelegramFailure));
    }

    [Fact]
    public async Task Source_cancellation_propagates_and_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var dou = new FakeSource(VacancySource.Dou)
        {
            OnFetch = cts.Cancel,
            Failure = new OperationCanceledException(cts.Token),
        };
        var h = new Harness([dou], failureThreshold: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cycle.RunOnceAsync(cts.Token));

        Assert.Empty(h.Notifier.Sent);
    }

    [Theory]
    [InlineData(VacancySource.Jooble)]
    [InlineData((VacancySource)0)]
    [InlineData((VacancySource)99)]
    public async Task Vacancy_labelled_with_another_or_unknown_source_is_skipped(VacancySource label)
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies = [Matching(VacancySource.Dou, "0"), Matching(label, "2"), Matching(VacancySource.Dou, "1")],
        };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.Single(h.Notifier.Sent);
        Assert.Equal(["0", "1"], h.Store.SeenIds(VacancySource.Dou));
        Assert.Empty(h.Store.SeenIds(label));
        Assert.Contains(
            h.Log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message == "Ignored 1 vacancies of DOU labelled with another source");
    }

    [Fact]
    public async Task Mislabelled_vacancy_is_not_remembered_on_first_run()
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Djinni, "2")],
        };
        var h = new Harness([dou]);

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(["1"], h.Store.SeenIds(VacancySource.Dou));
        Assert.Empty(h.Store.SeenIds(VacancySource.Djinni));
    }

    [Fact]
    public async Task Fetch_timeout_without_cancellation_counts_as_failure()
    {
        var dou = new FakeSource(VacancySource.Dou) { Failure = new TaskCanceledException("The request timed out.") };
        var h = new Harness([dou], failureThreshold: 1);

        await h.RunAsync();

        Assert.Equal(["⚠️ Джерело DOU не відповідає (1 невдалих спроб поспіль)"], h.Notifier.Sent);
    }

    // ---- the per-cycle summary line ----

    private const string SummaryTemplate =
        "{Source}: fetched {Fetched}, new {New}, matching the profile {Matching}, sent {Sent}, held back {HeldBack}";

    private static List<RecordingLogger<WatchCycle>.LogEntry> Summaries(Harness h) =>
        h.Log.Entries
            .Where(entry => entry.Fields.GetValueOrDefault(RecordingLogger<WatchCycle>.TemplateField) as string == SummaryTemplate)
            .ToList();

    private static void AssertSummary(
        RecordingLogger<WatchCycle>.LogEntry entry, string source, int fetched, int fresh, int matching, int sent, int heldBack)
    {
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(
            $"{source}: fetched {fetched}, new {fresh}, matching the profile {matching}, sent {sent}, held back {heldBack}",
            entry.Message);
        Assert.Equal(source, entry.Fields["Source"]);
        Assert.Equal(fetched, Assert.IsType<int>(entry.Fields["Fetched"]));
        Assert.Equal(fresh, Assert.IsType<int>(entry.Fields["New"]));
        Assert.Equal(matching, Assert.IsType<int>(entry.Fields["Matching"]));
        Assert.Equal(sent, Assert.IsType<int>(entry.Fields["Sent"]));
        Assert.Equal(heldBack, Assert.IsType<int>(entry.Fields["HeldBack"]));
    }

    [Fact]
    public async Task A_normal_cycle_logs_one_summary_with_the_counts_of_the_source()
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies =
            [
                Matching(VacancySource.Dou, "1"),
                Matching(VacancySource.Dou, "2"),
                Matching(VacancySource.Dou, "3"),
                Matching(VacancySource.Dou, "4"),
                NonMatching(VacancySource.Dou, "5"),
            ],
        };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "1", "2");

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 5, fresh: 3, matching: 2, sent: 2, heldBack: 0);
        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
    }

    [Fact]
    public async Task A_cycle_with_nothing_new_logs_zeros_after_the_fetched_count()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [.. Enumerable.Range(1, 5).Select(i => Matching(VacancySource.Dou, i.ToString()))] };
        var h = new Harness([dou]);

        await h.RunAsync(2);

        // The first cycle is the silent first run; only the second one has a summary.
        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 5, fresh: 0, matching: 0, sent: 0, heldBack: 0);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task An_empty_fetch_of_a_source_with_history_logs_all_zeros()
    {
        var dou = new FakeSource(VacancySource.Dou);
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "1");

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 0, fresh: 0, matching: 0, sent: 0, heldBack: 0);
    }

    [Fact]
    public async Task An_empty_fetch_while_nothing_is_stored_yet_logs_zeros_every_cycle_and_stays_in_first_run_mode()
    {
        var dou = new FakeSource(VacancySource.Dou);
        var h = new Harness([dou]);

        await h.RunAsync(2);

        // A source that returns nothing must not look frozen, but it has no first-run line to stand in for the count.
        Assert.Equal(2, h.Log.Entries.Count);
        Assert.Collection(
            Summaries(h),
            first => AssertSummary(first, "DOU", fetched: 0, fresh: 0, matching: 0, sent: 0, heldBack: 0),
            second => AssertSummary(second, "DOU", fetched: 0, fresh: 0, matching: 0, sent: 0, heldBack: 0));
        Assert.False(await h.Store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));

        // Still first-run mode: what appears later is only remembered, with the first-run line and no summary.
        dou.Vacancies = [Matching(VacancySource.Dou, "1")];
        await h.RunAsync();

        Assert.Equal(2, Summaries(h).Count);
        Assert.Equal("First run of DOU: 1 existing vacancies marked as seen", h.Log.Entries[^1].Message);
        Assert.Empty(h.Notifier.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_fetch_does_not_touch_the_store(bool withHistory)
    {
        var dou = new FakeSource(VacancySource.Dou);
        var h = new Harness([dou]);
        if (withHistory)
        {
            h.Store.Preload(VacancySource.Dou, "1");
        }

        await h.RunAsync(2);

        // A broken store must not break a cycle that has nothing to look up or remember.
        Assert.Equal(0, h.Store.Calls);
        Assert.Equal(2, Summaries(h).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fetch_of_only_unusable_vacancies_logs_the_fetched_count_with_zeros_and_does_not_touch_the_store(bool withHistory)
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies = [Matching(VacancySource.Dou, "1") with { Title = " " }, Matching(VacancySource.Jooble, "2")],
        };
        var h = new Harness([dou]);
        if (withHistory)
        {
            h.Store.Preload(VacancySource.Dou, "0");
        }

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 2, fresh: 0, matching: 0, sent: 0, heldBack: 0);
        Assert.Equal(0, h.Store.Calls);
        Assert.Empty(h.Notifier.Sent);
        Assert.Contains(
            h.Log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message == "Ignored 1 vacancies of DOU labelled with another source");
        Assert.Contains(
            h.Log.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message == "Ignored 1 vacancies of DOU with blank fields or a repeated id");
    }

    [Fact]
    public async Task Fetched_counts_everything_the_adapter_returned_but_new_only_the_valid_unseen_ones()
    {
        var dou = new FakeSource(VacancySource.Dou)
        {
            Vacancies =
            [
                Matching(VacancySource.Dou, "0"),
                Matching(VacancySource.Dou, "1"),
                Matching(VacancySource.Dou, "2") with { Title = " " },
                Matching(VacancySource.Jooble, "3"),
                Matching(VacancySource.Dou, "1") with { Title = ".NET Developer 1 (repost)" },
            ],
        };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 5, fresh: 1, matching: 1, sent: 1, heldBack: 0);
    }

    [Fact]
    public async Task Matches_beyond_MaxMessagesPerCycle_are_reported_as_held_back()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [.. Enumerable.Range(0, 4).Select(i => Matching(VacancySource.Dou, i.ToString()))] };
        var h = new Harness([dou], maxMessages: 1);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync(2);

        Assert.Collection(
            Summaries(h),
            first => AssertSummary(first, "DOU", fetched: 4, fresh: 3, matching: 3, sent: 1, heldBack: 2),
            second => AssertSummary(second, "DOU", fetched: 4, fresh: 2, matching: 2, sent: 1, heldBack: 1));
    }

    [Fact]
    public async Task A_budget_used_up_by_an_earlier_source_holds_back_every_match_of_the_next_one()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2")] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "10")] };
        var h = new Harness([dou, jooble], maxMessages: 2);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync();

        Assert.Collection(
            Summaries(h),
            first => AssertSummary(first, "DOU", fetched: 3, fresh: 2, matching: 2, sent: 2, heldBack: 0),
            second => AssertSummary(second, "Jooble", fetched: 2, fresh: 1, matching: 1, sent: 0, heldBack: 1));
    }

    [Fact]
    public async Task No_limit_means_nothing_is_ever_held_back()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [.. Enumerable.Range(0, 26).Select(i => Matching(VacancySource.Dou, i.ToString()))] };
        var h = new Harness([dou], maxMessages: null);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 26, fresh: 25, matching: 25, sent: 25, heldBack: 0);
    }

    [Fact]
    public async Task First_run_logs_only_the_first_run_line_and_no_summary()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "1"), Matching(VacancySource.Dou, "2"), NonMatching(VacancySource.Dou, "3")] };
        var h = new Harness([dou]);

        await h.RunAsync();

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("First run of DOU: 3 existing vacancies marked as seen", entry.Message);
        Assert.Empty(Summaries(h));
    }

    [Fact]
    public async Task A_failed_fetch_logs_the_warning_and_no_summary()
    {
        var failure = new HttpRequestException("boom");
        var dou = new FakeSource(VacancySource.Dou) { Failure = failure };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "1");

        await h.RunAsync();

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Fetching DOU failed", entry.Message);
        Assert.Same(failure, entry.Exception);
        Assert.Empty(Summaries(h));
    }

    [Fact]
    public async Task A_source_that_is_not_connected_logs_its_line_and_no_summary()
    {
        var h = new Harness([new DisabledSource(VacancySource.Djinni, "no public API")]);

        await h.RunAsync();

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Source Djinni is not connected: no public API", entry.Message);
        Assert.Empty(Summaries(h));
    }

    [Fact]
    public async Task When_Telegram_refuses_a_message_the_summary_with_the_counts_so_far_comes_before_the_stop_warning()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [.. Enumerable.Range(0, 4).Select(i => Matching(VacancySource.Dou, i.ToString()))] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0")] };
        var h = new Harness([dou, jooble]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = html => html.Contains("/dou/2\"", StringComparison.Ordinal);

        await h.RunAsync();

        // Vacancy 1 went out, vacancy 2 was refused, vacancy 3 was not tried: the cycle stopped, but not for the limit.
        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 4, fresh: 3, matching: 3, sent: 1, heldBack: 0);
        Assert.Equal(0, jooble.FetchCount);
        var messages = h.Log.Entries.Select(entry => entry.Message).ToList();
        var refused = messages.FindIndex(message => message.StartsWith("Telegram did not accept vacancy 2 of DOU", StringComparison.Ordinal));
        var summary = messages.FindIndex(message => message.StartsWith("DOU: fetched", StringComparison.Ordinal));
        var stopped = messages.FindIndex(message => message.StartsWith("Telegram is not accepting messages", StringComparison.Ordinal));
        Assert.True(refused >= 0 && refused < summary && summary < stopped, string.Join(" | ", messages));
    }

    [Fact]
    public async Task A_poison_notice_is_not_counted_as_a_sent_vacancy()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Notifier.FailWhen = html => html.Contains("/dou/bad\"", StringComparison.Ordinal);

        await h.RunAsync(3);

        // Cycles 1 and 2: "bad" is refused and the cycle stops before "2". Cycle 3: the notice about "bad" is
        // delivered (one operational message, not a sent vacancy), then "2" goes out.
        Assert.Collection(
            Summaries(h),
            first => AssertSummary(first, "DOU", fetched: 3, fresh: 2, matching: 2, sent: 0, heldBack: 0),
            second => AssertSummary(second, "DOU", fetched: 3, fresh: 2, matching: 2, sent: 0, heldBack: 0),
            third => AssertSummary(third, "DOU", fetched: 3, fresh: 2, matching: 2, sent: 1, heldBack: 0));
        Assert.Single(h.Notifier.OperationalMessages);
        Assert.Single(h.Notifier.VacancyMessages);
    }

    [Fact]
    public async Task A_vacancy_skipped_because_its_message_cannot_be_built_is_not_counted_as_sent()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "bad"), Matching(VacancySource.Dou, "2")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");
        FailPreparationOf(h, "bad");

        await h.RunAsync();

        AssertSummary(Assert.Single(Summaries(h)), "DOU", fetched: 3, fresh: 2, matching: 2, sent: 1, heldBack: 0);
        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "2"));
    }

    [Fact]
    public async Task Every_connected_source_gets_its_own_summary_in_the_order_of_the_sources()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0")] };
        var djinni = new DisabledSource(VacancySource.Djinni, "no public API");
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, djinni, jooble]);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync();

        Assert.Collection(
            Summaries(h),
            first => AssertSummary(first, "DOU", fetched: 1, fresh: 0, matching: 0, sent: 0, heldBack: 0),
            second => AssertSummary(second, "Jooble", fetched: 2, fresh: 1, matching: 1, sent: 1, heldBack: 0));
    }

    [Fact]
    public async Task The_summary_holds_only_the_source_name_and_numbers_whatever_the_vacancies_contain()
    {
        var secretive = Matching(VacancySource.Dou, "1") with
        {
            Title = ".NET Developer https://api.telegram.org/bot123456:TOKEN/sendMessage",
            Company = "Secret & Co",
            Url = "https://example.com/dou/1?key=SECRET-KEY",
        };
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), secretive] };
        var jooble = new FakeSource(VacancySource.Jooble) { Vacancies = [Matching(VacancySource.Jooble, "0"), Matching(VacancySource.Jooble, "1")] };
        var h = new Harness([dou, jooble], maxMessages: 1);
        h.Store.Preload(VacancySource.Dou, "0");
        h.Store.Preload(VacancySource.Jooble, "0");

        await h.RunAsync(2);

        var summaries = Summaries(h);
        Assert.NotEmpty(summaries);
        Assert.All(summaries, entry =>
        {
            Assert.Matches(@"^(DOU|Jooble): fetched \d+, new \d+, matching the profile \d+, sent \d+, held back \d+$", entry.Message);
            Assert.Equal(
                ["Fetched", "HeldBack", "Matching", "New", "Sent", "Source", RecordingLogger<WatchCycle>.TemplateField],
                entry.Fields.Keys.Order(StringComparer.Ordinal));
        });
    }

    // ---- reading the details of new vacancies (Djinni's vacancy pages) ----

    private const string DetailsTemplate = "{Source}: details read for {Read} of {Fresh} new vacancies, deferred {Deferred}";

    // A vacancy as the Djinni feed gives it: no company, no place, no work mode.
    private static Vacancy FromFeed(string id) =>
        Matching(VacancySource.Djinni, id) with { Company = Placeholders.NotSpecified, WorkMode = WorkMode.Unknown };

    private static FakeEnrichingSource Djinni(params string[] ids) =>
        new(VacancySource.Djinni) { Vacancies = [.. ids.Select(FromFeed)] };

    private static List<RecordingLogger<WatchCycle>.LogEntry> DetailsLines(Harness h) =>
        h.Log.Entries
            .Where(entry => entry.Fields.GetValueOrDefault(RecordingLogger<WatchCycle>.TemplateField) as string == DetailsTemplate)
            .ToList();

    [Fact]
    public async Task The_first_run_reads_no_vacancy_page()
    {
        var djinni = Djinni("1", "2", "3");
        var h = new Harness([djinni]);

        await h.RunAsync();

        Assert.Empty(djinni.Enriched);
        Assert.Equal(["1", "2", "3"], h.Store.SeenIds(VacancySource.Djinni));
        Assert.Empty(DetailsLines(h));
    }

    [Fact]
    public async Task Only_new_vacancies_are_enriched_and_the_filter_sees_the_details()
    {
        var djinni = Djinni("0", "office-lviv", "remote-ua", "office-kyiv");
        djinni.Details = vacancy => vacancy.SourceId switch
        {
            "office-lviv" => vacancy with { Company = "Lviv Soft", WorkMode = WorkMode.Office, Location = "Львів" },
            "office-kyiv" => vacancy with { Company = "Kyiv Soft", WorkMode = WorkMode.Office, Location = "Київ" },
            _ => vacancy with { Company = "Bliscore", WorkMode = WorkMode.Remote, RemoteRestrictedTo = "Ukraine" },
        };
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.Equal(["office-lviv", "remote-ua", "office-kyiv"], djinni.Enriched);
        Assert.False(h.Notifier.WasSent(VacancySource.Djinni, "office-lviv"));
        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "office-lviv"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "remote-ua"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "office-kyiv"));
        Assert.Contains(h.Notifier.VacancyMessages, message => message.Contains("Bliscore") && message.Contains("/djinni/remote-ua\""));
        // The rating is asked for the company the page named.
        Assert.Equal(["Bliscore", "Kyiv Soft"], h.Ratings.Requested);
    }

    [Fact]
    public async Task A_remote_vacancy_limited_to_a_region_outside_the_profile_is_filtered_out()
    {
        var djinni = Djinni("0", "us-only");
        djinni.Details = vacancy => vacancy with { WorkMode = WorkMode.Remote, RemoteRestrictedTo = "US" };
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.Empty(h.Notifier.Sent);
        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "us-only"));
    }

    [Fact]
    public async Task The_minimum_salary_rejects_a_salary_read_from_the_page()
    {
        var profile = new ProfileOptions
        {
            Keyword = Profile.Keyword,
            Synonyms = Profile.Synonyms,
            MinYearsExperience = Profile.MinYearsExperience,
            Cities = Profile.Cities,
            AllowedRemoteRegions = Profile.AllowedRemoteRegions,
            MinSalary = new MinSalaryOptions { Amount = 4000, Currency = "USD", Period = SalaryPeriod.Month },
        };
        var djinni = Djinni("0", "low", "high");
        djinni.Details = vacancy => vacancy with
        {
            WorkMode = WorkMode.Remote,
            RemoteRestrictedTo = "Ukraine",
            Salary = vacancy.SourceId == "low" ? "$2000–2500" : "$4000–5000",
        };
        var h = new Harness([djinni], profile: profile);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.False(h.Notifier.WasSent(VacancySource.Djinni, "low"));
        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "low"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "high"));
    }

    [Fact]
    public async Task At_most_MaxPerCycle_new_vacancies_are_enriched_and_the_rest_wait_for_the_next_cycles()
    {
        var djinni = Djinni("0", "1", "2", "3", "4", "5");
        djinni.MaxPerCycle = 2;
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.Equal(["1", "2"], djinni.Enriched);
        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "1"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "2"));
        Assert.Equal(["0", "1", "2"], h.Store.SeenIds(VacancySource.Djinni));
        AssertSummary(Assert.Single(Summaries(h)), "Djinni", fetched: 6, fresh: 2, matching: 2, sent: 2, heldBack: 0);

        await h.RunAsync();

        Assert.Equal(["1", "2", "3", "4"], djinni.Enriched);
        Assert.Equal(["0", "1", "2", "3", "4"], h.Store.SeenIds(VacancySource.Djinni));

        await h.RunAsync();

        Assert.Equal(["1", "2", "3", "4", "5"], djinni.Enriched);
        Assert.Equal(5, h.Notifier.VacancyMessages.Count());
        Assert.Collection(
            DetailsLines(h),
            first => Assert.Equal("Djinni: details read for 2 of 5 new vacancies, deferred 3", first.Message),
            second => Assert.Equal("Djinni: details read for 2 of 3 new vacancies, deferred 1", second.Message),
            third => Assert.Equal("Djinni: details read for 1 of 1 new vacancies, deferred 0", third.Message));
    }

    [Fact]
    public async Task A_vacancy_whose_details_cannot_be_read_is_sent_as_the_feed_gave_it_with_one_warning()
    {
        var djinni = Djinni("0", "broken", "fine");
        djinni.Details = vacancy => vacancy.SourceId == "broken"
            ? throw new InvalidDataException("The Djinni page of vacancy broken has no JobPosting data.")
            : vacancy with { Company = "Bliscore", WorkMode = WorkMode.Remote };
        var h = new Harness([djinni], failureThreshold: 2);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        var broken = Assert.Single(h.Notifier.VacancyMessages, message => message.Contains("/djinni/broken\"", StringComparison.Ordinal));
        Assert.Contains("🏢 не вказано", broken);
        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "broken"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "fine"));

        var warning = Assert.Single(h.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            "Could not read the details of Djinni vacancy broken: InvalidDataException: The Djinni page of vacancy broken has no JobPosting data.",
            warning.Message);
        Assert.Null(warning.Exception);

        // The source itself answered: no outage is counted (a recorded failure would make this one the second).
        Assert.Empty(h.Notifier.OperationalMessages);
        Assert.False(h.Health.RecordFailure(VacancySource.Djinni));
    }

    [Fact]
    public async Task A_timeout_of_a_page_request_is_a_failed_read_not_a_shutdown()
    {
        var djinni = Djinni("0", "1");
        djinni.Details = _ => throw new TaskCanceledException("The request timed out.");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "1"));
        Assert.Contains(h.Log.Entries, entry => entry.Message == "Could not read the details of Djinni vacancy 1: TaskCanceledException: The request timed out.");
    }

    [Fact]
    public async Task Cancellation_while_reading_details_propagates()
    {
        using var cts = new CancellationTokenSource();
        var djinni = Djinni("0", "1");
        djinni.Details = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cycle.RunOnceAsync(cts.Token));

        Assert.Empty(h.Notifier.Sent);
        Assert.False(h.Store.IsSeen(VacancySource.Djinni, "1"));
    }

    [Fact]
    public async Task The_details_line_is_information_with_its_counts()
    {
        var djinni = Djinni("0", "1", "2", "3");
        djinni.MaxPerCycle = 2;
        djinni.Details = vacancy => vacancy.SourceId == "2" ? throw new InvalidDataException("no data") : vacancy;
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        var line = Assert.Single(DetailsLines(h));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Null(line.Exception);
        Assert.Equal("Djinni: details read for 1 of 3 new vacancies, deferred 1", line.Message);
        Assert.Equal("Djinni", line.Fields["Source"]);
        Assert.Equal(1, Assert.IsType<int>(line.Fields["Read"]));
        Assert.Equal(3, Assert.IsType<int>(line.Fields["Fresh"]));
        Assert.Equal(1, Assert.IsType<int>(line.Fields["Deferred"]));

        // The details line comes before the summary, whose text is unchanged.
        var messages = h.Log.Entries.Select(entry => entry.Message).ToList();
        Assert.True(messages.IndexOf(line.Message) < messages.FindIndex(m => m.StartsWith("Djinni: fetched", StringComparison.Ordinal)));
        AssertSummary(Assert.Single(Summaries(h)), "Djinni", fetched: 4, fresh: 2, matching: 2, sent: 2, heldBack: 0);
    }

    [Fact]
    public async Task The_details_line_is_a_warning_when_no_page_could_be_read()
    {
        var djinni = Djinni("0", "1", "2");
        djinni.Details = _ => throw new InvalidDataException("no data");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        var line = Assert.Single(DetailsLines(h));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Djinni: details read for 0 of 2 new vacancies, deferred 0", line.Message);
        Assert.Equal(2, h.Notifier.VacancyMessages.Count());
    }

    [Fact]
    public async Task No_details_line_when_nothing_is_new()
    {
        var djinni = Djinni("0");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.Empty(djinni.Enriched);
        Assert.Empty(DetailsLines(h));
    }

    [Fact]
    public async Task A_disabled_enricher_reads_nothing_and_defers_nothing()
    {
        var djinni = Djinni("0", "1", "2", "3");
        djinni.IsEnabled = false;
        djinni.MaxPerCycle = 1;
        djinni.Details = _ => throw new InvalidOperationException("must not be called");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync();

        Assert.Empty(djinni.Enriched);
        Assert.Equal(3, h.Notifier.VacancyMessages.Count());
        Assert.Empty(DetailsLines(h));
        AssertSummary(Assert.Single(Summaries(h)), "Djinni", fetched: 4, fresh: 3, matching: 3, sent: 3, heldBack: 0);
    }

    [Fact]
    public async Task A_source_without_an_enricher_logs_no_details_line()
    {
        var dou = new FakeSource(VacancySource.Dou) { Vacancies = [Matching(VacancySource.Dou, "0"), Matching(VacancySource.Dou, "1")] };
        var h = new Harness([dou]);
        h.Store.Preload(VacancySource.Dou, "0");

        await h.RunAsync();

        Assert.True(h.Notifier.WasSent(VacancySource.Dou, "1"));
        Assert.Empty(DetailsLines(h));
        Assert.Equal(["DOU: fetched 2, new 1, matching the profile 1, sent 1, held back 0"], h.Log.Entries.Select(entry => entry.Message));
    }

    // ---- details already read are reused, not read again ----

    private static FakeEnrichingSource DjinniWithCompanies(params string[] ids)
    {
        var djinni = Djinni(ids);
        djinni.Details = vacancy => vacancy with { Company = $"Company {vacancy.SourceId}", WorkMode = WorkMode.Remote };

        return djinni;
    }

    [Fact]
    public async Task Details_of_vacancies_Telegram_refused_are_not_read_again_next_cycle()
    {
        var djinni = DjinniWithCompanies("0", "1", "2");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");
        h.Notifier.FailWhen = html => html.Contains("/djinni/2\"", StringComparison.Ordinal);

        await h.RunAsync();

        Assert.Equal(["1", "2"], djinni.Enriched);
        Assert.False(h.Store.IsSeen(VacancySource.Djinni, "2"));

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();

        Assert.Equal(["1", "2"], djinni.Enriched);
        var sent = Assert.Single(h.Notifier.VacancyMessages, message => message.Contains("/djinni/2\"", StringComparison.Ordinal));
        Assert.Contains("Company 2", sent);
        Assert.Equal("Djinni: details read for 1 of 1 new vacancies, deferred 0", DetailsLines(h)[^1].Message);
    }

    [Fact]
    public async Task Details_of_a_held_back_vacancy_are_not_read_again()
    {
        var djinni = DjinniWithCompanies("0", "1", "2");
        var h = new Harness([djinni], maxMessages: 1);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync(2);

        Assert.Equal(["1", "2"], djinni.Enriched);
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "2"));
        Assert.Contains(h.Notifier.VacancyMessages, message => message.Contains("Company 2"));
    }

    [Fact]
    public async Task A_failed_read_of_a_held_back_vacancy_is_not_repeated_either()
    {
        var djinni = Djinni("0", "1", "2");
        djinni.Details = vacancy => vacancy.SourceId == "2" ? throw new InvalidDataException("no data") : vacancy;
        var h = new Harness([djinni], maxMessages: 1);
        h.Store.Preload(VacancySource.Djinni, "0");

        await h.RunAsync(2);

        Assert.Equal(["1", "2"], djinni.Enriched);
        Assert.Single(h.Log.Entries, entry => entry.Message.StartsWith("Could not read the details", StringComparison.Ordinal));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "2"));
    }

    [Fact]
    public async Task Details_already_read_do_not_count_against_MaxPerCycle()
    {
        var djinni = DjinniWithCompanies("0", "1", "2");
        djinni.MaxPerCycle = 1;
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");
        h.Notifier.FailWhen = html => html.Contains("/djinni/1\"", StringComparison.Ordinal);

        await h.RunAsync();

        Assert.Equal(["1"], djinni.Enriched);

        h.Notifier.FailWhen = _ => false;
        await h.RunAsync();

        // "1" comes from what was read before; the one page read of this cycle goes to "2".
        Assert.Equal(["1", "2"], djinni.Enriched);
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "1"));
        Assert.True(h.Notifier.WasSent(VacancySource.Djinni, "2"));
        Assert.Collection(
            DetailsLines(h),
            first => Assert.Equal("Djinni: details read for 1 of 2 new vacancies, deferred 1", first.Message),
            second => Assert.Equal("Djinni: details read for 2 of 2 new vacancies, deferred 0", second.Message));
    }

    [Fact]
    public async Task Details_are_forgotten_once_the_vacancy_is_stored_as_seen()
    {
        // "1" is sent, "2" is filtered out by its details, "bad" is given up on after three refused sends.
        var djinni = DjinniWithCompanies("0", "1", "2", "bad");
        djinni.Details = vacancy => vacancy.SourceId == "2"
            ? vacancy with { WorkMode = WorkMode.Office, Location = "Львів" }
            : vacancy with { Company = $"Company {vacancy.SourceId}", WorkMode = WorkMode.Remote };
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");
        h.Notifier.FailWhen = html => html.Contains("/djinni/bad\"", StringComparison.Ordinal);

        await h.RunAsync();

        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "1"));
        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "2"));
        Assert.Equal(1, h.Cycle.CachedDetailsCount);

        await h.RunAsync(2);

        Assert.True(h.Store.IsSeen(VacancySource.Djinni, "bad"));
        Assert.Equal(0, h.Cycle.CachedDetailsCount);
        Assert.Equal(["1", "2", "bad"], djinni.Enriched);
    }

    [Fact]
    public async Task Details_of_a_vacancy_that_left_the_feed_are_forgotten()
    {
        var djinni = DjinniWithCompanies("0", "1");
        var h = new Harness([djinni]);
        h.Store.Preload(VacancySource.Djinni, "0");
        h.Notifier.FailWhen = _ => true;

        await h.RunAsync();

        Assert.Equal(1, h.Cycle.CachedDetailsCount);

        djinni.Vacancies = [FromFeed("0")];
        await h.RunAsync();

        Assert.Equal(0, h.Cycle.CachedDetailsCount);
    }

    [Fact]
    public async Task After_a_restart_the_details_are_read_again()
    {
        var djinni = DjinniWithCompanies("0", "1");
        var before = new Harness([djinni]);
        before.Store.Preload(VacancySource.Djinni, "0");
        before.Notifier.FailWhen = _ => true;
        await before.RunAsync();

        var after = new Harness([djinni]);
        after.Store.Preload(VacancySource.Djinni, "0");
        await after.RunAsync();

        Assert.Equal(["1", "1"], djinni.Enriched);
        Assert.True(after.Notifier.WasSent(VacancySource.Djinni, "1"));
    }
}
