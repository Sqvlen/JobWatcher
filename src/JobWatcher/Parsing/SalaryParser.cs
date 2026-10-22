using System.Globalization;
using System.Text.RegularExpressions;
using JobWatcher.Domain;

namespace JobWatcher.Parsing;

/// <param name="Min">Lower bound; null when the text only states an upper bound ("up to $3000").</param>
/// <param name="Max">Upper bound; null for an open-ended range ("from $3000").</param>
/// <param name="Currency">Currency code ("USD", "EUR", "UAH", "PLN", "GBP"); null when not recognised.</param>
public sealed record SalaryRange(decimal? Min, decimal? Max, string? Currency, SalaryPeriod Period);

/// <summary>Reads the salary text exactly as a source gives it (English or Ukrainian).</summary>
public static partial class SalaryParser
{
    // Without a stated period, a small amount may well be an hourly rate, so it stays Unknown instead of
    // being taken for a (very low) monthly salary and rejected.
    private const decimal SmallestPlausibleMonthlyAmount = 1000m;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // First match in the text wins, so "$5000 (about 200 000 грн)" is USD. Each term is a symbol or a word
    // not embedded in a longer word.
    private static readonly (string Code, Regex Pattern)[] Currencies =
    [
        ("USD", new(@"\$|(?<!\p{L})usd(?!\p{L})|dollar|долар", Options)),
        ("EUR", new(@"€|(?<!\p{L})eur(?!\p{L})|euro|євро", Options)),
        ("GBP", new(@"£|(?<!\p{L})gbp(?!\p{L})|pound|фунт", Options)),
        ("UAH", new(@"₴|(?<!\p{L})uah(?!\p{L})|грн|гривн|гривень", Options)),
        ("PLN", new(@"(?<!\p{L})pln(?!\p{L})|zł|(?<!\p{L})zl(?!\p{L})|злот", Options)),
    ];

    private static readonly (SalaryPeriod Period, Regex Pattern)[] Periods =
    [
        (SalaryPeriod.Hour, new(@"hour|/\s*h(?!\p{L})|(?<!\p{L})hr(?!\p{L})|годин|(?<!\p{L})год(?!\p{L})", Options)),
        (SalaryPeriod.Month, new(@"month|/\s*mo(?!\p{L})|(?<!\p{L})mo(?!\p{L})|міс", Options)),
        (SalaryPeriod.Year, new(@"year|annum|annual|(?<!\p{L})yr(?!\p{L})|(?<!\p{L})p\.a\.|/\s*y(?!\p{L})|рік|року|річн", Options)),
    ];

    // A day or week rate is neither a monthly nor an hourly salary, so it must not fall back to Month (see
    // DetectPeriod). The words stand alone ("/day", "a day", "daily"); "a daytime role" is not a day rate.
    private static readonly Regex DayOrWeekPeriod = new(
        @"/\s*(?:day|week|wk|день|тиждень)(?!\p{L})"
        + @"|(?<!\p{L})(?:per|a|each|every|на|в|за)\s+(?:day|week|wk|день|тиждень)(?!\p{L})"
        + @"|(?<!\p{L})(?:(?:bi-?)?(?:daily|weekly)|щодня|щотижня)(?!\p{L})",
        Options);

    private static readonly Regex OpenEndedMarker = new(@"(?<!\p{L})(?:від|from|starting|min|minimum)(?!\p{L})", Options);

    private static readonly Regex UpperBoundMarker = new(@"(?<!\p{L})(?:до|up\s+to|max|maximum)(?!\p{L})", Options);

    // "3500+", "$3500+", "3500+ USD", "3500 USD+": a plus glued to the amount or to its currency symbol or
    // code. A spaced plus ("3500 USD + bonus") adds something to the amount and does not open the range.
    private static readonly Regex OpenEndedPlus = new(@"^(?:\s*(?:\p{Sc}|\p{L}{2,4}\.?))?\+", Options);

    /// <summary>
    /// Returns null when the text has no amount ("competitive") or the amount cannot be read as a number
    /// (more digits than a decimal holds). A single amount is both Min and Max, except after "from"/"від"
    /// or before a plus sign (open-ended, Max null) and after "up to"/"до" (Min null).
    /// </summary>
    public static SalaryRange? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var amounts = AmountPattern().Matches(text);
        if (amounts.Count == 0)
        {
            return null;
        }

        if (ToAmount(amounts[0]) is not { } first)
        {
            return null;
        }

        decimal? min = first;
        decimal? max;
        if (amounts.Count >= 2)
        {
            if (ToAmount(amounts[1]) is not { } second)
            {
                return null;
            }

            (min, max) = (Math.Min(first, second), Math.Max(first, second));
        }
        else
        {
            var beforeAmount = text[..amounts[0].Index];
            var afterAmount = text[(amounts[0].Index + amounts[0].Length)..];
            if (OpenEndedMarker.IsMatch(beforeAmount) || OpenEndedPlus.IsMatch(afterAmount))
            {
                max = null;
            }
            else if (UpperBoundMarker.IsMatch(beforeAmount))
            {
                (min, max) = (null, first);
            }
            else
            {
                max = first;
            }
        }

        var currency = TryFindEarliest(Currencies, text, out var code) ? code : null;

        return new SalaryRange(min, max, currency, DetectPeriod(text, max ?? min));
    }

    private static SalaryPeriod DetectPeriod(string text, decimal? highestAmount)
    {
        if (TryFindEarliest(Periods, text, out var period))
        {
            return period;
        }

        // Only when no month, year or hour word is given: those win as before.
        if (DayOrWeekPeriod.IsMatch(text))
        {
            return SalaryPeriod.Unknown;
        }

        return highestAmount >= SmallestPlausibleMonthlyAmount ? SalaryPeriod.Month : SalaryPeriod.Unknown;
    }

    private static bool TryFindEarliest<T>(IEnumerable<(T Value, Regex Pattern)> candidates, string text, out T found)
    {
        found = default!;
        var foundIndex = int.MaxValue;
        foreach (var (value, pattern) in candidates)
        {
            var match = pattern.Match(text);
            if (match.Success && match.Index < foundIndex)
            {
                (found, foundIndex) = (value, match.Index);
            }
        }

        return foundIndex != int.MaxValue;
    }

    // Null when the digits are not a number a decimal can hold: too many of them, or digits of another script
    // that \d matches but the number parser does not read ("٣٠٠٠"). The text is third-party input and must
    // never make the parser throw.
    private static decimal? ToAmount(Match match)
    {
        var digits = match.Groups["grouped"].Success
            ? new string(match.Groups["grouped"].Value.Where(char.IsDigit).ToArray())
            : match.Groups["plain"].Value.Replace(',', '.');
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        if (!match.Groups["thousands"].Success)
        {
            return amount;
        }

        return amount <= decimal.MaxValue / 1000 ? amount * 1000 : null;
    }

    // "grouped": digits grouped in threes by a space (also no-break), comma or dot ("4 000", "4,000");
    // "plain": a number with an optional decimal part ("4000", "4.5"). "thousands": the k suffix.
    // Percentages ("+ 10% bonus") are not amounts.
    [GeneratedRegex(
        @"(?:(?<grouped>\d{1,3}(?:[\p{Zs},.]\d{3})+)|(?<plain>\d+(?:[.,]\d+)?))(?!\d)(?<thousands>[kк](?!\p{L}))?(?!\s*%)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
