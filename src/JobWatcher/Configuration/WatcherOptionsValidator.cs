using JobWatcher.Domain;
using Microsoft.Extensions.Options;

namespace JobWatcher.Configuration;

public sealed class WatcherOptionsValidator : IValidateOptions<WatcherOptions>
{
    // The poll timer rejects very long periods; a week is far more than any sensible polling interval.
    private const int MaxPollIntervalMinutes = 10080;

    // The least poll interval while an RSS feed is watched: DOU serves its feed with "Cache-Control: max-age=600"
    // (see docs/sources/dou.md); Djinni announces no polling interval and its feed is about 270 KB
    // (see docs/sources/djinni.md), so it is polled no more often than DOU.
    private const int FeedPollFloorMinutes = 10;

    public ValidateOptionsResult Validate(string? name, WatcherOptions o)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(o.Telegram.BotToken))
        {
            failures.Add("Telegram bot token is not set. Set the environment variable JobWatcher__Telegram__BotToken (or the Key Vault secret JobWatcher--Telegram--BotToken).");
        }

        if (string.IsNullOrWhiteSpace(o.Telegram.ChatId))
        {
            failures.Add("Telegram chat id is not set. Set the environment variable JobWatcher__Telegram__ChatId (or the Key Vault secret JobWatcher--Telegram--ChatId).");
        }

        if (o.PollIntervalMinutes < 1)
        {
            failures.Add($"Poll interval must be at least 1 minute (got {o.PollIntervalMinutes}). Fix the environment variable JobWatcher__PollIntervalMinutes.");
        }

        if (o.PollIntervalMinutes > MaxPollIntervalMinutes)
        {
            failures.Add($"Poll interval must be at most {MaxPollIntervalMinutes} minutes, one week (got {o.PollIntervalMinutes}). Fix the environment variable JobWatcher__PollIntervalMinutes.");
        }

        if ((o.Sources.Dou.Enabled || o.Sources.Djinni.Enabled) && o.PollIntervalMinutes < FeedPollFloorMinutes)
        {
            failures.Add($"Polling the DOU or Djinni feed more often than every {FeedPollFloorMinutes} minutes is impolite (poll interval is {o.PollIntervalMinutes}): DOU's feed is cached for {FeedPollFloorMinutes} minutes, so more frequent polls are pointless, and Djinni announces no polling interval while its feed is about 270 KB. Set the environment variable JobWatcher__PollIntervalMinutes to at least {FeedPollFloorMinutes}, or disable both feeds with JobWatcher__Sources__Dou__Enabled=false and JobWatcher__Sources__Djinni__Enabled=false.");
        }

        if (o.FailureAlertThreshold < 1)
        {
            failures.Add($"Failure alert threshold must be at least 1 (got {o.FailureAlertThreshold}). Fix the environment variable JobWatcher__FailureAlertThreshold.");
        }

        if (string.IsNullOrWhiteSpace(o.DataDir))
        {
            failures.Add("Data directory is not set. Set the environment variable JobWatcher__DataDir (the folder that holds the seen-vacancies database).");
        }

        if (o.MaxMessagesPerCycle is < 1)
        {
            failures.Add($"Max messages per cycle must be at least 1 when set (got {o.MaxMessagesPerCycle}). Fix or unset the environment variable JobWatcher__MaxMessagesPerCycle.");
        }

        if (string.IsNullOrWhiteSpace(o.Profile.Keyword))
        {
            failures.Add("Profile keyword is not set. Set the environment variable JobWatcher__Profile__Keyword (a blank keyword would match every vacancy).");
        }

        if (o.Profile.MinYearsExperience < 0)
        {
            failures.Add($"Profile minimum years of experience must be at least 0 (got {o.Profile.MinYearsExperience}). Fix the environment variable JobWatcher__Profile__MinYearsExperience.");
        }

        AddBlankEntryFailures(failures, o.Profile.Synonyms, "JobWatcher__Profile__Synonyms");
        AddBlankEntryFailures(failures, o.Profile.Cities, "JobWatcher__Profile__Cities");
        AddBlankEntryFailures(failures, o.Profile.AllowedRemoteRegions, "JobWatcher__Profile__AllowedRemoteRegions");

        if (o.Profile.MinSalary is { } minSalary)
        {
            if (string.IsNullOrWhiteSpace(minSalary.Currency))
            {
                failures.Add("Minimum salary currency is not set. Set the environment variable JobWatcher__Profile__MinSalary__Currency (for example USD), or unset the minimum salary.");
            }

            if (minSalary.Amount <= 0)
            {
                failures.Add($"Minimum salary amount must be greater than 0 (got {minSalary.Amount}). Fix or unset the environment variable JobWatcher__Profile__MinSalary__Amount.");
            }

            if (minSalary.Period is not (SalaryPeriod.Month or SalaryPeriod.Year))
            {
                failures.Add($"Minimum salary period must be Month or Year (got {minSalary.Period}). Fix the environment variable JobWatcher__Profile__MinSalary__Period.");
            }
        }

        // LinkedIn counts as enabled but is not offered as a fix: it only reports that it is not connected.
        var sources = o.Sources;
        if (!sources.Dou.Enabled && !sources.Jooble.Enabled && !sources.Djinni.Enabled && !sources.LinkedIn.Enabled)
        {
            failures.Add("No job source is enabled. Set at least one of JobWatcher__Sources__Dou__Enabled, JobWatcher__Sources__Jooble__Enabled, JobWatcher__Sources__Djinni__Enabled to true.");
        }

        if (sources.Dou.Enabled && !IsAbsoluteHttpUrl(sources.Dou.FeedUrl))
        {
            failures.Add("DOU is enabled but its feed URL is not an absolute http(s) URL. Set the environment variable JobWatcher__Sources__Dou__FeedUrl (or disable DOU with JobWatcher__Sources__Dou__Enabled=false).");
        }

        if (sources.Djinni.Enabled && !IsAbsoluteHttpUrl(sources.Djinni.FeedUrl))
        {
            failures.Add("Djinni is enabled but its feed URL is not an absolute http(s) URL. Set the environment variable JobWatcher__Sources__Djinni__FeedUrl (or disable Djinni with JobWatcher__Sources__Djinni__Enabled=false).");
        }

        if (sources.Djinni.Enabled && sources.Djinni.ReadVacancyPages && sources.Djinni.PageRequestDelaySeconds < 1)
        {
            failures.Add($"Djinni page request delay must be at least 1 second (got {sources.Djinni.PageRequestDelaySeconds}): vacancy pages are read one by one and politely apart. Fix the environment variable JobWatcher__Sources__Djinni__PageRequestDelaySeconds (or stop reading vacancy pages with JobWatcher__Sources__Djinni__ReadVacancyPages=false).");
        }

        if (sources.Djinni.Enabled && sources.Djinni.ReadVacancyPages && sources.Djinni.MaxPagesPerCycle < 1)
        {
            failures.Add($"Djinni max pages per cycle must be at least 1 (got {sources.Djinni.MaxPagesPerCycle}), or no new Djinni vacancy would ever be announced. Fix the environment variable JobWatcher__Sources__Djinni__MaxPagesPerCycle (or stop reading vacancy pages with JobWatcher__Sources__Djinni__ReadVacancyPages=false).");
        }

        if (sources.Jooble.Enabled && string.IsNullOrWhiteSpace(sources.Jooble.ApiKey))
        {
            failures.Add("Jooble is enabled but its API key is not set. Set the environment variable JobWatcher__Sources__Jooble__ApiKey (or the Key Vault secret JobWatcher--Sources--Jooble--ApiKey), or disable it with JobWatcher__Sources__Jooble__Enabled=false.");
        }

        if (sources.Jooble.Enabled && !IsAbsoluteHttpsUrl(sources.Jooble.BaseUrl))
        {
            failures.Add("Jooble is enabled but its base URL is not an absolute https URL. Set the environment variable JobWatcher__Sources__Jooble__BaseUrl (for example https://jooble.org/api).");
        }

        // The key is appended to the URL's path; after a query or a fragment it would land in those instead.
        if (sources.Jooble.Enabled && IsAbsoluteHttpsUrl(sources.Jooble.BaseUrl) && sources.Jooble.BaseUrl.IndexOfAny(['?', '#']) >= 0)
        {
            failures.Add("Jooble base URL must not have a query string or a fragment ('?' or '#'), because the API key is appended to its path. Fix the environment variable JobWatcher__Sources__Jooble__BaseUrl (for example https://ua.jooble.org/api).");
        }

        if (sources.Jooble.Enabled && sources.Jooble.MinFetchIntervalMinutes < 1)
        {
            failures.Add($"Jooble minimum fetch interval must be at least 1 minute (got {sources.Jooble.MinFetchIntervalMinutes}). Fix the environment variable JobWatcher__Sources__Jooble__MinFetchIntervalMinutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // On Unix a path such as "/vacancies/feeds/" parses as an absolute file URI, hence the scheme check.
    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // The Jooble API key travels in the URL path, so the connection must be encrypted.
    private static bool IsAbsoluteHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https";

    // An empty list is fine; a blank entry is not, because a blank term matches every vacancy.
    private static void AddBlankEntryFailures(List<string> failures, string[] entries, string keyPrefix)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(entries[i]))
            {
                failures.Add($"Entry {i} of the list {keyPrefix} is blank, which would match every vacancy. Fill in or remove the environment variable {keyPrefix}__{i}.");
            }
        }
    }
}
