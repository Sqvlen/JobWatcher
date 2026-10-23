using JobWatcher.Domain;

namespace JobWatcher.Configuration;

public sealed class WatcherOptions
{
    public const string SectionName = "JobWatcher";

    public TelegramOptions Telegram { get; set; } = new();

    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>Upper bound on Telegram messages sent per polling cycle; null means no limit.</summary>
    public int? MaxMessagesPerCycle { get; set; }

    public int FailureAlertThreshold { get; set; } = 3;

    public string DataDir { get; set; } = "./data";

    public ProfileOptions Profile { get; set; } = new();

    public SourcesOptions Sources { get; set; } = new();
}

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";

    public string ChatId { get; set; } = "";
}

/// <remarks>
/// The list defaults live in appsettings.json, not here: the configuration binder appends configured
/// values to a pre-filled array, so defaults set in code could never be removed from the config file.
/// </remarks>
public sealed class ProfileOptions
{
    public string Keyword { get; set; } = ".NET";

    public string[] Synonyms { get; set; } = [];

    public int MinYearsExperience { get; set; } = 3;

    public string[] Cities { get; set; } = [];

    public string[] AllowedRemoteRegions { get; set; } = [];

    /// <summary>Minimum acceptable salary; null means no salary filter.</summary>
    public MinSalaryOptions? MinSalary { get; set; }
}

public sealed class MinSalaryOptions
{
    public decimal Amount { get; set; }

    /// <summary>Currency code, e.g. "USD".</summary>
    public string Currency { get; set; } = "";

    public SalaryPeriod Period { get; set; } = SalaryPeriod.Month;
}

public sealed class SourcesOptions
{
    public DouOptions Dou { get; set; } = new();

    public JoobleOptions Jooble { get; set; } = new();

    public DjinniOptions Djinni { get; set; } = new();

    public JobsPipeOptions JobsPipe { get; set; } = new();

    public EleksOptions Eleks { get; set; } = new();

    /// <summary>
    /// LinkedIn has no lawful access route (docs/sources/linkedin.md), so enabling it only registers a stand-in
    /// that tells the user once that the source is not connected.
    /// </summary>
    public ToggleOptions LinkedIn { get; set; } = new();
}

public class ToggleOptions
{
    public bool Enabled { get; set; }
}

public sealed class DouOptions : ToggleOptions
{
    public DouOptions() => Enabled = true;

    public string FeedUrl { get; set; } = "";
}

public sealed class DjinniOptions : ToggleOptions
{
    public string FeedUrl { get; set; } = "";

    /// <summary>
    /// Whether the public page of each new vacancy is read for its company, salary, place, work format and years
    /// (the feed has none of them; see docs/sources/djinni.md). Only the page's JSON-LD block is used.
    /// </summary>
    public bool ReadVacancyPages { get; set; } = true;

    /// <summary>The least time between two vacancy page requests, in seconds.</summary>
    public int PageRequestDelaySeconds { get; set; } = 2;

    /// <summary>How many vacancy pages are read per cycle at most; further new vacancies wait for the next cycle.</summary>
    public int MaxPagesPerCycle { get; set; } = 15;
}

public sealed class JoobleOptions : ToggleOptions
{
    public JoobleOptions() => Enabled = true;

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// The Jooble API address without the key; the key is appended as the last path segment. A key works only on
    /// the Jooble domain it was issued for, so a Ukrainian key needs https://ua.jooble.org/api.
    /// </summary>
    public string BaseUrl { get; set; } = "https://jooble.org/api";

    /// <summary>
    /// The least time between two real fetches. A free Jooble key allows 500 requests in its whole life and a fetch
    /// takes two, so until this many minutes have passed the source makes no request: it answers with an empty list
    /// when the last real fetch succeeded, and throws the last failure again when it failed (so the failure keeps
    /// counting towards the "source is down" alert).
    /// </summary>
    public int MinFetchIntervalMinutes { get; set; } = 720;
}

public sealed class JobsPipeOptions : ToggleOptions
{
    /// <summary>The JobsPipe API key (it starts with <c>jp_live_</c>), sent as a bearer token.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The JobsPipe API address; the search path is appended to it.</summary>
    public string BaseUrl { get; set; } = "https://api.jobspipe.dev";

    /// <summary>
    /// The least time between two real searches. Until this many minutes have passed the source makes no request: it
    /// answers with an empty list when the last search succeeded, and throws the last failure again when it failed (so
    /// the failure keeps counting towards the "source is down" alert). A search is paid per vacancy that the key has not
    /// received this month, not per request, but the plan also has a monthly request quota that the docs do not number.
    /// Once a day by default: even a search that returned 25 vacancies nobody had received yet would cost 25 credits,
    /// so a search a day stays under the 1000 free credits (30 x 25 = 750), which several a day would not.
    /// </summary>
    public int MinFetchIntervalMinutes { get; set; } = 1440;

    /// <summary>Only vacancies posted within this many days are asked for.</summary>
    public int MaxAgeDays { get; set; } = 14;

    /// <summary>
    /// How many vacancies one search asks for (the newest first). The plan caps it: 25 on the free plan, so a larger
    /// value is cut down by JobsPipe, not refused.
    /// </summary>
    public int PageSize { get; set; } = 25;

    /// <summary>
    /// The JobsPipe boards whose vacancies are left out (its <c>source_not</c> filter); their names are JobsPipe's
    /// ("linkedin", "indeed", "greenhouse", ...). The default list lives in appsettings.json, not here, because the
    /// configuration binder appends configured values to a pre-filled array.
    /// </summary>
    public string[] ExcludedSources { get; set; } = [];
}

public sealed class EleksOptions : ToggleOptions
{
    /// <summary>The only site the source requests anything from.</summary>
    public const string Host = "careers.eleks.com";

    /// <summary>
    /// The vacancy list of careers.eleks.com that is watched, with its filters in the address (see docs/sources/eleks.md):
    /// <c>technology=21,27</c> is .NET, <c>location=206,98</c> is Ukraine or Remote. Eleks decides which vacancies are
    /// in Ukraine or remote: the place of a vacancy is not checked again, and the profile's cities are not used. The
    /// address of the default lives in appsettings.json. Prefer these plain parameters to a <c>?f=</c> link: that is a
    /// short code that the site stores, and a code it no longer knows lists every vacancy instead of failing.
    /// </summary>
    public string ListUrl { get; set; } = "";

    /// <summary>
    /// The least time between two real fetches of the list; once a day by default, unlike the feeds. Until this many
    /// minutes have passed the source makes no request: it answers with an empty list when the last fetch succeeded,
    /// and throws the last failure again when it failed (so the failure keeps counting towards the "source is down"
    /// alert).
    /// </summary>
    public int MinFetchIntervalMinutes { get; set; } = 1440;

    /// <summary>
    /// Whether the public page of each new vacancy is read for its description, from which the requirements and the
    /// years of experience are taken (the list has only the title, the places and the tags).
    /// </summary>
    public bool ReadVacancyPages { get; set; } = true;

    /// <summary>The least time between two requests to the Eleks site (the list or a vacancy page), in seconds.</summary>
    public int PageRequestDelaySeconds { get; set; } = 2;

    /// <summary>How many vacancy pages are read per cycle at most; further new vacancies wait for the next cycle.</summary>
    public int MaxPagesPerCycle { get; set; } = 15;
}
