using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Configuration;

public class WatcherOptionsValidatorTests
{
    private static readonly WatcherOptionsValidator Validator = new();

    private static WatcherOptions CompleteConfig() => new()
    {
        Telegram = { BotToken = "123456:token", ChatId = "42" },
        Sources =
        {
            Dou = { FeedUrl = "https://jobs.dou.ua/vacancies/feeds/?category=.NET" },
            Jooble = { ApiKey = "jooble-key" },
        },
    };

    private static void AssertFailedNaming(ValidateOptionsResult result, string expectedKey)
    {
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expectedKey));
    }

    [Fact]
    public void Validate_fails_when_bot_token_blank()
    {
        var options = CompleteConfig();
        options.Telegram.BotToken = "  ";

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Telegram__BotToken");
    }

    [Fact]
    public void Validate_fails_when_chat_id_blank()
    {
        var options = CompleteConfig();
        options.Telegram.ChatId = "";

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Telegram__ChatId");
    }

    [Fact]
    public void The_missing_secrets_name_their_key_vault_secrets_too()
    {
        var options = CompleteConfig();
        options.Telegram.BotToken = "";
        options.Telegram.ChatId = " ";
        options.Sources.Jooble.Enabled = true;
        options.Sources.Jooble.ApiKey = "";

        var result = Validator.Validate(null, options);

        Assert.Contains(
            "Telegram bot token is not set. Set the environment variable JobWatcher__Telegram__BotToken (or the Key Vault secret JobWatcher--Telegram--BotToken).",
            result.Failures!);
        Assert.Contains(
            "Telegram chat id is not set. Set the environment variable JobWatcher__Telegram__ChatId (or the Key Vault secret JobWatcher--Telegram--ChatId).",
            result.Failures!);
        Assert.Contains(
            "Jooble is enabled but its API key is not set. Set the environment variable JobWatcher__Sources__Jooble__ApiKey (or the Key Vault secret JobWatcher--Sources--Jooble--ApiKey), or disable it with JobWatcher__Sources__Jooble__Enabled=false.",
            result.Failures!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_fails_when_poll_interval_below_1(int minutes)
    {
        var options = CompleteConfig();
        options.PollIntervalMinutes = minutes;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__PollIntervalMinutes");
    }

    [Theory]
    [InlineData(10081)]
    [InlineData(int.MaxValue)]
    public void Validate_fails_when_poll_interval_above_one_week(int minutes)
    {
        var options = CompleteConfig();
        options.PollIntervalMinutes = minutes;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__PollIntervalMinutes");
        Assert.Contains(result.Failures!, failure => failure.Contains("10080"));
    }

    [Fact]
    public void Validate_accepts_a_poll_interval_of_exactly_one_week()
    {
        var options = CompleteConfig();
        options.PollIntervalMinutes = 10080;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_fails_when_failure_alert_threshold_below_1(int threshold)
    {
        var options = CompleteConfig();
        options.FailureAlertThreshold = threshold;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__FailureAlertThreshold");
    }

    [Fact]
    public void Validate_accepts_a_failure_alert_threshold_of_1()
    {
        var options = CompleteConfig();
        options.FailureAlertThreshold = 1;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_fails_when_data_dir_blank(string dataDir)
    {
        var options = CompleteConfig();
        options.DataDir = dataDir;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__DataDir");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Validate_fails_when_dou_enabled_and_poll_interval_below_the_feed_cache_time(int minutes)
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = true;
        options.PollIntervalMinutes = minutes;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__PollIntervalMinutes");
        var failure = Assert.Single(result.Failures!, f => f.Contains("DOU"));
        Assert.Contains("cached for 10 minutes", failure);
        Assert.Contains("impolite", failure);
        Assert.Contains("JobWatcher__Sources__Dou__Enabled", failure);
        Assert.Contains("JobWatcher__Sources__Djinni__Enabled", failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Validate_fails_when_djinni_enabled_and_poll_interval_below_10_minutes(int minutes)
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Djinni.Enabled = true;
        options.Sources.Djinni.FeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";
        options.PollIntervalMinutes = minutes;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__PollIntervalMinutes");
        var failure = Assert.Single(result.Failures!, f => f.Contains("Djinni"));
        Assert.Contains("DOU", failure);
        Assert.Contains("JobWatcher__Sources__Djinni__Enabled", failure);
    }

    [Fact]
    public void Validate_accepts_a_poll_interval_of_10_minutes_with_djinni_enabled()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Djinni.Enabled = true;
        options.Sources.Djinni.FeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";
        options.PollIntervalMinutes = 10;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_accepts_a_poll_interval_of_10_minutes_with_dou_enabled()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = true;
        options.PollIntervalMinutes = 10;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_accepts_a_fast_poll_interval_when_dou_and_djinni_are_disabled()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Djinni.Enabled = false;
        options.PollIntervalMinutes = 1;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_fails_when_no_source_enabled()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Jooble.Enabled = false;
        options.Sources.Djinni.Enabled = false;
        options.Sources.LinkedIn.Enabled = false;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Dou__Enabled");
        AssertFailedNaming(result, "JobWatcher__Sources__Jooble__Enabled");
        AssertFailedNaming(result, "JobWatcher__Sources__Djinni__Enabled");
        AssertFailedNaming(result, "JobWatcher__Sources__JobsPipe__Enabled");

        // LinkedIn watches nothing, so enabling it is not offered as a fix.
        Assert.DoesNotContain(result.Failures!, failure => failure.Contains("JobWatcher__Sources__LinkedIn__Enabled"));
    }

    [Fact]
    public void Validate_counts_linkedin_as_an_enabled_source()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Jooble.Enabled = false;
        options.Sources.Djinni.Enabled = false;
        options.Sources.LinkedIn.Enabled = true;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_fails_when_jooble_enabled_without_api_key(string apiKey)
    {
        var options = CompleteConfig();
        options.Sources.Jooble.Enabled = true;
        options.Sources.Jooble.ApiKey = apiKey;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Jooble__ApiKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jooble.org/api")]
    [InlineData("/api")]
    [InlineData("not a url")]
    [InlineData("http://jooble.org/api")]
    [InlineData("ftp://jooble.org/api")]
    [InlineData("file:///etc/passwd")]
    public void Validate_fails_when_jooble_enabled_without_an_absolute_https_base_url(string baseUrl)
    {
        var options = CompleteConfig();
        options.Sources.Jooble.Enabled = true;
        options.Sources.Jooble.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Jooble__BaseUrl");
    }

    [Theory]
    [InlineData("https://ua.jooble.org/api?lang=uk")]
    [InlineData("https://ua.jooble.org/api/?")]
    [InlineData("https://ua.jooble.org/api#top")]
    [InlineData("https://ua.jooble.org/api?a=1#top")]
    public void Validate_fails_when_jooble_base_url_has_a_query_or_fragment(string baseUrl)
    {
        var options = CompleteConfig();
        options.Sources.Jooble.Enabled = true;
        options.Sources.Jooble.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("JobWatcher__Sources__Jooble__BaseUrl", failure);
        Assert.Contains("query", failure);
    }

    [Theory]
    [InlineData("https://jooble.org/api")]
    [InlineData("https://ua.jooble.org/api/")]
    public void Validate_accepts_an_absolute_https_jooble_base_url(string baseUrl)
    {
        var options = CompleteConfig();
        options.Sources.Jooble.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_fails_when_jooble_enabled_with_a_min_fetch_interval_below_1(int minutes)
    {
        var options = CompleteConfig();
        options.Sources.Jooble.Enabled = true;
        options.Sources.Jooble.MinFetchIntervalMinutes = minutes;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Jooble__MinFetchIntervalMinutes");
    }

    [Fact]
    public void Validate_accepts_a_min_fetch_interval_of_1_minute()
    {
        var options = CompleteConfig();
        options.Sources.Jooble.MinFetchIntervalMinutes = 1;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_does_not_check_the_jooble_settings_when_jooble_is_disabled()
    {
        var options = CompleteConfig();
        options.Sources.Jooble.Enabled = false;
        options.Sources.Jooble.ApiKey = "";
        options.Sources.Jooble.BaseUrl = "";
        options.Sources.Jooble.MinFetchIntervalMinutes = 0;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    private static WatcherOptions JobsPipeConfig()
    {
        var options = CompleteConfig();
        options.Sources.JobsPipe.Enabled = true;
        options.Sources.JobsPipe.ApiKey = "jobspipe-key";

        return options;
    }

    [Fact]
    public void Validate_accepts_a_complete_jobspipe_configuration()
    {
        var result = Validator.Validate(null, JobsPipeConfig());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_fails_when_jobspipe_enabled_without_api_key(string apiKey)
    {
        var options = JobsPipeConfig();
        options.Sources.JobsPipe.ApiKey = apiKey;

        var result = Validator.Validate(null, options);

        Assert.Contains(
            "JobsPipe is enabled but its API key is not set. Set the environment variable JobWatcher__Sources__JobsPipe__ApiKey (or the Key Vault secret JobWatcher--Sources--JobsPipe--ApiKey), or disable it with JobWatcher__Sources__JobsPipe__Enabled=false.",
            result.Failures!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.jobspipe.dev")]
    [InlineData("not a url")]
    [InlineData("http://api.jobspipe.dev")]
    [InlineData("file:///etc/passwd")]
    public void Validate_fails_when_jobspipe_enabled_without_an_absolute_https_base_url(string baseUrl)
    {
        var options = JobsPipeConfig();
        options.Sources.JobsPipe.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__JobsPipe__BaseUrl");
    }

    [Theory]
    [InlineData("https://api.jobspipe.dev?x=1")]
    [InlineData("https://api.jobspipe.dev#top")]
    public void Validate_fails_when_jobspipe_base_url_has_a_query_or_fragment(string baseUrl)
    {
        var options = JobsPipeConfig();
        options.Sources.JobsPipe.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("JobWatcher__Sources__JobsPipe__BaseUrl", failure);
        Assert.Contains("query", failure);
    }

    [Theory]
    [InlineData("https://api.jobspipe.dev")]
    [InlineData("https://api.jobspipe.dev/")]
    public void Validate_accepts_an_absolute_https_jobspipe_base_url(string baseUrl)
    {
        var options = JobsPipeConfig();
        options.Sources.JobsPipe.BaseUrl = baseUrl;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(nameof(JobsPipeOptions.MinFetchIntervalMinutes), 0)]
    [InlineData(nameof(JobsPipeOptions.MinFetchIntervalMinutes), -1)]
    [InlineData(nameof(JobsPipeOptions.MaxAgeDays), 0)]
    [InlineData(nameof(JobsPipeOptions.MaxAgeDays), -3)]
    [InlineData(nameof(JobsPipeOptions.PageSize), 0)]
    [InlineData(nameof(JobsPipeOptions.PageSize), 501)]
    public void Validate_fails_when_a_jobspipe_number_is_out_of_range(string setting, int value)
    {
        var options = JobsPipeConfig();
        typeof(JobsPipeOptions).GetProperty(setting)!.SetValue(options.Sources.JobsPipe, value);

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, $"JobWatcher__Sources__JobsPipe__{setting}");
    }

    [Theory]
    [InlineData(nameof(JobsPipeOptions.MinFetchIntervalMinutes), 1)]
    [InlineData(nameof(JobsPipeOptions.MaxAgeDays), 1)]
    [InlineData(nameof(JobsPipeOptions.PageSize), 1)]
    [InlineData(nameof(JobsPipeOptions.PageSize), 500)]
    public void Validate_accepts_the_edges_of_the_jobspipe_number_ranges(string setting, int value)
    {
        var options = JobsPipeConfig();
        typeof(JobsPipeOptions).GetProperty(setting)!.SetValue(options.Sources.JobsPipe, value);

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_fails_when_an_excluded_jobspipe_source_is_blank()
    {
        var options = JobsPipeConfig();
        options.Sources.JobsPipe.ExcludedSources = ["linkedin", " "];

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__JobsPipe__ExcludedSources__1");
    }

    [Fact]
    public void Validate_does_not_check_the_jobspipe_settings_when_jobspipe_is_disabled()
    {
        var options = CompleteConfig();
        options.Sources.JobsPipe.Enabled = false;
        options.Sources.JobsPipe.ApiKey = "";
        options.Sources.JobsPipe.BaseUrl = "";
        options.Sources.JobsPipe.MinFetchIntervalMinutes = 0;
        options.Sources.JobsPipe.MaxAgeDays = 0;
        options.Sources.JobsPipe.PageSize = 0;
        options.Sources.JobsPipe.ExcludedSources = [""];

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_counts_jobspipe_as_an_enabled_source()
    {
        var options = JobsPipeConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Jooble.Enabled = false;
        options.Sources.Djinni.Enabled = false;
        options.Sources.LinkedIn.Enabled = false;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jobs.dou.ua/vacancies/feeds/")]
    [InlineData("/vacancies/feeds/")]
    [InlineData("not a url")]
    [InlineData("ftp://jobs.dou.ua/vacancies/feeds/")]
    [InlineData("file:///etc/passwd")]
    public void Validate_fails_when_dou_enabled_without_an_http_feed_url(string feedUrl)
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = true;
        options.Sources.Dou.FeedUrl = feedUrl;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Dou__FeedUrl");
    }

    [Theory]
    [InlineData("https://jobs.dou.ua/vacancies/feeds/?category=.NET")]
    [InlineData("http://localhost:8080/dou.xml")]
    public void Validate_accepts_an_absolute_http_or_https_dou_feed_url(string feedUrl)
    {
        var options = CompleteConfig();
        options.Sources.Dou.FeedUrl = feedUrl;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ignores_dou_feed_url_when_dou_disabled()
    {
        var options = CompleteConfig();
        options.Sources.Dou.Enabled = false;
        options.Sources.Dou.FeedUrl = "";

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("djinni.co/jobs/rss/")]
    [InlineData("/jobs/rss/")]
    [InlineData("not a url")]
    [InlineData("ftp://djinni.co/jobs/rss/")]
    [InlineData("file:///etc/passwd")]
    public void Validate_fails_when_djinni_enabled_without_an_http_feed_url(string feedUrl)
    {
        var options = CompleteConfig();
        options.Sources.Djinni.Enabled = true;
        options.Sources.Djinni.FeedUrl = feedUrl;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Djinni__FeedUrl");
        var failure = Assert.Single(result.Failures!, f => f.Contains("Djinni"));
        Assert.Contains("JobWatcher__Sources__Djinni__Enabled=false", failure);
    }

    [Theory]
    [InlineData("https://djinni.co/jobs/rss/?primary_keyword=.NET")]
    [InlineData("http://localhost:8080/djinni.xml")]
    public void Validate_accepts_an_absolute_http_or_https_djinni_feed_url(string feedUrl)
    {
        var options = CompleteConfig();
        options.Sources.Djinni.Enabled = true;
        options.Sources.Djinni.FeedUrl = feedUrl;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ignores_djinni_feed_url_when_djinni_disabled()
    {
        var options = CompleteConfig();
        options.Sources.Djinni.Enabled = false;
        options.Sources.Djinni.FeedUrl = "";

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_fails_when_max_messages_per_cycle_set_below_1(int limit)
    {
        var options = CompleteConfig();
        options.MaxMessagesPerCycle = limit;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__MaxMessagesPerCycle");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Validate_fails_when_min_salary_amount_not_positive(int amount)
    {
        var options = CompleteConfig();
        options.Profile.MinSalary = new MinSalaryOptions { Amount = amount, Currency = "USD", Period = SalaryPeriod.Month };

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Profile__MinSalary__Amount");
    }

    [Theory]
    [InlineData(SalaryPeriod.Unknown)]
    [InlineData(SalaryPeriod.Hour)]
    public void Validate_fails_when_min_salary_period_is_not_month_or_year(SalaryPeriod period)
    {
        var options = CompleteConfig();
        options.Profile.MinSalary = new MinSalaryOptions { Amount = 3000, Currency = "USD", Period = period };

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Profile__MinSalary__Period");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_fails_when_min_salary_currency_blank(string currency)
    {
        var options = CompleteConfig();
        options.Profile.MinSalary = new MinSalaryOptions { Amount = 3000, Currency = currency, Period = SalaryPeriod.Month };

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Profile__MinSalary__Currency");
    }

    [Fact]
    public void Validate_ignores_min_salary_currency_when_min_salary_not_set()
    {
        var options = CompleteConfig();
        options.Profile.MinSalary = null;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_fails_when_profile_keyword_blank(string keyword)
    {
        var options = CompleteConfig();
        options.Profile.Keyword = keyword;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Profile__Keyword");
    }

    [Theory]
    [InlineData("Synonyms")]
    [InlineData("Cities")]
    [InlineData("AllowedRemoteRegions")]
    public void Validate_fails_when_profile_list_has_blank_entry(string listName)
    {
        var options = CompleteConfig();
        SetProfileList(options.Profile, listName, ["valid", "  "]);

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, $"JobWatcher__Profile__{listName}__1");
    }

    [Theory]
    [InlineData("Synonyms")]
    [InlineData("Cities")]
    [InlineData("AllowedRemoteRegions")]
    public void Validate_fails_when_profile_list_has_empty_string_entry(string listName)
    {
        var options = CompleteConfig();
        SetProfileList(options.Profile, listName, [""]);

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, $"JobWatcher__Profile__{listName}__0");
    }

    [Theory]
    [InlineData("Synonyms")]
    [InlineData("Cities")]
    [InlineData("AllowedRemoteRegions")]
    public void Validate_allows_empty_profile_list(string listName)
    {
        var options = CompleteConfig();
        SetProfileList(options.Profile, listName, []);

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_fails_when_profile_min_years_experience_is_negative(int years)
    {
        var options = CompleteConfig();
        options.Profile.MinYearsExperience = years;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Profile__MinYearsExperience");
    }

    [Fact]
    public void Validate_accepts_a_profile_min_years_experience_of_0()
    {
        var options = CompleteConfig();
        options.Profile.MinYearsExperience = 0;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_succeeds_for_complete_profile()
    {
        var options = CompleteConfig();
        options.Profile.Keyword = ".NET";
        options.Profile.Synonyms = ["C#", "ASP.NET", "dotnet"];
        options.Profile.Cities = ["Київ", "Kyiv"];
        options.Profile.AllowedRemoteRegions = ["Ukraine", "Europe", "EU", "Worldwide"];
        options.Profile.MinSalary = new MinSalaryOptions { Amount = 4000, Currency = "USD", Period = SalaryPeriod.Month };

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    private static void SetProfileList(ProfileOptions profile, string listName, string[] values)
    {
        switch (listName)
        {
            case nameof(ProfileOptions.Synonyms):
                profile.Synonyms = values;
                break;
            case nameof(ProfileOptions.Cities):
                profile.Cities = values;
                break;
            case nameof(ProfileOptions.AllowedRemoteRegions):
                profile.AllowedRemoteRegions = values;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(listName), listName, null);
        }
    }

    [Fact]
    public void Validate_succeeds_without_optional_limit_and_salary()
    {
        var options = CompleteConfig();
        options.MaxMessagesPerCycle = null;
        options.Profile.MinSalary = null;

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_succeeds_for_complete_config()
    {
        var options = CompleteConfig();
        options.MaxMessagesPerCycle = 20;
        options.Profile.MinSalary = new MinSalaryOptions { Amount = 3000, Currency = "USD", Period = SalaryPeriod.Month };

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    // ---- reading Djinni vacancy pages ----

    private static WatcherOptions WithDjinniReadingPages()
    {
        var options = CompleteConfig();
        options.Sources.Djinni.Enabled = true;
        options.Sources.Djinni.FeedUrl = "https://djinni.co/jobs/rss/?primary_keyword=.NET";
        options.Sources.Djinni.ReadVacancyPages = true;

        return options;
    }

    [Fact]
    public void Djinni_page_settings_default_to_reading_15_pages_2_seconds_apart()
    {
        var djinni = new DjinniOptions();

        Assert.True(djinni.ReadVacancyPages);
        Assert.Equal(2, djinni.PageRequestDelaySeconds);
        Assert.Equal(15, djinni.MaxPagesPerCycle);
        Assert.True(Validator.Validate(null, WithDjinniReadingPages()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_fails_when_djinni_reads_pages_with_a_page_delay_below_1_second(int seconds)
    {
        var options = WithDjinniReadingPages();
        options.Sources.Djinni.PageRequestDelaySeconds = seconds;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Djinni__PageRequestDelaySeconds");
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("JobWatcher__Sources__Djinni__ReadVacancyPages=false", failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_fails_when_djinni_reads_pages_with_max_pages_per_cycle_below_1(int pages)
    {
        var options = WithDjinniReadingPages();
        options.Sources.Djinni.MaxPagesPerCycle = pages;

        var result = Validator.Validate(null, options);

        AssertFailedNaming(result, "JobWatcher__Sources__Djinni__MaxPagesPerCycle");
        Assert.Single(result.Failures!);
    }

    [Fact]
    public void Validate_accepts_a_page_delay_of_1_second_and_1_page_per_cycle()
    {
        var options = WithDjinniReadingPages();
        options.Sources.Djinni.PageRequestDelaySeconds = 1;
        options.Sources.Djinni.MaxPagesPerCycle = 1;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Validate_does_not_check_the_page_settings_when_djinni_or_page_reading_is_off(bool djinniEnabled, bool readPages)
    {
        var options = WithDjinniReadingPages();
        options.Sources.Djinni.Enabled = djinniEnabled;
        options.Sources.Djinni.ReadVacancyPages = readPages;
        options.Sources.Djinni.PageRequestDelaySeconds = 0;
        options.Sources.Djinni.MaxPagesPerCycle = 0;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }
}
