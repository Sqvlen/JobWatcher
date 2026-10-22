using System.Xml;
using System.Xml.Linq;
using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>
/// The reading that every RSS-based source adapter shares, in one place: the safe parse of third-party input and
/// the rules that keep a failure visible have to change in lockstep for all of them. An adapter keeps only its own
/// constants and the mapping of one item to a <see cref="Vacancy"/>.
/// </summary>
internal static class RssFeed
{
    /// <summary>
    /// Makes one GET request for the feed and maps its items. An HTTP error, a document that is not well-formed
    /// XML (a login or bot-check page, for one), a document that is not an RSS feed, and a feed whose items the
    /// mapper cannot read at all all throw. A valid feed without items gives an empty list; an item the mapper
    /// rejects (returns null) is skipped.
    /// </summary>
    public static async Task<IReadOnlyList<Vacancy>> FetchAsync(
        HttpClient http,
        string feedUrl,
        string userAgent,
        string sourceName,
        Func<XElement, Vacancy?> map,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        request.Headers.UserAgent.ParseAdd(userAgent);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var channel = (await ReadAsync(body, ct)).Root is { Name.LocalName: "rss" } rss ? rss.Element("channel") : null;
        if (channel is null)
        {
            throw new InvalidDataException($"The {sourceName} response is not an RSS feed.");
        }

        var items = channel.Elements("item").ToList();
        var vacancies = items.Select(map).OfType<Vacancy>().ToList();
        if (items.Count > 0 && vacancies.Count == 0)
        {
            throw new InvalidDataException($"None of the {items.Count} items of the {sourceName} feed could be read; the feed format has probably changed.");
        }

        return vacancies;
    }

    /// <summary>The trimmed text of the item's child element; null when the element is missing or blank.</summary>
    public static string? TextOf(XElement item, string name) =>
        item.Element(name)?.Value.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The item's description as the feed gives it (HTML, not trimmed); null when missing or blank.</summary>
    public static string? DescriptionOf(XElement item) =>
        item.Element("description")?.Value is { } description && !string.IsNullOrWhiteSpace(description) ? description : null;

    // The feed is third-party input: a document type declaration (entity expansion) is refused outright.
    private static async Task<XDocument> ReadAsync(Stream body, CancellationToken ct)
    {
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });

        return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
    }
}
