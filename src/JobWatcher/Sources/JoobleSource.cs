using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Sources;

/// <summary>
/// Searches Jooble through its REST API (see docs/sources/jooble.md): one query for the profile's first city and
/// one for remote work, first page of each, merged and de-duplicated by the Jooble id.
/// </summary>
/// <param name="time">The clock of the fetch interval; the system clock unless a test supplies its own.</param>
/// <param name="log">Where a call that makes no request says so; nothing is logged when it is null.</param>
/// <remarks>
/// SECURITY: the API key is part of the request URL path. It is never put into an exception message, and no
/// exception of the HTTP stack is passed on, nor as an inner exception, because those may carry the URL: whatever
/// the framework throws is replaced by an exception built here (only the caller's own cancellation passes).
/// Whoever registers the HttpClient must also keep the key out of its logs (see docs/sources/jooble.md).
/// A response holds only a snippet of the description, so the requirements list is often empty, which is normal.
/// QUOTA: a free key allows 500 requests in its whole life, so a real fetch (two requests) happens at most once per
/// <see cref="JoobleOptions.MinFetchIntervalMinutes"/>. A call inside the interval makes no request: it returns an
/// empty list when the last real fetch succeeded, and throws when it failed (so that the failure keeps being counted
/// by the caller and the "source is down" alert can fire). That empty list is logged as an Information line naming
/// the time the next request is due. The interval starts when the requests start, also when they fail (a failed
/// request may still count), and is remembered in memory only, so a restart of the process fetches again.
/// Not thread-safe: the single polling loop is its only caller.
/// </remarks>
public sealed partial class JoobleSource(
    HttpClient http,
    IOptions<WatcherOptions> options,
    TimeProvider? time = null,
    ILogger<JoobleSource>? log = null) : IVacancySource
{
    private const string UserAgent = "JobWatcher/1.0 (personal job alerts; uses the Jooble REST API)";
    private const string ApiKeySetting = "JobWatcher__Sources__Jooble__ApiKey";

    // Asked for when the profile lists no city: the search is about Kyiv, so the source still has something to look up.
    private const string DefaultCity = "Київ";

    // The second query: Jooble matches the word against the vacancies' location text.
    private const string RemoteLocation = "remote";

    // A vacancy that has no id is told apart by its link; the prefix keeps such ids apart from Jooble's numbers.
    private const string LinkIdPrefix = "link-";
    private const int LinkIdLength = 16;

    // Keywords and city names are sent as readable UTF-8, not as \uXXXX escapes (a JSON body is not HTML).
    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    // When the last real fetch began (a monotonic timestamp); null until the first one.
    private long? lastFetchStarted;

    // When the interval of the last real fetch ends, in UTC, for the message of a call inside the interval.
    private DateTimeOffset nextFetchDue;

    // Why the last real fetch failed; null when it succeeded (or was cut short by the caller's cancellation). Only
    // the text of an exception built in this class, never an exception object: those can carry the request URL.
    private string? lastFailure;

    public VacancySource Source => VacancySource.Jooble;

    public async Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var jooble = settings.Sources.Jooble;

        // Checked first and without touching the interval: no request is made, so no quota is involved.
        if (jooble.ApiKey?.Trim() is not { Length: > 0 } apiKey)
        {
            throw new SourceNotConnectedException(
                VacancySource.Jooble,
                "the Jooble API key is not set. Request a key for Ukraine at https://ua.jooble.org/api/about (a key from "
                + $"jooble.org covers only US vacancies) and set the environment variable {ApiKeySetting}.");
        }

        var interval = TimeSpan.FromMinutes(jooble.MinFetchIntervalMinutes);
        if (lastFetchStarted is { } started && clock.GetElapsedTime(started) < interval)
        {
            // No request: the quota is protected. A failure is repeated, not hidden behind an empty list, because
            // the caller counts consecutive failures and an empty list would count as a success.
            if (lastFailure is not null)
            {
                throw new InvalidOperationException(
                    $"The last Jooble fetch failed: {lastFailure.TrimEnd('.')}. No new request is made before "
                    + $"{nextFetchDue.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}, "
                    + "to protect the request quota of the API key.");
            }

            // The empty list looks like "no vacancies" in the watch cycle's summary; this line says why. A replayed
            // failure needs none: its exception names the due time and the caller logs it.
            log?.LogInformation(
                "Jooble: no request this time, the next one is due after {NextFetch:O} (MinFetchIntervalMinutes={Minutes})",
                nextFetchDue.UtcDateTime,
                jooble.MinFetchIntervalMinutes);

            return [];
        }

        lastFetchStarted = clock.GetTimestamp();
        nextFetchDue = clock.GetUtcNow() + interval;
        lastFailure = null;
        try
        {
            return await FetchFromJoobleAsync(settings, apiKey, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the caller's own cancellation passes unchanged (it is neither a failure nor carries the URL).
            // A timeout of the HttpClient is also an OperationCanceledException but not the caller's.
            if (exception is HttpRequestException or InvalidDataException)
            {
                lastFailure = exception.Message;

                throw;
            }

            var sanitised = new HttpRequestException($"The Jooble request failed (unexpected {exception.GetType().Name}).");
            lastFailure = sanitised.Message;

            throw sanitised;
        }
    }

    private async Task<IReadOnlyList<Vacancy>> FetchFromJoobleAsync(WatcherOptions settings, string apiKey, CancellationToken ct)
    {
        var cities = settings.Profile.Cities;
        var city = cities.Length > 0 && !string.IsNullOrWhiteSpace(cities[0]) ? cities[0].Trim() : DefaultCity;

        var endpoint = $"{settings.Sources.Jooble.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(apiKey)}";
        var vacancies = new List<Vacancy>();
        var seen = new HashSet<string>();
        foreach (var location in new[] { city, RemoteLocation })
        {
            foreach (var vacancy in await SearchAsync(endpoint, settings.Profile.Keyword, location, ct))
            {
                if (seen.Add(vacancy.SourceId))
                {
                    vacancies.Add(vacancy);
                }
            }
        }

        return vacancies;
    }

    // The page is not sent: the API's default is the first one.
    private async Task<List<Vacancy>> SearchAsync(string endpoint, string keywords, string location, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { keywords, location }, options: BodyOptions),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException exception)
        {
            // Not passed on and not wrapped: the exception's text and inner exceptions may hold the request URL.
            throw new HttpRequestException($"The Jooble request failed ({exception.HttpRequestError}).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(StatusMessage(response.StatusCode), null, response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The Jooble response is not valid JSON.");
            }

            using (document)
            {
                return ReadJobs(document.RootElement);
            }
        }
    }

    private static string StatusMessage(HttpStatusCode status) => status == HttpStatusCode.Forbidden
        ? "Jooble answered HTTP 403: the API key is invalid or missing, or it was issued for another Jooble domain "
          + "(a key works only on the domain it was issued for; see docs/sources/jooble.md)."
        : $"Jooble answered HTTP {(int)status} ({status}).";

    private static List<Vacancy> ReadJobs(JsonElement root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } || !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Jooble response has no list of jobs; the API format has probably changed.");
        }

        var count = jobs.GetArrayLength();
        var vacancies = jobs.EnumerateArray().Select(Map).OfType<Vacancy>().ToList();
        if (count > 0 && vacancies.Count == 0)
        {
            throw new InvalidDataException($"None of the {count} jobs of the Jooble response could be read; the API format has probably changed.");
        }

        return vacancies;
    }

    // Null when the job is unusable: without a title and a web link it cannot be shown or opened.
    private static Vacancy? Map(JsonElement job)
    {
        if (job.ValueKind != JsonValueKind.Object
            || TextOf(job, "title") is not { } title
            || TextOf(job, "link") is not { } link
            || !IsWebLink(link))
        {
            return null;
        }

        // A blank company would make the watch cycle drop the vacancy, so a missing one is shown as not specified.
        var company = TextOf(job, "company") ?? Placeholders.NotSpecified;
        var location = TextOf(job, "location");
        var snippet = job.TryGetProperty("snippet", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var description = string.IsNullOrWhiteSpace(snippet) ? null : snippet;

        var isRemote = new[] { TextOf(job, "type"), location, title }.Any(text => text is not null && RemoteWord().IsMatch(text));
        var workMode = isRemote ? WorkMode.Remote : location is null ? WorkMode.Unknown : WorkMode.Office;

        return new Vacancy(
            VacancySource.Jooble,
            IdOf(job) ?? LinkId(link),
            title,
            company,
            link,
            TextOf(job, "salary"),
            location,
            workMode,
            isRemote ? RemoteRegion(location) : null,
            SourceMapping.MinYearsExperience(title, description),
            SourceMapping.IsJuniorTitle(title),
            description);
    }

    // The documented id is a number (a 64-bit one, often negative), kept as the digits the API sent; a string is
    // accepted too. Null when the job has no usable id.
    private static string? IdOf(JsonElement job)
    {
        if (!job.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(id.GetString()) => id.GetString()!.Trim(),
            _ => null,
        };
    }

    // SHA-256 (string.GetHashCode changes from run to run) of the link, cut to 16 hex digits: the same link
    // always gives the same id, so the vacancy is announced once.
    private static string LinkId(string link) =>
        LinkIdPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(link)))[..LinkIdLength];

    // The one region a remote vacancy's location names ("Remote, Europe", "Ukraine (remote)"); null when it names
    // none or several, because the profile's filter compares the value for equality.
    private static string? RemoteRegion(string? location)
    {
        if (location is null)
        {
            return null;
        }

        var regions = LocationSeparator().Split(location)
            .Select(part => RemoteWord().Replace(part, " ").Trim())
            .Select(SourceMapping.NormalizeRegion)
            .OfType<string>()
            .Distinct()
            .ToList();

        return regions.Count == 1 ? regions[0] : null;
    }

    private static bool IsWebLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // The property's text, trimmed; null when it is absent, not a string or blank.
    private static string? TextOf(JsonElement job, string name) =>
        job.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    [GeneratedRegex(
        @"(?<!\p{L})(?:remote(?:ly)?|віддален\p{L}*|дистанційн\p{L}*)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteWord();

    // "Remote, Europe", "Ukraine (remote)", "Remote / UK", "Remote - EU".
    [GeneratedRegex(@"[,;/|()]|\s[-–—]\s", RegexOptions.CultureInvariant)]
    private static partial Regex LocationSeparator();
}
