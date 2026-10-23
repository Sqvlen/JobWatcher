using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using JobWatcher.Configuration;
using JobWatcher.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Sources;

/// <summary>
/// Searches JobsPipe through its REST API (see docs/sources/jobspipe.md): one search per fetch for remote vacancies
/// whose title names the profile's keyword or one of its synonyms, the newest first, the first page only. JobsPipe
/// gathers the vacancies from many boards (the ones in <see cref="JobsPipeOptions.ExcludedSources"/> are left out) and
/// gives the full text of each posting, so the requirements list is usually found.
/// </summary>
/// <param name="time">The clock of the fetch interval; the system clock unless a test supplies its own.</param>
/// <param name="log">Where a call that makes no request says so; nothing is logged when it is null.</param>
/// <remarks>
/// SECURITY: the API key travels in the Authorization header, never in the URL, and is never put into an exception
/// message. No exception of the HTTP stack is passed on, nor as an inner exception: whatever the framework throws is
/// replaced by an exception built here (only the caller's own cancellation passes). Whoever registers the HttpClient
/// must also keep the headers out of its logs.
/// QUOTA: a search is paid per vacancy that the key has not received this month (a vacancy already paid for is free,
/// and a search that finds nothing costs nothing), and the plan has a monthly request quota too. So a real search
/// happens at most once per <see cref="JobsPipeOptions.MinFetchIntervalMinutes"/>. A call inside the interval makes no
/// request: it returns an empty list when the last real fetch succeeded, and throws when it failed (so that the
/// failure keeps being counted by the caller and the "source is down" alert can fire). That empty list is logged as an
/// Information line naming the time the next request is due. The interval starts when the request starts, also when it
/// fails, and is remembered in memory only, so a restart of the process fetches again.
/// Not thread-safe: the single polling loop is its only caller.
/// </remarks>
public sealed class JobsPipeSource(
    HttpClient http,
    IOptions<WatcherOptions> options,
    TimeProvider? time = null,
    ILogger<JobsPipeSource>? log = null) : IVacancySource
{
    private const string UserAgent = "JobWatcher/1.0 (personal job alerts; uses the JobsPipe API)";
    private const string SearchPath = "v1/jobs/search";

    // JobsPipe's own error text is shown to help with a wrong request, but only this much of it.
    private const int MaxErrorTextLength = 200;

    // The European Union, and the other European countries; both are regions the profile's AllowedRemoteRegions knows.
    private const string EuCountryCodes = "AT BE BG HR CY CZ DK EE FI FR DE GR HU IE IT LV LT LU MT NL PL PT RO SK SI ES SE";
    private const string OtherEuropeanCountryCodes = "AD AL BA CH IS LI MC MD ME MK NO RS SM VA XK";

    // Keywords are sent as readable UTF-8, not as \uXXXX escapes (a JSON body is not HTML).
    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The profile's region of the countries it knows by name; every other country stays a country of its own, so that
    // a remote job tied to India or Australia is not taken for one that is open to a candidate in Ukraine.
    private static readonly Dictionary<string, string> RegionByCountryCode = RegionTable();

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    // When the last real fetch began (a monotonic timestamp); null until the first one.
    private long? lastFetchStarted;

    // When the interval of the last real fetch ends, in UTC, for the message of a call inside the interval.
    private DateTimeOffset nextFetchDue;

    // Why the last real fetch failed; null when it succeeded (or was cut short by the caller's cancellation).
    private string? lastFailure;

    public VacancySource Source => VacancySource.JobsPipe;

    public async Task<IReadOnlyList<Vacancy>> FetchAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var jobsPipe = settings.Sources.JobsPipe;

        var interval = TimeSpan.FromMinutes(jobsPipe.MinFetchIntervalMinutes);
        if (lastFetchStarted is { } started && clock.GetElapsedTime(started) < interval)
        {
            // No request: the quota is protected. A failure is repeated, not hidden behind an empty list, because
            // the caller counts consecutive failures and an empty list would count as a success.
            if (lastFailure is not null)
            {
                throw new InvalidOperationException(
                    $"The last JobsPipe fetch failed: {lastFailure.TrimEnd('.')}. No new request is made before "
                    + $"{nextFetchDue.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}, "
                    + "to protect the quota of the API key.");
            }

            // The empty list looks like "no vacancies" in the watch cycle's summary; this line says why.
            log?.LogInformation(
                "JobsPipe: no request this time, the next one is due after {NextFetch:O} (MinFetchIntervalMinutes={Minutes})",
                nextFetchDue.UtcDateTime,
                jobsPipe.MinFetchIntervalMinutes);

            return [];
        }

        lastFetchStarted = clock.GetTimestamp();
        nextFetchDue = clock.GetUtcNow() + interval;
        lastFailure = null;
        try
        {
            return await SearchAsync(settings, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the caller's own cancellation passes unchanged (it is neither a failure nor carries a secret).
            // A timeout of the HttpClient is also an OperationCanceledException but not the caller's.
            if (exception is HttpRequestException or InvalidDataException)
            {
                lastFailure = exception.Message;

                throw;
            }

            var sanitised = new HttpRequestException($"The JobsPipe request failed (unexpected {exception.GetType().Name}).");
            lastFailure = sanitised.Message;

            throw sanitised;
        }
    }

    private async Task<List<Vacancy>> SearchAsync(WatcherOptions settings, CancellationToken ct)
    {
        var jobsPipe = settings.Sources.JobsPipe;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{jobsPipe.BaseUrl.TrimEnd('/')}/{SearchPath}")
        {
            Content = JsonContent.Create(SearchBody(settings), options: BodyOptions),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jobsPipe.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException exception)
        {
            // Not passed on and not wrapped: the exception's text and inner exceptions are the framework's, not ours.
            throw new HttpRequestException($"The JobsPipe request failed ({exception.HttpRequestError}).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await ErrorTextAsync(response, ct);
                var message = StatusMessage(response.StatusCode) + (errorText is null ? "" : $" JobsPipe says: {errorText}");

                throw new HttpRequestException(message, null, response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The JobsPipe response is not valid JSON.");
            }

            using (document)
            {
                return ReadJobs(document.RootElement);
            }
        }
    }

    // Remote vacancies only (the profile is remote, or Kyiv, and JobsPipe has no Ukrainian boards), newest first.
    private static Dictionary<string, object> SearchBody(WatcherOptions settings)
    {
        var jobsPipe = settings.Sources.JobsPipe;
        var profile = settings.Profile;
        var body = new Dictionary<string, object>
        {
            ["job_title_or"] = new[] { profile.Keyword }
                .Concat(profile.Synonyms)
                .Select(term => term.Trim())
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["remote"] = true,
            ["posted_at_max_age_days"] = jobsPipe.MaxAgeDays,
            ["limit"] = jobsPipe.PageSize,
        };

        if (jobsPipe.ExcludedSources.Length > 0)
        {
            body["source_not"] = jobsPipe.ExcludedSources.Select(source => source.Trim()).ToArray();
        }

        return body;
    }

    private static string StatusMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "JobsPipe answered HTTP 401: the API key is invalid, revoked or missing.",
        HttpStatusCode.PaymentRequired =>
            "JobsPipe answered HTTP 402: the monthly quota of the plan is used up; it resets at the start of the next UTC month.",
        HttpStatusCode.TooManyRequests => "JobsPipe answered HTTP 429: too many requests per second.",
        HttpStatusCode.GatewayTimeout =>
            "JobsPipe answered HTTP 504: the search was too large or took too long; narrow the filters (see docs/sources/jobspipe.md).",
        _ => $"JobsPipe answered HTTP {(int)status} ({status}).",
    };

    // The "error" text of a JSON error body, cut short; null when the body is something else.
    private static async Task<string?> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString()?.Trim() is { Length: > 0 } text)
            {
                return text.Length > MaxErrorTextLength ? text[..MaxErrorTextLength] + "…" : text;
            }

            return null;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static List<Vacancy> ReadJobs(JsonElement root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } || !root.TryGetProperty("data", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The JobsPipe response has no list of jobs (data); the API format has probably changed.");
        }

        var count = jobs.GetArrayLength();
        var vacancies = jobs.EnumerateArray().Select(Map).OfType<Vacancy>().DistinctBy(vacancy => vacancy.SourceId).ToList();
        if (count > 0 && vacancies.Count == 0)
        {
            throw new InvalidDataException($"None of the {count} jobs of the JobsPipe response could be read; the API format has probably changed.");
        }

        return vacancies;
    }

    // Null when the job is unusable: without an id it cannot be remembered, without a title and a web link it cannot
    // be shown or opened.
    private static Vacancy? Map(JsonElement job)
    {
        if (job.ValueKind != JsonValueKind.Object
            || IdOf(job) is not { } id
            || TextOf(job, "job_title") is not { } title
            || new[] { TextOf(job, "url"), TextOf(job, "source_url") }.FirstOrDefault(candidate => candidate is not null && IsWebLink(candidate)) is not { } link)
        {
            return null;
        }

        // The text as published, only a blank one is no description.
        var description = job.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        description = string.IsNullOrWhiteSpace(description) ? null : description;

        var remote = job.TryGetProperty("remote", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean()
            : (bool?)null;
        var arrangement = TextOf(job, "work_arrangement")?.ToLowerInvariant();
        var workMode = remote == true || arrangement == "remote" ? WorkMode.Remote
            : remote == false || arrangement is "hybrid" or "onsite" ? WorkMode.Office
            : WorkMode.Unknown;

        return new Vacancy(
            VacancySource.JobsPipe,
            id,
            title,
            // A blank company would make the watch cycle drop the vacancy, so a missing one is shown as not specified.
            TextOf(job, "company") ?? Placeholders.NotSpecified,
            link,
            SalaryOf(job),
            TextOf(job, "location"),
            workMode,
            workMode == WorkMode.Remote ? RemoteRegionOf(job) : null,
            SourceMapping.MinYearsExperience(title, description),
            SourceMapping.IsJuniorTitle(title),
            description);
    }

    // The posting's own salary text; failing that, the pay it states as numbers, written like DOU writes a yearly salary.
    // A benchmark of the occupation is not the posting's pay, so a salary of the "estimated" type is no salary.
    private static string? SalaryOf(JsonElement job)
    {
        if (TextOf(job, "salary_string") is { } text)
        {
            return text;
        }

        return TextOf(job, "salary_type") == "estimated"
            ? null
            : JobPostingPage.SalaryText(AmountOf(job, "min_annual_salary"), AmountOf(job, "max_annual_salary"), TextOf(job, "salary_currency"), "YEAR");
    }

    // The region the remote job is tied to by the country it sits in; null when JobsPipe names no country.
    private static string? RemoteRegionOf(JsonElement job)
    {
        if (TextOf(job, "country_code")?.ToUpperInvariant() is not { } code)
        {
            return null;
        }

        return RegionByCountryCode.GetValueOrDefault(code) ?? TextOf(job, "country") ?? code;
    }

    // The id is a string (often the posting's address) or a number, kept as sent; null when the job has no usable id.
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

    private static bool IsWebLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // The property's text, trimmed; null when it is absent, not a string or blank.
    private static string? TextOf(JsonElement job, string name) =>
        job.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    // A positive number; null for anything else.
    private static decimal? AmountOf(JsonElement job, string name) =>
        job.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) && amount > 0
            ? amount
            : null;

    private static Dictionary<string, string> RegionTable()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UA"] = "Ukraine",
            ["US"] = "US",
            ["GB"] = "UK",
        };
        foreach (var code in EuCountryCodes.Split(' '))
        {
            table[code] = "EU";
        }

        foreach (var code in OtherEuropeanCountryCodes.Split(' '))
        {
            table[code] = "Europe";
        }

        return table;
    }
}
