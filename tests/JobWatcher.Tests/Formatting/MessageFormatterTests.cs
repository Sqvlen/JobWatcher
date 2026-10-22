using System.Globalization;
using JobWatcher.Domain;
using JobWatcher.Formatting;

namespace JobWatcher.Tests.Formatting;

public class MessageFormatterTests
{
    private const string FullLink = "🔗 <a href=\"https://example.com/vacancies/1\">Відкрити вакансію (DOU)</a>";

    // Builds a vacancy with every field filled; each test changes only the one under test.
    private static Vacancy MakeVacancy(
        VacancySource source = VacancySource.Dou,
        string title = "Senior .NET Developer",
        string company = "Acme",
        string url = "https://example.com/vacancies/1",
        string? salary = "$4000–5000",
        string? location = "Київ",
        WorkMode workMode = WorkMode.Remote) => new(
        Source: source,
        SourceId: "1",
        Title: title,
        Company: company,
        Url: url,
        Salary: salary,
        Location: location,
        WorkMode: workMode,
        RemoteRestrictedTo: null,
        MinYearsExperience: null,
        IsJunior: false,
        Description: null);

    private static string[] Lines(string message) => message.Split('\n');

    [Fact]
    public void Includes_all_lines_for_full_vacancy()
    {
        var message = MessageFormatter.Format(
            MakeVacancy(),
            ["5+ years of C#", "SQL"],
            new CompanyRating(4.2, "Glassdoor"));

        var expected = string.Join(
            "\n",
            "🆕 <b>Senior .NET Developer</b>",
            "🏢 Acme · ⭐ 4.2 (Glassdoor)",
            "💰 $4000–5000",
            "📍 Київ · Remote",
            "",
            "📋 Вимоги:",
            "• 5+ years of C#",
            "• SQL",
            "",
            FullLink);

        Assert.Equal(expected, message);
    }

