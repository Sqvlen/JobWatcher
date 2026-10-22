using JobWatcher.Parsing;

namespace JobWatcher.Tests.Parsing;

public class RequirementsExtractorTests
{
    [Fact]
    public void English_heading_Requirements_returns_bullets()
    {
        const string html = """
            <p>We build payment software.</p>
            <h3>Requirements</h3>
            <ul>
              <li>3+ years of C#</li>
              <li>Experience with PostgreSQL</li>
            </ul>
            """;

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["3+ years of C#", "Experience with PostgreSQL"], items);
    }

    [Fact]
    public void Ukrainian_heading_Вимоги_returns_bullets()
    {
        const string html = "<h4>Вимоги:</h4><ul><li>Досвід з .NET від 3 років</li><li>Знання SQL</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["Досвід з .NET від 3 років", "Знання SQL"], items);
    }

    [Theory]
    [InlineData("<p><strong>Вимоги:</strong></p><ul><li>Знання C#</li></ul>")]
    [InlineData("<p><b>Вимоги</b>:</p><ul><li>Знання C#</li></ul>")]
    [InlineData("<div><b>Вимоги</b></div><ul><li>Знання C#</li></ul>")]
    public void Heading_in_bold_paragraph_is_recognised(string html)
    {
        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["Знання C#"], items);
    }

    [Fact]
    public void More_than_4_bullets_returns_first_4()
    {
        const string html = "<h3>Requirements</h3><ul><li>One</li><li>Two</li><li>Three</li><li>Four</li><li>Five</li><li>Six</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(RequirementsExtractor.MaxItems, items.Count);
        Assert.Equal(["One", "Two", "Three", "Four"], items);
    }

    [Fact]
    public void Bullet_over_160_chars_is_truncated_with_ellipsis()
    {
        var longItem = string.Join(' ', Enumerable.Repeat("verylongword", 20));
        var html = $"<h3>Requirements</h3><ul><li>{longItem}</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        var item = Assert.Single(items);
        Assert.True(item.Length <= RequirementsExtractor.MaxItemLength);
        Assert.EndsWith("…", item);
        Assert.StartsWith("verylongword verylongword", item);
    }

    [Fact]
    public void Bullet_of_exactly_160_chars_is_kept_unchanged()
    {
        var item = new string('a', RequirementsExtractor.MaxItemLength);
        var html = $"<h3>Requirements</h3><ul><li>{item}</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal([item], items);
    }

    [Fact]
    public void HTML_tags_and_entities_are_stripped()
    {
        const string html = "<h3>Requirements</h3><ul><li>Experience with <b>C#</b> &amp; .NET&nbsp;8 &mdash; &quot;real&quot; projects</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["Experience with C# & .NET 8 — \"real\" projects"], items);
    }

    [Fact]
    public void Script_and_style_content_is_ignored()
    {
        const string html = "<style>li { color: red }</style><h3>Requirements</h3><ul><li>C#<script>alert(1)</script></li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C#"], items);
    }

    [Fact]
    public void No_requirements_section_returns_empty()
    {
        const string html = "<h3>Responsibilities</h3><ul><li>Write code</li></ul><h3>We offer</h3><ul><li>Salary</li></ul>";

        Assert.Empty(RequirementsExtractor.Extract(html));
    }

    [Fact]
    public void Bullets_without_any_heading_are_not_requirements()
    {
        Assert.Empty(RequirementsExtractor.Extract("<ul><li>Free lunch</li><li>Gym</li></ul>"));
    }

    [Fact]
    public void Stops_at_next_heading()
    {
        const string html = """
            <h3>Вимоги</h3>
            <ul><li>Знання C#</li><li>Досвід з SQL</li></ul>
            <h3>Ми пропонуємо</h3>
            <ul><li>Гнучкий графік</li><li>Медичне страхування</li></ul>
            """;

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["Знання C#", "Досвід з SQL"], items);
    }

    [Fact]
    public void Stops_at_next_bold_paragraph_heading()
    {
        const string html = """
            <p><b>Requirements</b></p>
            <ul><li>C#</li></ul>
            <p><b>About the project</b></p>
            <ul><li>Fintech product</li></ul>
            """;

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C#"], items);
    }

    [Fact]
    public void Plain_text_with_dash_bullets_is_supported()
    {
        const string text = "About us\nWe are great.\n\nВимоги:\n- Знання C#\n• Досвід з SQL\n* Англійська B2\n\nМи пропонуємо:\n- Гнучкий графік";

        var items = RequirementsExtractor.Extract(text);

        Assert.Equal(["Знання C#", "Досвід з SQL", "Англійська B2"], items);
    }

    [Fact]
    public void Bullets_separated_by_line_breaks_inside_a_paragraph_are_split()
    {
        const string html = "<p><b>Requirements:</b><br>- C# 10+<br>- SQL<br></p><p>Apply now!</p>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C# 10+", "SQL"], items);
    }

    [Fact]
    public void List_item_with_nested_paragraph_is_one_item()
    {
        const string html = "<h3>Requirements</h3><ul><li><p>Strong knowledge</p><p>of C#</p></li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["Strong knowledge of C#"], items);
    }

    [Fact]
    public void Intro_sentence_under_heading_does_not_end_the_section()
    {
        const string html = "<h3>Requirements</h3><p>You should have:</p><ul><li>C#</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C#"], items);
    }

    [Theory]
    [InlineData("Qualifications")]
    [InlineData("Must have")]
    [InlineData("What we expect from you")]
    [InlineData("Ми очікуємо")]
    [InlineData("Очікуємо від тебе")]
    [InlineData("Що потрібно")]
    public void Other_requirement_headings_are_recognised(string heading)
    {
        var items = RequirementsExtractor.Extract($"<h3>{heading}</h3><ul><li>C#</li></ul>");

        Assert.Equal(["C#"], items);
    }

    // The three wordings of the real DOU fixture that the first list of headings missed.
    [Fact]
    public void Heading_What_you_need_is_recognised()
    {
        const string html = "<h3>What you do</h3><ul><li>Write code</li></ul><h3>What you need</h3><ul><li>3+ years of C#</li><li>SQL</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["3+ years of C#", "SQL"], items);
    }

    [Theory]
    [InlineData("<p><br><strong>What You'll Bring</strong></p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p><br><strong>What You’ll Bring</strong></p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<h3>What You’ll Bring</h3><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p>What You’ll Bring:</p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<h3>What you will bring</h3><ul><li>5 years of C#</li></ul>")]
    public void Heading_What_Youll_Bring_is_recognised_with_either_apostrophe(string html)
    {
        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["5 years of C#"], items);
    }

    [Theory]
    [InlineData("<h3>Required Experience and Skills:</h3><ul><li>3+ years of React</li></ul>")]
    [InlineData("<h3>Required Experience and Skills</h3><ul><li>3+ years of React</li></ul>")]
    [InlineData("<p><strong>Required Experience and Skills:</strong></p><ul><li>3+ years of React</li></ul>")]
    [InlineData("<p>Required Experience and Skills:</p><ul><li>3+ years of React</li></ul>")]
    public void Heading_Required_Experience_and_Skills_is_recognised(string html)
    {
        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["3+ years of React"], items);
    }

    [Theory]
    [InlineData("<h3>What we’re looking for</h3><ul><li>5 years of C#</li></ul>")]
    [InlineData("<h3>What we're looking for</h3><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p><strong>What we are looking for</strong></p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p>Who we are looking for:</p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p>What we’re looking for</p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p>What we are looking for</p><ul><li>5 years of C#</li></ul>")]
    [InlineData("<h3>What we need from you</h3><ul><li>5 years of C#</li></ul>")]
    [InlineData("<p>What we need from you:</p><ul><li>5 years of C#</li></ul>")]
    public void Headings_looking_for_and_what_we_need_from_you_are_recognised(string html)
    {
        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["5 years of C#"], items);
    }

    [Theory]
    [InlineData("What we offer")]
    [InlineData("Ми пропонуємо")]
    public void Section_after_a_looking_for_heading_ends_at_a_benefits_heading(string offerHeading)
    {
        var html = $"<h3>What we’re looking for</h3><ul><li>C#</li></ul><h3>{offerHeading}</h3><ul><li>Free lunch</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C#"], items);
    }

    [Fact]
    public void Bare_необхідно_is_not_a_requirements_heading()
    {
        Assert.Empty(RequirementsExtractor.Extract("<h3>Необхідно</h3><ul><li>Free lunch</li></ul>"));
    }

    [Theory]
    [InlineData("What we offer")]
    [InlineData("What you’ll get")]
    [InlineData("What You’ll Do")]
    [InlineData("What you will do")]
    [InlineData("Nice to have")]
    [InlineData("Would Be a Plus:")]
    public void Benefit_and_task_headings_do_not_start_a_requirements_section(string heading)
    {
        var items = RequirementsExtractor.Extract($"<h3>{heading}</h3><ul><li>Free lunch</li></ul>");

        Assert.Empty(items);
    }

    [Fact]
    public void Section_after_a_new_requirements_heading_ends_at_the_offer_heading()
    {
        const string html = "<h3>What you need</h3><ul><li>C#</li></ul><h3>What we offer</h3><ul><li>Free lunch</li></ul>";

        var items = RequirementsExtractor.Extract(html);

        Assert.Equal(["C#"], items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_input_returns_empty(string? input)
    {
        Assert.Empty(RequirementsExtractor.Extract(input));
    }
}
