using JobWatcher.Parsing;

namespace JobWatcher.Tests.Parsing;

public class ExperienceParserTests
{
    [Theory]
    [InlineData("3+ years", 3)]
    [InlineData("3-5 years", 3)]
    [InlineData("3 to 5 years", 3)]
    [InlineData("від 3 років", 3)]
    [InlineData("від 3-х років", 3)]
    [InlineData("від 3 до 5 років", 3)]
    [InlineData("2 роки", 2)]
    [InlineData("1 рік", 1)]
    [InlineData("5+ years of C#, 2 years of Azure", 5)]
    [InlineData("Requirements: 4 years commercial experience with .NET", 4)]
    public void ParseMinYears_returns_the_largest_stated_minimum(string text, int expected)
    {
        Assert.Equal(expected, ExperienceParser.ParseMinYears(text));
    }

    [Theory]
    [InlineData("no years mentioned")]
    [InlineData("Strong knowledge of C# and SQL")]
    [InlineData("We are 5 people and have worked 3 days a week")]
    [InlineData("1.5 years")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseMinYears_returns_null_when_no_experience_is_stated(string? text)
    {
        Assert.Null(ExperienceParser.ParseMinYears(text));
    }

    // \d also matches the digits of other scripts (Arabic-Indic U+0660.., fullwidth U+FF10..), which int.Parse
    // refuses. The text is third-party input: one hostile description must never make a source's fetch fail.
    // Such digits are not read as a number of years: the result is null, never an exception.
    [Theory]
    [InlineData("\u0663+ years")]
    [InlineData("\u0663-\u0665 years")]
    [InlineData("від \u0663 років")]
    [InlineData("\u0663\u0660 years")]
    [InlineData("1\u0663 years")]
    [InlineData("\uFF13+ years")]
    [InlineData("\uFF13-\uFF15 years")]
    [InlineData("\uFF11\uFF12 years")]
    public void ParseMinYears_ignores_digits_of_other_scripts_without_throwing(string text)
    {
        Assert.Null(ExperienceParser.ParseMinYears(text));
    }

    [Fact]
    public void ParseMinYears_still_reads_ascii_numbers_around_digits_of_other_scripts()
    {
        Assert.Equal(5, ExperienceParser.ParseMinYears("\u0663+ years of Java, 5+ years of C#, \uFF13 years of SQL"));
        Assert.Equal(3, ExperienceParser.ParseMinYears("3-\u0665 years"));
    }
}
