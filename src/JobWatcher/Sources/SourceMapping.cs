using System.Text.RegularExpressions;
using JobWatcher.Parsing;

namespace JobWatcher.Sources;

/// <summary>
/// The rules every source adapter shares when it turns a board's text into a <c>Vacancy</c>. They live in one place
/// so that the same text means the same thing whichever board it came from: the profile's AllowedRemoteRegions
/// filter compares the region strings for equality, and a table that differed per adapter would quietly change
/// what passes the filter for one source only.
/// </summary>
internal static partial class SourceMapping
{
    // The regions a remote vacancy can be restricted to, as the profile's AllowedRemoteRegions spells them.
    // Anything else (a country not listed, "Worldwide") is unrestricted or unknown, which is null.
    private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ukraine"] = "Ukraine",
        ["Україна"] = "Ukraine",
        ["Europe"] = "Europe",
        ["Європа"] = "Europe",
        ["EU"] = "EU",
        ["European Union"] = "EU",
        ["ЄС"] = "EU",
        ["US"] = "US",
        ["USA"] = "US",
        ["United States"] = "US",
        ["США"] = "US",
        ["UK"] = "UK",
        ["United Kingdom"] = "UK",
    };

    // The countries that are one of those regions, as a board's structured data names them (Djinni's JSON-LD gives
    // "UA" or "UKR", and "Україна" as the country of an office). Other countries are not a region the profile knows.
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UA"] = "Ukraine",
        ["UKR"] = "Ukraine",
        ["Ukraine"] = "Ukraine",
        ["Україна"] = "Ukraine",
        ["US"] = "US",
        ["USA"] = "US",
        ["United States"] = "US",
        ["США"] = "US",
        ["GB"] = "UK",
        ["GBR"] = "UK",
        ["UK"] = "UK",
        ["United Kingdom"] = "UK",
    };

    /// <summary>
    /// The exact region string ("Europe", "EU", "US", ...) for a name of a region, ignoring case, surrounding
    /// whitespace and one trailing "only" ("лише", "тільки"), so "US only" is "US" and the filter can reject it;
    /// null for anything else, so free text such as "Poland only" never reaches the filter.
    /// </summary>
    public static string? NormalizeRegion(string? text) =>
        text is null ? null : Regions.GetValueOrDefault(TrailingOnly().Replace(text, "").Trim());

    /// <summary>
    /// The exact region string for a country as structured data names it (a code such as "UA" or "UKR", or a name),
    /// ignoring case and surrounding whitespace; null for any other country, which the profile cannot express.
    /// </summary>
    public static string? NormalizeCountry(string? text) =>
        text is null ? null : Countries.GetValueOrDefault(text.Trim());

    /// <summary>
    /// True when the vacancy title contains junior, jr, trainee, intern, internship or a Ukrainian equivalent as a
    /// whole word. Only the title counts: a company called "Junior Games" or a snippet that mentions interns does not.
    /// </summary>
    public static bool IsJuniorTitle(string title) => JuniorWord().IsMatch(title);

    /// <summary>
    /// The years of experience the vacancy asks for: the largest minimum stated in the title or in the requirement
    /// items of the description. Elsewhere in a description a number of years usually describes the company
    /// ("a 2 year old startup"), not the candidate, so the rest of the text is not read. Null when none is stated.
    /// </summary>
    public static int? MinYearsExperience(string title, string? description) =>
        new[] { title }
            .Concat(RequirementsExtractor.Extract(description))
            .Select(ExperienceParser.ParseMinYears)
            .Max();

    [GeneratedRegex(@"\s+(?:only|лише|тільки)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOnly();

    [GeneratedRegex(
        @"(?<!\p{L})(?:junior|jr|trainee|intern|internship|джуніор|джун|стажер|стажист|стажування)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JuniorWord();
}
