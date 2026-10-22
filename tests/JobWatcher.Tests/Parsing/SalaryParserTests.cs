using JobWatcher.Domain;
using JobWatcher.Parsing;

namespace JobWatcher.Tests.Parsing;

public class SalaryParserTests
{
    [Theory]
    [InlineData("$4 000 – $5 500")]
    [InlineData("$4\u00A0000 – $5\u00A0500")]
    [InlineData("$4\u202F000 – $5\u202F500")]
    [InlineData("4000-5500 USD")]
    [InlineData("4 000 — 5 500 usd")]
    [InlineData("4,000 - 5,500 USD")]
    [InlineData("US$4000−5500")]
    public void Range_is_parsed_with_min_max_and_currency(string text)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(4000m, range.Min);
        Assert.Equal(5500m, range.Max);
        Assert.Equal("USD", range.Currency);
        Assert.Equal(SalaryPeriod.Month, range.Period);
    }

    [Fact]
    public void Open_ended_from_has_no_max()
    {
        var range = SalaryParser.Parse("від 3000 грн");

        Assert.NotNull(range);
        Assert.Equal(3000m, range.Min);
        Assert.Null(range.Max);
        Assert.Equal("UAH", range.Currency);
    }

    [Theory]
    [InlineData("$3500+", 3500, "USD")]
    [InlineData("3500+ USD", 3500, "USD")]
    [InlineData("3500 USD+", 3500, "USD")]
    [InlineData("€3 500+", 3500, "EUR")]
    [InlineData("40 000+ грн", 40000, "UAH")]
    [InlineData("$60k+", 60000, "USD")]
    [InlineData("5000+", 5000, null)]
    public void Trailing_plus_makes_the_amount_open_ended(string text, int expectedMin, string? expectedCurrency)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal((decimal)expectedMin, range.Min);
        Assert.Null(range.Max);
        Assert.Equal(expectedCurrency, range.Currency);
    }

    [Theory]
    [InlineData("$3500 + bonus")]
    [InlineData("3500 USD + 10% bonus")]
    public void Spaced_plus_before_a_bonus_does_not_open_the_range(string text)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(3500m, range.Min);
        Assert.Equal(3500m, range.Max);
    }

    [Fact]
    public void English_from_has_no_max()
    {
        var range = SalaryParser.Parse("from $2000");

        Assert.NotNull(range);
        Assert.Equal(2000m, range.Min);
        Assert.Null(range.Max);
        Assert.Equal("USD", range.Currency);
    }

    [Fact]
    public void Up_to_has_no_min()
    {
        var range = SalaryParser.Parse("до $3000");

        Assert.NotNull(range);
        Assert.Null(range.Min);
        Assert.Equal(3000m, range.Max);
    }

    [Fact]
    public void Percentages_are_not_amounts()
    {
        var range = SalaryParser.Parse("до $5000 + 10% бонус");

        Assert.NotNull(range);
        Assert.Null(range.Min);
        Assert.Equal(5000m, range.Max);
    }

    [Fact]
    public void Single_amount_is_both_min_and_max()
    {
        var range = SalaryParser.Parse("40 000 грн");

        Assert.NotNull(range);
        Assert.Equal(40000m, range.Min);
        Assert.Equal(40000m, range.Max);
        Assert.Equal("UAH", range.Currency);
    }

    [Fact]
    public void K_suffix_multiplies_by_thousand_and_year_period_is_recognised()
    {
        var range = SalaryParser.Parse("$60k/year");

        Assert.NotNull(range);
        Assert.Equal(60000m, range.Min);
        Assert.Equal(60000m, range.Max);
        Assert.Equal("USD", range.Currency);
        Assert.Equal(SalaryPeriod.Year, range.Period);
    }

    [Fact]
    public void Decimal_k_suffix_is_supported()
    {
        var range = SalaryParser.Parse("$4.5k - $5.5k");

        Assert.NotNull(range);
        Assert.Equal(4500m, range.Min);
        Assert.Equal(5500m, range.Max);
    }

    [Fact]
    public void Ukrainian_month_period_and_uah_code_are_recognised()
    {
        var range = SalaryParser.Parse("100 000 – 150 000 UAH на місяць");

        Assert.NotNull(range);
        Assert.Equal(100000m, range.Min);
        Assert.Equal(150000m, range.Max);
        Assert.Equal("UAH", range.Currency);
        Assert.Equal(SalaryPeriod.Month, range.Period);
    }

    [Theory]
    [InlineData("$50/hour")]
    [InlineData("40-60 USD per hour")]
    [InlineData("500 грн/год")]
    public void Hourly_period_is_recognised(string text)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(SalaryPeriod.Hour, range.Period);
    }

    [Theory]
    [InlineData("$4000 per month", SalaryPeriod.Month)]
    [InlineData("€3 500 / mo", SalaryPeriod.Month)]
    [InlineData("48 000 € annually", SalaryPeriod.Year)]
    [InlineData("£50 000 per year", SalaryPeriod.Year)]
    [InlineData("720 000 грн на рік", SalaryPeriod.Year)]
    public void Explicit_period_is_recognised(string text, SalaryPeriod expected)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(expected, range.Period);
    }

    [Fact]
    public void Small_amount_without_period_is_unknown_because_it_may_be_an_hourly_rate()
    {
        var range = SalaryParser.Parse("$50-70");

        Assert.NotNull(range);
        Assert.Equal(SalaryPeriod.Unknown, range.Period);
    }

    // A day or week rate is neither hourly nor monthly: without these words an amount of 1000 or more
    // would be taken for a monthly salary.
    [Theory]
    [InlineData("$1200/week")]
    [InlineData("$1200 / week")]
    [InlineData("$1200 per week")]
    [InlineData("$1200 weekly")]
    [InlineData("$2500 biweekly")]
    [InlineData("$1200 a week")]
    [InlineData("1500 грн/день")]
    [InlineData("1500 грн на день")]
    [InlineData("1500 грн в день")]
    [InlineData("1500 грн щодня")]
    [InlineData("6000 грн на тиждень")]
    [InlineData("6000 грн/тиждень")]
    [InlineData("6000 грн в тиждень")]
    [InlineData("$300 per day")]
    [InlineData("$1100 daily")]
    [InlineData("$1100 a day")]
    [InlineData("$1100/day")]
    [InlineData("$1100 PER DAY")]
    public void Day_and_week_rates_have_an_unknown_period(string text)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(SalaryPeriod.Unknown, range.Period);
    }

    [Theory]
    [InlineData("$4000 per month, paid weekly", SalaryPeriod.Month)]
    [InlineData("$4000/month ($200 per day)", SalaryPeriod.Month)]
    [InlineData("$48k per year", SalaryPeriod.Year)]
    [InlineData("$50/hour, up to 8 hours a day", SalaryPeriod.Hour)]
    [InlineData("$3000 holiday bonus", SalaryPeriod.Month)]
    [InlineData("$3000 plus a weekend shift", SalaryPeriod.Month)]
    [InlineData("$3000 a daytime role", SalaryPeriod.Month)]
    [InlineData("3000 грн у денну зміну", SalaryPeriod.Month)]
    public void Day_and_week_words_do_not_override_an_explicit_period_or_match_inside_other_words(string text, SalaryPeriod expected)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(expected, range.Period);
    }

    // Salary strings as DOU puts them into the feed titles (docs/sources/dou.md): the two forms of the
    // fixture and the open-ended ones the source documents. All of them are readable.
    [Theory]
    [InlineData("$2500–4000", 2500, 4000, "USD", SalaryPeriod.Month)]
    [InlineData("$700", 700, 700, "USD", SalaryPeriod.Unknown)]
    [InlineData("до $3500", null, 3500, "USD", SalaryPeriod.Month)]
    [InlineData("від $1200", 1200, null, "USD", SalaryPeriod.Month)]
    public void Salary_forms_of_the_DOU_feed_are_read(string text, int? min, int? max, string currency, SalaryPeriod period)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal((decimal?)min, range.Min);
        Assert.Equal((decimal?)max, range.Max);
        Assert.Equal(currency, range.Currency);
        Assert.Equal(period, range.Period);
    }

    // A salary that cannot be read as a number is the same as no salary: it must never throw, because the
    // filter calls the parser on third-party text in the middle of a cycle.
    [Theory]
    [InlineData("99999999999999999999999999999999999999999 USD")]
    [InlineData("$99999999999999999999999999k")]
    [InlineData("$3000-99999999999999999999999999999999999999999")]
    [InlineData("$99 999 999 999 999 999 999 999 999 999 999 999")]
    [InlineData("٣٠٠٠ USD")]
    [InlineData("３０００ USD")]
    public void Unreadable_number_gives_no_salary_instead_of_throwing(string text)
    {
        Assert.Null(SalaryParser.Parse(text));
    }

    [Theory]
    [InlineData("$100", "USD")]
    [InlineData("100 USD", "USD")]
    [InlineData("100 dollars", "USD")]
    [InlineData("€100", "EUR")]
    [InlineData("100 EUR", "EUR")]
    [InlineData("100 євро", "EUR")]
    [InlineData("£100", "GBP")]
    [InlineData("100 GBP", "GBP")]
    [InlineData("100 грн", "UAH")]
    [InlineData("100 ₴", "UAH")]
    [InlineData("100 UAH", "UAH")]
    [InlineData("100 PLN", "PLN")]
    [InlineData("100 zł", "PLN")]
    public void Currency_is_recognised_by_symbol_or_word(string text, string expected)
    {
        var range = SalaryParser.Parse(text);

        Assert.NotNull(range);
        Assert.Equal(expected, range.Currency);
    }

    [Fact]
    public void Unrecognised_currency_is_null()
    {
        var range = SalaryParser.Parse("3000-4000");

        Assert.NotNull(range);
        Assert.Null(range.Currency);
    }

    [Theory]
    [InlineData("competitive")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Text_without_amounts_returns_null(string? text)
    {
        Assert.Null(SalaryParser.Parse(text));
    }
}
