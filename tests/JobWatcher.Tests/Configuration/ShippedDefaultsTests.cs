using JobWatcher.Configuration;
using Microsoft.Extensions.Configuration;

namespace JobWatcher.Tests.Configuration;

public class ShippedDefaultsTests
{
    // The real src/JobWatcher/appsettings.json, linked into the test output by the test project.
    private const string ShippedSettingsFile = "shipped-appsettings.json";

    [Fact]
    public void Shipped_appsettings_bind_to_the_documented_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ShippedSettingsFile, optional: false)
            .Build();

        var options = configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>();

        Assert.NotNull(options);
        Assert.Equal(15, options.PollIntervalMinutes);
        Assert.Equal(3, options.FailureAlertThreshold);
        Assert.Equal("./data", options.DataDir);
        Assert.Null(options.MaxMessagesPerCycle);
        Assert.Null(options.Profile.MinSalary);
        Assert.Equal(".NET", options.Profile.Keyword);
        Assert.Equal(["C#", "ASP.NET", "dotnet"], options.Profile.Synonyms);
        Assert.Equal(3, options.Profile.MinYearsExperience);
        Assert.Equal(["Київ", "Kyiv"], options.Profile.Cities);
        Assert.Equal(["Ukraine", "Europe", "EU", "Worldwide"], options.Profile.AllowedRemoteRegions);
        Assert.True(options.Sources.Dou.Enabled);
        Assert.Equal("https://jobs.dou.ua/vacancies/feeds/?category=.NET", options.Sources.Dou.FeedUrl);
        Assert.False(options.Sources.Jooble.Enabled);
        Assert.Equal("https://jooble.org/api", options.Sources.Jooble.BaseUrl);
        Assert.Equal(720, options.Sources.Jooble.MinFetchIntervalMinutes);
        Assert.True(options.Sources.Djinni.Enabled);
        Assert.Equal("https://djinni.co/jobs/rss/?primary_keyword=.NET", options.Sources.Djinni.FeedUrl);
        Assert.True(options.Sources.Djinni.ReadVacancyPages);
        Assert.Equal(2, options.Sources.Djinni.PageRequestDelaySeconds);
        Assert.Equal(15, options.Sources.Djinni.MaxPagesPerCycle);
        // Written out in the file, not only the class defaults: the user sees and can change them there.
        Assert.Equal("true", configuration["JobWatcher:Sources:Djinni:ReadVacancyPages"], ignoreCase: true);
        Assert.Equal("2", configuration["JobWatcher:Sources:Djinni:PageRequestDelaySeconds"]);
        Assert.Equal("15", configuration["JobWatcher:Sources:Djinni:MaxPagesPerCycle"]);
        Assert.False(options.Sources.LinkedIn.Enabled);
        Assert.False(options.Sources.JobsPipe.Enabled);
        Assert.Equal("https://api.jobspipe.dev", options.Sources.JobsPipe.BaseUrl);
        // Once a day: the free plan's request quota is not documented, and what a search costs does not depend on how often it runs.
        Assert.Equal(1440, options.Sources.JobsPipe.MinFetchIntervalMinutes);
        Assert.Equal(14, options.Sources.JobsPipe.MaxAgeDays);
        Assert.Equal(25, options.Sources.JobsPipe.PageSize);
        // The excluded boards are listed in the file, not in the class: a configured list would otherwise be appended
        // to a pre-filled array, and the user could never remove LinkedIn from it. Workday, Greenhouse and Ashby are
        // left out at the customer's request (2026-10-23); these are JobsPipe's own spellings of the names.
        Assert.Equal(["linkedin", "workday", "greenhouse", "ashby"], options.Sources.JobsPipe.ExcludedSources);
        // Eleks needs no key, so it is on; its list is .NET (technology 21,27) in Ukraine or remote (location 206,98).
        Assert.True(options.Sources.Eleks.Enabled);
        Assert.Equal("https://careers.eleks.com/vacancies/?technology=21,27&location=206,98", options.Sources.Eleks.ListUrl);
        Assert.True(options.Sources.Eleks.ReadVacancyPages);
        Assert.Equal(2, options.Sources.Eleks.PageRequestDelaySeconds);
        Assert.Equal(15, options.Sources.Eleks.MaxPagesPerCycle);
        // The day between two fetches is written out in the file: the user sees it, and it is the knob to change.
        Assert.Equal("1440", configuration["JobWatcher:Sources:Eleks:MinFetchIntervalMinutes"]);
    }

    // Characterisation of how lists merge, because the customer's own Local file lists only "linkedin": a list from a
    // later source replaces the shipped one index by index and does not cut it short, so the three added names stay.
    [Fact]
    public void A_shorter_excluded_list_from_a_later_source_overlays_the_shipped_one_by_index()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ShippedSettingsFile, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JobWatcher:Sources:JobsPipe:ExcludedSources:0"] = "linkedin",
            })
            .Build();

        var options = configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>();

        Assert.Equal(["linkedin", "workday", "greenhouse", "ashby"], options!.Sources.JobsPipe.ExcludedSources);
    }

    [Fact]
    public void Shipped_appsettings_have_azure_key_vault_switched_off()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ShippedSettingsFile, optional: false)
            .Build();

        // Written out, empty, at the top level: the user sees the setting and Key Vault stays off until it is set.
        Assert.Contains(configuration.GetSection(KeyVaultSettings.SectionName).GetChildren(), child => child.Key == "VaultUri");
        Assert.Equal("", configuration["KeyVault:VaultUri"]);
    }

    [Fact]
    public void Configured_list_replaces_the_empty_class_default_instead_of_extending_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WatcherOptions.SectionName}:Profile:Cities:0"] = "Lviv",
            })
            .Build();
        var options = new WatcherOptions();

        configuration.GetSection(WatcherOptions.SectionName).Bind(options);

        Assert.Equal(["Lviv"], options.Profile.Cities);
    }
}
