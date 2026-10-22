using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Filtering;

namespace JobWatcher.Tests.Filtering;

public class VacancyFilterTests
{
    private static readonly VacancyFilter Filter = new(new ProfileOptions
    {
        Keyword = ".NET",
        Synonyms = ["C#", "ASP.NET", "dotnet"],
        MinYearsExperience = 3,
        Cities = ["Київ", "Kyiv"],
        AllowedRemoteRegions = ["Ukraine", "Europe", "EU", "Worldwide"],
    });

    private static readonly VacancyFilter FilterWithMinSalary = new(new ProfileOptions
    {
        Keyword = ".NET",
        Synonyms = ["C#", "ASP.NET", "dotnet"],
        MinYearsExperience = 3,
        Cities = ["Київ", "Kyiv"],
        AllowedRemoteRegions = ["Ukraine", "Europe", "EU", "Worldwide"],
        MinSalary = new MinSalaryOptions { Amount = 4000, Currency = "USD", Period = SalaryPeriod.Month },
    });

    // Builds a vacancy that passes every criterion; each test changes only the one under test.
    private static Vacancy MakeVacancy(
        string title = ".NET Developer",
        string? description = null,
        string? salary = null,
        WorkMode workMode = WorkMode.Remote,
        string? location = null,
        string? remoteRestrictedTo = null,
        int? minYearsExperience = null,
        bool isJunior = false) => new(
        Source: VacancySource.Dou,
        SourceId: "1",
        Title: title,
        Company: "Acme",
        Url: "https://example.com/vacancies/1",
        Salary: salary,
        Location: location,
        WorkMode: workMode,
        RemoteRestrictedTo: remoteRestrictedTo,
        MinYearsExperience: minYearsExperience,
        IsJunior: isJunior,
        Description: description);

    [Theory]
    [InlineData(".NET Developer")]
    [InlineData("senior .net engineer")]
    public void Matches_title_with_dot_net(string title)
    {
        Assert.True(Filter.Matches(MakeVacancy(title: title)));
    }

    [Fact]
    public void Matches_dot_net_only_in_description()
    {
        var vacancy = MakeVacancy(title: "Backend Developer", description: "Our stack: .NET 8, PostgreSQL");

        Assert.True(Filter.Matches(vacancy));
    }

    [Theory]
    [InlineData("Senior C# Engineer")]
    [InlineData("ASP.NET Core Developer")]
    [InlineData("Dotnet Engineer")]
    public void Matches_title_with_csharp_synonym(string title)
    {
        Assert.True(Filter.Matches(MakeVacancy(title: title)));
    }

    [Fact]
    public void Matches_dot_net8_in_title()
    {
        Assert.True(Filter.Matches(MakeVacancy(title: "Backend Developer (.NET8)")));
    }

    [Theory]
    [InlineData("Our stack is .NET Core and Azure")]
    [InlineData("Stack: .NET, PostgreSQL")]
    [InlineData("Modern .NET.")]
    [InlineData("Built on .net")]
    [InlineData("First occurrence is not a match: jobs.netflix.com, but we use .NET 8")]
    public void Matches_dot_net_followed_by_non_letter_in_description(string description)
    {
        var vacancy = MakeVacancy(title: "Backend Developer", description: description);

        Assert.True(Filter.Matches(vacancy));
    }

