using System.Text.RegularExpressions;

namespace JobWatcher.Parsing;

/// <summary>Reads the required years of experience from free text in English or Ukrainian.</summary>
public static partial class ExperienceParser
{
    /// <summary>
    /// Returns the largest minimum stated in the text ("3+ years", "3-5 years", "від 3-х років"),
    /// or null when no number of years is mentioned.
    /// </summary>
    public static int? ParseMinYears(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int? minYears = null;
        foreach (Match match in YearsPattern().Matches(text))
        {
            var years = int.Parse(match.Groups["min"].Value);
            minYears = Math.Max(minYears ?? 0, years);
        }

        return minYears;
    }

    // A one- or two-digit number that is not the tail of a larger/decimal number ("1.5 years"), optionally
    // followed by "+", an "-х" suffix or a range end ("-5", " to 5", " до 5"), then a year word.
    // The number itself is ASCII ([0-9]): \d would also match Arabic-Indic or fullwidth digits, which int.Parse
    // refuses, and a description is third-party input that must never make the parser throw. Such digits are
    // not read as a number, but they still block a match next to them (the lookarounds keep \d on purpose).
    [GeneratedRegex(
        @"(?<![\d])(?<!\d[.,])(?<min>[0-9]{1,2})(?!\d)\s*(?:\+|-\s*[хx](?!\p{L})|(?:-|–|—|\bto\b|\bдо\b)\s*\d{1,2}(?!\d))?\s*(?:years?|yrs?|рік|року|роки|років)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearsPattern();
}
