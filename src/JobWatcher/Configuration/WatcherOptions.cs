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
