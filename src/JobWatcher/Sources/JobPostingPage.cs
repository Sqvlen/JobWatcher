using System.Globalization;
using System.Text.Json;
using HtmlAgilityPack;
using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>
/// Reads the schema.org <c>JobPosting</c> that a vacancy page publishes for machines in a
/// <c>&lt;script type="application/ld+json"&gt;</c> block, and fills a vacancy from it. Only that block is read, never
/// the layout of the page. See docs/sources/djinni.md for the fields and the real pages they were checked on.
/// </summary>
internal static class JobPostingPage
{
    private const string JsonLdType = "application/ld+json";
    private const string JobPostingType = "JobPosting";
    private const string Telecommute = "TELECOMMUTE";
    private const int MonthsPerYear = 12;

    // SalaryParser takes an amount without a stated period for a monthly one only from this amount up, so a smaller
    // monthly amount gets its period written out.
    private const decimal SmallestImplicitlyMonthlyAmount = 1000m;

    // The currencies SalaryParser recognises by their symbol; any other code is written after the amount.
    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["UAH"] = "₴",
    };

    /// <summary>
    /// A copy of the vacancy with every field the page's JobPosting states in a usable form; all other fields stay as
    /// they were. Every JSON-LD block is looked at; one that is not valid JSON is skipped, since it may be some other
    /// block. Throws <see cref="InvalidDataException"/>, naming the vacancy, when no block holds a JobPosting (saying
    /// so when a block was not valid JSON).
    /// </summary>
    public static Vacancy Enrich(Vacancy vacancy, string html)
    {
        var page = new HtmlDocument();
        page.LoadHtml(html);

        var blocks = page.DocumentNode
            .Descendants("script")
            .Where(script => string.Equals(script.GetAttributeValue("type", "").Trim(), JsonLdType, StringComparison.OrdinalIgnoreCase));
        JsonException? invalid = null;
        foreach (var block in blocks)
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(block.InnerHtml);
            }
            catch (JsonException ex)
            {
                invalid ??= ex;
                continue;
            }

            using (json)
            {
                if (FindJobPosting(json.RootElement) is { } posting)
                {
                    return Apply(vacancy, posting);
                }
            }
        }

        if (invalid is not null)
        {
            throw new InvalidDataException(
                $"The JSON-LD data of the {vacancy.Source.DisplayName()} page of vacancy {vacancy.SourceId} is not valid JSON.", invalid);
        }

        throw new InvalidDataException(
            $"The {vacancy.Source.DisplayName()} page of vacancy {vacancy.SourceId} has no schema.org JobPosting data; the page markup has probably changed.");
    }

    /// <summary>
    /// The salary as text in the style DOU writes it ("$2000–2500", "від $2000", "до $2500", "2000–2500 PLN",
    /// "$60000–80000 / year"), so that <see cref="Parsing.SalaryParser"/> reads back the same bounds, currency and
    /// period. Null when neither bound is given.
    /// </summary>
    public static string? SalaryText(decimal? min, decimal? max, string? currency, string? unit)
    {
        var code = currency?.Trim().ToUpperInvariant();
        var symbol = code is not null && CurrencySymbols.TryGetValue(code, out var known) ? known : "";
        var codeAfter = symbol.Length == 0 && !string.IsNullOrEmpty(code) ? " " + code : "";

        var amounts = (min, max) switch
        {
            ({ } low, { } high) when low == high => $"{symbol}{Number(low)}",
            ({ } low, { } high) => $"{symbol}{Number(low)}–{Number(high)}",
            ({ } low, null) => $"від {symbol}{Number(low)}",
            (null, { } high) => $"до {symbol}{Number(high)}",
            _ => null,
        };

        return amounts is null ? null : amounts + codeAfter + PeriodSuffix(unit, (max ?? min)!.Value);
    }

    // The block holds one object, a list of them, or an object whose "@graph" lists them.
    private static JsonElement? FindJobPosting(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(FindJobPosting).FirstOrDefault(found => found is not null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Items(Property(element, "@type")).Any(type => Text(type) == JobPostingType))
        {
            return element;
        }

        return Property(element, "@graph") is { } graph ? FindJobPosting(graph) : null;
    }

    private static Vacancy Apply(Vacancy vacancy, JsonElement posting)
    {
        var workMode = WorkModeOf(posting);

        return vacancy with
        {
            Company = Text(Property(Property(posting, "hiringOrganization"), "name")) ?? vacancy.Company,
            Salary = SalaryOf(posting) ?? vacancy.Salary,
            WorkMode = workMode ?? vacancy.WorkMode,
            Location = workMode == WorkMode.Office ? LocalityOf(posting) ?? vacancy.Location : vacancy.Location,
            RemoteRestrictedTo = workMode == WorkMode.Remote ? RemoteRegionOf(posting) : vacancy.RemoteRestrictedTo,
            MinYearsExperience = YearsOf(posting) ?? vacancy.MinYearsExperience,
        };
    }

    // Null when the page says neither.
    private static WorkMode? WorkModeOf(JsonElement posting)
    {
        if (Items(Property(posting, "jobLocationType")).Any(type => string.Equals(Text(type), Telecommute, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkMode.Remote;
        }

        return Items(Property(posting, "jobLocation")).Any(place => place.ValueKind == JsonValueKind.Object) ? WorkMode.Office : null;
    }

    // "addressLocality" is a string or (on Djinni) a list of cities.
    private static string? LocalityOf(JsonElement posting)
    {
        var cities = Items(Property(posting, "jobLocation"))
            .SelectMany(place => Items(Property(Property(place, "address"), "addressLocality")))
            .Select(city => Text(city))
            .OfType<string>()
            .Distinct()
            .ToList();

        return cities.Count > 0 ? string.Join(", ", cities) : null;
    }

    // Exactly one requirement that names a known region; anything else is unrestricted or unknown (null), which the
    // filter lets pass. A country, when given, decides; otherwise the region does.
    private static string? RemoteRegionOf(JsonElement posting)
    {
        var requirements = Items(Property(posting, "applicantLocationRequirements")).ToList();
        if (requirements.Count != 1)
        {
            return null;
        }

        var address = Property(requirements[0], "address");

        return Text(Property(address, "addressCountry")) is { } country
            ? SourceMapping.NormalizeCountry(country)
            : SourceMapping.NormalizeRegion(Text(Property(address, "addressRegion")));
    }

    private static string? SalaryOf(JsonElement posting)
    {
        var salary = Property(posting, "baseSalary");
        var value = Property(salary, "value");

        return SalaryText(
            Amount(Property(value, "minValue")),
            Amount(Property(value, "maxValue")),
            Text(Property(salary, "currency")),
            Text(Property(value, "unitText")));
    }

    // Months of experience in whole years, half up: 30 months is 3 years.
    private static int? YearsOf(JsonElement posting)
    {
        var months = Property(Property(posting, "experienceRequirements"), "monthsOfExperience");
        if (months is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDecimal(out var count) || count < 0)
        {
            return null;
        }

        var years = Math.Round(count / MonthsPerYear, MidpointRounding.AwayFromZero);

        return years <= int.MaxValue ? (int)years : null;
    }

    private static string PeriodSuffix(string? unit, decimal highest) => unit?.Trim().ToUpperInvariant() switch
    {
        "MONTH" => highest < SmallestImplicitlyMonthlyAmount ? " / month" : "",
        "YEAR" => " / year",
        "HOUR" => " / hour",
        "WEEK" => " / week",
        "DAY" => " / day",
        _ => "",
    };

    private static string Number(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);

    // A positive number; null for anything else.
    private static decimal? Amount(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetDecimal(out var amount) && amount > 0 ? amount : null;

    // The property's value; null when the element is not an object, or the property is missing or JSON null.
    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } owner
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    // The elements of a list, or the value itself when it is not a list.
    private static IEnumerable<JsonElement> Items(JsonElement? element) => element switch
    {
        null => [],
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Where(item => item.ValueKind != JsonValueKind.Null),
        { } single => [single],
    };

    // The trimmed string; null when it is not a string or blank.
    private static string? Text(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } text && text.GetString()?.Trim() is { Length: > 0 } value ? value : null;
}