    [Fact]
    public void Omits_rating_when_null_and_has_no_NA()
    {
        var message = MessageFormatter.Format(MakeVacancy(), ["SQL"], null);

        Assert.Equal("🏢 Acme", Lines(message)[1]);
        Assert.DoesNotContain("⭐", message);
        Assert.DoesNotContain("N/A", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shows_ne_vkazano_when_salary_null()
    {
        var message = MessageFormatter.Format(MakeVacancy(salary: null), [], null);

        Assert.Contains("💰 не вказано", Lines(message));
    }

    [Fact]
    public void Omits_requirements_block_when_empty()
    {
        var message = MessageFormatter.Format(MakeVacancy(), [], null);

        Assert.DoesNotContain("Вимоги", message);
        Assert.DoesNotContain("•", message);
        Assert.Equal(
            string.Join("\n", "🆕 <b>Senior .NET Developer</b>", "🏢 Acme", "💰 $4000–5000", "📍 Київ · Remote", "", FullLink),
            message);
    }

    [Fact]
    public void Omits_location_line_when_unknown()
    {
        var message = MessageFormatter.Format(
            MakeVacancy(location: null, workMode: WorkMode.Unknown), [], null);

        Assert.DoesNotContain("📍", message);
    }

    [Theory]
    [InlineData(null, WorkMode.Remote, "📍 Remote")]
    [InlineData("Київ", WorkMode.Remote, "📍 Київ · Remote")]
    [InlineData("Київ", WorkMode.Hybrid, "📍 Київ")]
    [InlineData("Київ", WorkMode.Office, "📍 Київ")]
    [InlineData("Київ", WorkMode.Unknown, "📍 Київ")]
    public void Location_line_combines_location_and_remote_marker(string? location, WorkMode workMode, string expectedLine)
    {
        var message = MessageFormatter.Format(MakeVacancy(location: location, workMode: workMode), [], null);

        Assert.Contains(expectedLine, Lines(message));
    }

    [Fact]
    public void Link_line_has_source_name()
    {
        var message = MessageFormatter.Format(MakeVacancy(source: VacancySource.Djinni), [], null);

        Assert.Equal(
            "🔗 <a href=\"https://example.com/vacancies/1\">Відкрити вакансію (Djinni)</a>",
            Lines(message)[^1]);
    }

    [Fact]
    public void Escapes_html_in_title_company_and_url()
    {
        var vacancy = MakeVacancy(
            title: "C# <Lead> & \"more\"",
            company: "R&D <Labs>",
            url: "https://example.com/?q=\"a\"&b=<1>");

        var message = MessageFormatter.Format(vacancy, ["Know <b> & more"], new CompanyRating(4.2, "A&B"));

        var lines = Lines(message);
        Assert.Equal("🆕 <b>C# &lt;Lead&gt; &amp; \"more\"</b>", lines[0]);
        Assert.Equal("🏢 R&amp;D &lt;Labs&gt; · ⭐ 4.2 (A&amp;B)", lines[1]);
        Assert.Contains("• Know &lt;b&gt; &amp; more", lines);
        Assert.Equal(
            "🔗 <a href=\"https://example.com/?q=&quot;a&quot;&amp;b=&lt;1&gt;\">Відкрити вакансію (DOU)</a>",
            lines[^1]);
    }

    [Fact]
    public void Formats_rating_value_with_one_decimal_regardless_of_current_culture()
    {
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = commaCulture;
        try
        {
            var message = MessageFormatter.Format(MakeVacancy(), [], new CompanyRating(4.2, "Glassdoor"));
            var wholeNumber = MessageFormatter.Format(MakeVacancy(), [], new CompanyRating(4, "Glassdoor"));

            Assert.Contains("🏢 Acme · ⭐ 4.2 (Glassdoor)", Lines(message));
            Assert.Contains("🏢 Acme · ⭐ 4.0 (Glassdoor)", Lines(wholeNumber));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Output_never_exceeds_4096_and_keeps_link_line()
    {
        var vacancy = MakeVacancy(title: new string('T', 5000));
        string[] requirements = [.. Enumerable.Range(1, 4).Select(i => $"Requirement {i} " + new string('r', 800))];

        var message = MessageFormatter.Format(vacancy, requirements, new CompanyRating(4.2, "Glassdoor"));

        Assert.True(message.Length <= MessageFormatter.TelegramLimit, $"Length was {message.Length}");
        var lines = Lines(message);
        Assert.Equal(FullLink, lines[^1]);
        // The title is cut and marked with an ellipsis, and the requirements were dropped before that.
        Assert.StartsWith("🆕 <b>TTT", lines[0]);
        Assert.EndsWith("…</b>", lines[0]);
        Assert.DoesNotContain("•", message);
        Assert.DoesNotContain("Вимоги", message);
    }

    [Fact]
    public void Drops_requirements_from_the_end_before_touching_the_title()
    {
        string[] requirements = [.. Enumerable.Range(1, 4).Select(i => $"Requirement {i} " + new string('r', 1200))];

        var message = MessageFormatter.Format(MakeVacancy(), requirements, null);

        Assert.True(message.Length <= MessageFormatter.TelegramLimit, $"Length was {message.Length}");
        var lines = Lines(message);
        Assert.Equal("🆕 <b>Senior .NET Developer</b>", lines[0]);
        Assert.Equal(FullLink, lines[^1]);
        Assert.Equal(
            requirements[..3].Select(r => "• " + r),
            lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Truncating_the_title_never_cuts_an_html_entity_or_a_surrogate_pair()
    {
        var ampersands = MessageFormatter.Format(MakeVacancy(title: new string('&', 5000)), [], null);
        var emoji = MessageFormatter.Format(MakeVacancy(title: string.Concat(Enumerable.Repeat("😀", 3000))), [], null);

        foreach (var message in new[] { ampersands, emoji })
        {
            Assert.True(message.Length <= MessageFormatter.TelegramLimit, $"Length was {message.Length}");
            Assert.Equal(FullLink, Lines(message)[^1]);
        }

        var ampersandTitle = Lines(ampersands)[0]["🆕 <b>".Length..^"…</b>".Length];
        Assert.Matches("^(&amp;)+$", ampersandTitle);

        var emojiTitle = Lines(emoji)[0]["🆕 <b>".Length..^"…</b>".Length];
        Assert.Matches("^(😀)+$", emojiTitle);
    }

    [Theory]
    [InlineData("R&D <Lab> & Co", "R&amp;D &lt;Lab&gt; &amp; Co")]
    [InlineData("plain text, \"quotes\" stay", "plain text, \"quotes\" stay")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void EscapeHtml_escapes_markup_characters_and_turns_null_into_empty(string? text, string expected)
    {
        Assert.Equal(expected, MessageFormatter.EscapeHtml(text));
    }
}
