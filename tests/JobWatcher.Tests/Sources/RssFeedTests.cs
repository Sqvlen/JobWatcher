using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JobWatcher.Domain;
using JobWatcher.Sources;

namespace JobWatcher.Tests.Sources;

// The reading every RSS-based adapter shares (DOU, Djinni): the request, the safe parse, and the failures that
// must stay visible. The adapters' own tests cover the same paths through each adapter.
public sealed class RssFeedTests
{
    private const string FeedUrl = "https://example.com/feed/?q=1";
    private const string UserAgent = "JobWatcher/1.0 (test)";
    private const string SourceName = "Acme";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri, request.Headers.UserAgent.ToString()));

            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Ok(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };

    private static string Feed(params XElement[] items) =>
        new XElement("rss", new XAttribute("version", "2.0"), new XElement("channel", new XElement("title", "Test"), items)).ToString();

    private static XElement Item(string? title) =>
        new("item", title is null ? null : new XElement("title", title));

    // Maps an item to a vacancy when it has a title; the title doubles as the id.
    private static Vacancy? MapTitle(XElement item) =>
        RssFeed.TextOf(item, "title") is { } title
            ? new Vacancy(VacancySource.Dou, title, title, "Acme Ltd", "https://example.com/" + title, null, null, WorkMode.Unknown, null, null, false, null)
            : null;

    private static Task<IReadOnlyList<Vacancy>> FetchAsync(
        Func<HttpResponseMessage> respond,
        Func<XElement, Vacancy?>? map = null,
        CancellationToken ct = default) =>
        RssFeed.FetchAsync(new HttpClient(new StubHandler(respond)), FeedUrl, UserAgent, SourceName, map ?? MapTitle, ct);

    [Fact]
    public async Task Maps_every_item_in_the_order_of_the_feed()
    {
        var vacancies = await FetchAsync(() => Ok(Feed(Item("first"), Item("second"), Item("third"))));

        Assert.Equal(["first", "second", "third"], vacancies.Select(v => v.SourceId));
    }

    [Fact]
    public async Task Makes_one_get_request_for_the_given_url_with_the_given_user_agent()
    {
        var handler = new StubHandler(() => Ok(Feed()));

        await RssFeed.FetchAsync(new HttpClient(handler), FeedUrl, UserAgent, SourceName, MapTitle, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(FeedUrl, request.Uri!.ToString());
        Assert.Equal(UserAgent, request.UserAgent);
    }

    [Fact]
    public async Task Items_the_mapper_rejects_are_skipped_while_the_others_are_kept()
    {
        var vacancies = await FetchAsync(() => Ok(Feed(Item(null), Item("kept"), Item(null))));

        Assert.Equal("kept", Assert.Single(vacancies).SourceId);
    }

    [Fact]
    public async Task Valid_empty_feed_returns_empty_list_without_calling_the_mapper()
    {
        var calls = 0;

        var vacancies = await FetchAsync(
            () => Ok(Feed()),
            item =>
            {
                calls++;

                return MapTitle(item);
            });

        Assert.Empty(vacancies);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Items_present_but_none_readable_throws_naming_the_source_and_the_count()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchAsync(() => Ok(Feed(Item(null), Item(null), Item(null)))));

        Assert.Equal(
            "None of the 3 items of the Acme feed could be read; the feed format has probably changed.",
            exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HTTP_error_throws(HttpStatusCode status)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(() => new HttpResponseMessage(status)));
    }

    [Fact]
    public async Task Invalid_xml_throws()
    {
        await Assert.ThrowsAnyAsync<XmlException>(
            () => FetchAsync(() => Ok("<rss version=\"2.0\"><channel><item><title>x")));
    }

    [Fact]
    public async Task Document_type_declaration_is_refused()
    {
        const string withDtd = "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x \"boom\">]><rss version=\"2.0\"><channel/></rss>";

        await Assert.ThrowsAnyAsync<XmlException>(() => FetchAsync(() => Ok(withDtd)));
    }

    [Fact]
    public async Task An_html_page_with_a_doctype_instead_of_the_feed_throws()
    {
        const string page = "<!DOCTYPE html><html><body>Checking your browser</body></html>";

        await Assert.ThrowsAnyAsync<XmlException>(() => FetchAsync(() => Ok(page)));
    }

    [Fact]
    public async Task Well_formed_xml_that_is_not_rss_throws_naming_the_source()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchAsync(() => Ok("<html><body>Just a moment...</body></html>")));

        Assert.Equal("The Acme response is not an RSS feed.", exception.Message);
    }

    [Fact]
    public async Task Rss_without_a_channel_throws_naming_the_source()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(() => Ok("<rss version=\"2.0\"/>")));

        Assert.Equal("The Acme response is not an RSS feed.", exception.Message);
    }

    [Fact]
    public async Task A_cancelled_request_throws_instead_of_returning_an_empty_list()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FetchAsync(() => Ok(Feed()), ct: cancelled.Token));
    }

    [Fact]
    public void TextOf_is_the_trimmed_text_of_the_child_element()
    {
        var item = new XElement("item", new XElement("title", "  Dev \n"));

        Assert.Equal("Dev", RssFeed.TextOf(item, "title"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void TextOf_is_null_for_a_blank_element(string text)
    {
        var item = new XElement("item", new XElement("title", text));

        Assert.Null(RssFeed.TextOf(item, "title"));
    }

    [Fact]
    public void TextOf_is_null_for_a_missing_element()
    {
        var item = new XElement("item", new XElement("link", "https://example.com/"));

        Assert.Null(RssFeed.TextOf(item, "title"));
    }

    [Fact]
    public void DescriptionOf_is_the_html_of_the_element_unchanged()
    {
        var item = new XElement("item", new XElement("description", " <p>Hello &amp; <b>bye</b></p>\n"));

        Assert.Equal(" <p>Hello &amp; <b>bye</b></p>\n", RssFeed.DescriptionOf(item));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void DescriptionOf_is_null_for_a_blank_description(string text)
    {
        var item = new XElement("item", new XElement("description", text));

        Assert.Null(RssFeed.DescriptionOf(item));
    }

    [Fact]
    public void DescriptionOf_is_null_for_a_missing_description()
    {
        Assert.Null(RssFeed.DescriptionOf(new XElement("item")));
    }
}
