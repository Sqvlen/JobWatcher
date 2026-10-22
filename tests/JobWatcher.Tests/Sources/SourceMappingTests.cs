using JobWatcher.Sources;

namespace JobWatcher.Tests.Sources;

// The rules every source adapter shares, so that the same text means the same thing whichever board it came from.
public sealed class SourceMappingTests
{
    [Theory]
    [InlineData("Ukraine", "Ukraine")]
    [InlineData("Україна", "Ukraine")]
    [InlineData("Europe", "Europe")]
    [InlineData("Європа", "Europe")]
    [InlineData("EU", "EU")]
    [InlineData("European Union", "EU")]
    [InlineData("ЄС", "EU")]
    [InlineData("US", "US")]
    [InlineData("USA", "US")]
    [InlineData("United States", "US")]
    [InlineData("США", "US")]
    [InlineData("UK", "UK")]
    [InlineData("United Kingdom", "UK")]
    public void NormalizeRegion_gives_the_exact_region_string_of_every_alias(string text, string expected)
    {
        Assert.Equal(expected, SourceMapping.NormalizeRegion(text));
    }

    [Theory]
    [InlineData("europe", "Europe")]
    [InlineData("EUROPE", "Europe")]
    [InlineData("usa", "US")]
    [InlineData("united kingdom", "UK")]
    [InlineData("україна", "Ukraine")]
    [InlineData("  EU  ", "EU")]
    [InlineData("\tEurope\n", "Europe")]
    public void NormalizeRegion_ignores_case_and_surrounding_whitespace(string text, string expected)
    {
        Assert.Equal(expected, SourceMapping.NormalizeRegion(text));
    }

    [Theory]
    [InlineData("EU only", "EU")]
    [InlineData("US only", "US")]
    [InlineData("USA ONLY", "US")]
    [InlineData("  UK   Only  ", "UK")]
    [InlineData("Україна лише", "Ukraine")]
    [InlineData("Європа тільки", "Europe")]
    [InlineData("ЄС ЛИШЕ", "EU")]
    public void NormalizeRegion_ignores_a_trailing_only(string text, string expected)
    {
        Assert.Equal(expected, SourceMapping.NormalizeRegion(text));
    }

    [Theory]
    [InlineData("Poland")]
    [InlineData("Worldwide")]
    [InlineData("Poland only")]
    [InlineData("only")]
    [InlineData("EUonly")]
    [InlineData("EU only only")]
    [InlineData("Ukraine (remote)")]
    [InlineData("Europe, Ukraine")]
    [InlineData("European")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeRegion_is_null_for_unknown_or_ambiguous_text(string? text)
    {
        Assert.Null(SourceMapping.NormalizeRegion(text));
    }

    [Theory]
    [InlineData("Junior .NET Developer", true)]
    [InlineData("JUNIOR C# Engineer", true)]
    [InlineData("junior developer", true)]
    [InlineData("Junior+ / Middle .NET Developer", true)]
    [InlineData("<b>Junior</b> C# Developer", true)]
    [InlineData("Jr. C# Developer", true)]
    [InlineData("Jr C# Developer", true)]
    [InlineData("Trainee Backend Developer", true)]
    [InlineData("C# Intern", true)]
    [InlineData("C# Internship Programme", true)]
    [InlineData("Джуніор .NET розробник", true)]
    [InlineData("Джун C#", true)]
    [InlineData("Стажер C#", true)]
    [InlineData("Стажист .NET", true)]
    [InlineData("Стажування C#", true)]
    [InlineData("Senior .NET Developer", false)]
    [InlineData("Middle .NET Developer", false)]
    [InlineData("Internal Tools .NET Developer", false)]
    [InlineData("International Developer", false)]
    [InlineData("Juniper Networks Integration Engineer", false)]
    [InlineData("Jrebel Plugin Developer", false)]
    [InlineData("Джунглі Games Developer", false)]
    [InlineData("", false)]
    public void IsJuniorTitle_only_for_whole_junior_words(string title, bool expected)
    {
        Assert.Equal(expected, SourceMapping.IsJuniorTitle(title));
    }

    [Fact]
    public void MinYearsExperience_reads_the_years_from_the_requirement_items()
    {
        const string description = "<p><strong>Requirements:</strong></p><ul><li>3+ years of C#</li><li>5 years of SQL</li></ul>";

        Assert.Equal(5, SourceMapping.MinYearsExperience("Dev", description));
    }

    [Fact]
    public void MinYearsExperience_reads_the_years_from_the_title()
    {
        Assert.Equal(6, SourceMapping.MinYearsExperience("Senior .NET Developer (6+ years)", null));
        Assert.Equal(6, SourceMapping.MinYearsExperience("Senior .NET Developer (6+ years)", "<ul><li>3+ years of C#</li></ul>"));
    }

    [Fact]
    public void MinYearsExperience_ignores_years_outside_the_requirement_items()
    {
        const string description = "<p>We are a 2 year old startup with a 10 years long roadmap.</p>"
            + "<p><strong>Responsibilities:</strong></p><ul><li>Build things for 7 years of customers</li></ul>"
            + "<p><strong>Requirements:</strong></p><ul><li>C# and SQL</li></ul>"
            + "<p><strong>We offer:</strong></p><ul><li>15 years of stability</li></ul>";

        Assert.Null(SourceMapping.MinYearsExperience("Dev", description));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MinYearsExperience_is_null_without_a_description_and_without_years_in_the_title(string? description)
    {
        Assert.Null(SourceMapping.MinYearsExperience("Dev", description));
    }

    // The country codes and names a board's structured data uses (Djinni's JSON-LD: "UA", "UKR", "Україна").
    [Theory]
    [InlineData("UA", "Ukraine")]
    [InlineData("UKR", "Ukraine")]
    [InlineData("Ukraine", "Ukraine")]
    [InlineData("Україна", "Ukraine")]
    [InlineData("US", "US")]
    [InlineData("USA", "US")]
    [InlineData("United States", "US")]
    [InlineData("США", "US")]
    [InlineData("GB", "UK")]
    [InlineData("GBR", "UK")]
    [InlineData("UK", "UK")]
    [InlineData("United Kingdom", "UK")]
    [InlineData("ua", "Ukraine")]
    [InlineData("  UKR ", "Ukraine")]
    public void NormalizeCountry_gives_the_region_string_of_every_known_country(string text, string expected)
    {
        Assert.Equal(expected, SourceMapping.NormalizeCountry(text));
    }

    [Theory]
    [InlineData("PL")]
    [InlineData("Poland")]
    [InlineData("Europe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeCountry_is_null_for_an_unknown_country_or_a_region(string? text)
    {
        Assert.Null(SourceMapping.NormalizeCountry(text));
    }
}