    [Theory]
    [InlineData("Write to hr@acme.network")]
    [InlineData("See jobs.netflix.com for details")]
    [InlineData("Visit acme.networkingevents.org")]
    public void Does_not_match_dot_network_in_description(string description)
    {
        var vacancy = MakeVacancy(title: "Backend Developer", description: description);

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void Does_not_match_dot_network_in_title()
    {
        Assert.False(Filter.Matches(MakeVacancy(title: "Engineer at acme.network")));
    }

    [Fact]
    public void Synonym_followed_by_letter_does_not_match()
    {
        var vacancy = MakeVacancy(title: "Backend Developer", description: "Part of the dotnetfoundation community");

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void Rejects_java_vacancy()
    {
        var vacancy = MakeVacancy(title: "Java Developer", description: "Spring Boot, Hibernate, Kafka");

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void Rejects_word_internet_without_dot_net()
    {
        var vacancy = MakeVacancy(title: "Internet Marketing Specialist", description: "Promote products on the internet");

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void MinYears_3_passes()
    {
        Assert.True(Filter.Matches(MakeVacancy(minYearsExperience: 3)));
    }

    [Fact]
    public void MinYears_2_rejected()
    {
        Assert.False(Filter.Matches(MakeVacancy(minYearsExperience: 2)));
    }

    [Fact]
    public void MinYears_null_passes()
    {
        Assert.True(Filter.Matches(MakeVacancy(minYearsExperience: null)));
    }

    [Fact]
    public void IsJunior_rejected()
    {
        Assert.False(Filter.Matches(MakeVacancy(isJunior: true)));
    }

    [Fact]
    public void Remote_unrestricted_passes()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Remote, remoteRestrictedTo: null);

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Remote_restricted_to_US_rejected()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Remote, remoteRestrictedTo: "US");

        Assert.False(Filter.Matches(vacancy));
    }

    [Theory]
    [InlineData("Europe")]
    [InlineData("europe")]
    public void Remote_restricted_to_Europe_passes(string region)
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Remote, remoteRestrictedTo: region);

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Remote_in_other_city_passes()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Remote, location: "Lviv");

        Assert.True(Filter.Matches(vacancy));
    }

    [Theory]
    [InlineData("Kyiv")]
    [InlineData("kyiv")]
    [InlineData("Київ")]
    [InlineData("КИЇВ")]
    [InlineData("Kyiv, Ukraine")]
    public void Office_in_Kyiv_passes(string location)
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Office, location: location);

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Hybrid_in_Kyiv_passes()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Hybrid, location: "Kyiv");

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Office_in_Lviv_rejected()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Office, location: "Lviv");

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void Office_without_location_passes()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Office, location: null);

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Unknown_mode_and_no_location_passes()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Unknown, location: null);

        Assert.True(Filter.Matches(vacancy));
    }

    [Fact]
    public void Unknown_mode_in_Lviv_rejected()
    {
        var vacancy = MakeVacancy(workMode: WorkMode.Unknown, location: "Lviv");

        Assert.False(Filter.Matches(vacancy));
    }

    [Fact]
    public void No_min_salary_configured_ignores_salary()
    {
        Assert.True(Filter.Matches(MakeVacancy(salary: "$100")));
    }

    [Fact]
    public void Salary_null_passes()
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: null)));
    }

    [Fact]
    public void Range_max_above_min_passes()
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: "$3000-5000")));
    }

    [Fact]
    public void Range_max_below_min_rejected()
    {
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(salary: "$2000-3000")));
    }

    [Fact]
    public void Range_max_equal_to_min_passes()
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: "$3000-4000")));
    }

    [Fact]
    public void Open_ended_from_passes()
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: "від $2000")));
    }

    [Theory]
    [InlineData("$3500+")]
    [InlineData("5000+ USD")]
    [InlineData("3500 USD+")]
    public void Open_ended_plus_passes(string salary)
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: salary)));
    }

    [Fact]
    public void Plus_before_a_bonus_does_not_hide_a_low_base_salary()
    {
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(salary: "$3500 + bonus")));
    }

    [Fact]
    public void Up_to_below_min_rejected()
    {
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(salary: "до $3000")));
    }

    [Fact]
    public void Single_amount_below_min_rejected()
    {
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(salary: "$3500")));
    }

    [Theory]
    [InlineData("40 000 грн")]
    [InlineData("€1000-2000")]
    [InlineData("1000-2000")]
    public void Different_currency_passes_without_conversion(string salary)
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: salary)));
    }

    [Fact]
    public void Currency_is_compared_case_insensitively()
    {
        var filter = new VacancyFilter(new ProfileOptions
        {
            Keyword = ".NET",
            MinSalary = new MinSalaryOptions { Amount = 4000, Currency = "usd", Period = SalaryPeriod.Month },
        });

        Assert.False(filter.Matches(MakeVacancy(salary: "$2000-3000")));
    }

    [Fact]
    public void Yearly_is_divided_by_12()
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: "$60k/year")));
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(salary: "$36k/year")));
    }

    [Fact]
    public void Yearly_minimum_is_divided_by_12_too()
    {
        var filter = new VacancyFilter(new ProfileOptions
        {
            Keyword = ".NET",
            MinSalary = new MinSalaryOptions { Amount = 48000, Currency = "USD", Period = SalaryPeriod.Year },
        });

        Assert.True(filter.Matches(MakeVacancy(salary: "$4000-4500 per month")));
        Assert.False(filter.Matches(MakeVacancy(salary: "$3000-3900 per month")));
    }

    [Theory]
    [InlineData("$50/hour")]
    [InlineData("$40-60")]
    [InlineData("competitive")]
    [InlineData("negotiable")]
    [InlineData("")]
    public void Hourly_or_unparseable_passes(string salary)
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: salary)));
    }

    // 1200 a week is about 5200 a month, but the text does not say "month": it must not be read as a low
    // monthly salary.
    [Theory]
    [InlineData("$1200/week")]
    [InlineData("$1200 weekly")]
    [InlineData("$1500 per day")]
    public void Day_or_week_rate_passes_instead_of_being_taken_for_a_low_monthly_salary(string salary)
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: salary)));
    }

    [Theory]
    [InlineData("99999999999999999999999999999999999999999 USD")]
    [InlineData("$99999999999999999999999999k")]
    [InlineData("$3000-99999999999999999999999999999999999999999")]
    [InlineData("٣٠٠٠ USD")]
    public void Unreadable_salary_number_passes_without_throwing(string salary)
    {
        Assert.True(FilterWithMinSalary.Matches(MakeVacancy(salary: salary)));
    }

    [Fact]
    public void Salary_does_not_rescue_a_vacancy_rejected_by_other_rules()
    {
        Assert.False(FilterWithMinSalary.Matches(MakeVacancy(title: "Java Developer", salary: "$9000-10000")));
    }
}
