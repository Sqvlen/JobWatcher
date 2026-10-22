using System.Globalization;
using System.Text;
using JobWatcher.Domain;

namespace JobWatcher.Formatting;

/// <summary>
/// Renders one vacancy as the Telegram message (HTML for <c>parse_mode=HTML</c>). The vacancy's own text is
/// escaped but never translated or rephrased; only the labels are fixed Ukrainian text.
/// </summary>
public static class MessageFormatter
{
    public const int TelegramLimit = 4096;

    private const string Ellipsis = "…";
    private const string PartSeparator = " · ";
    private const string RequirementsHeading = "📋 Вимоги:";

    /// <summary>
    /// A longer message than <see cref="TelegramLimit"/> characters is shrunk by dropping requirements from the end,
    /// then by cutting the title (marked with an ellipsis); the link line stays. The result is within the limit as
    /// long as the lines that are never shortened (company, salary, location and link) fit in it.
    /// </summary>
    public static string Format(Vacancy v, IReadOnlyList<string> requirements, CompanyRating? rating)
    {
        var title = EscapeHtml(v.Title);
        var details = DetailLines(v, rating);
        var link = $"🔗 <a href=\"{EscapeAttribute(v.Url)}\">Відкрити вакансію ({EscapeHtml(v.Source.DisplayName())})</a>";
        var items = requirements
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => "• " + EscapeHtml(item))
            .ToList();

        var message = Build(title, details, items, link);
        while (message.Length > TelegramLimit && items.Count > 0)
        {
            items.RemoveAt(items.Count - 1);
            message = Build(title, details, items, link);
        }

        if (message.Length <= TelegramLimit)
        {
            return message;
        }

        var budget = TelegramLimit - Build(string.Empty, details, items, link).Length - Ellipsis.Length;

        return Build(Shorten(v.Title, budget), details, items, link);
    }

    /// <summary>
    /// Escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> so the text is safe inside a Telegram HTML message;
    /// null becomes an empty string. Anything interpolated into an HTML message must pass through here.
    /// </summary>
    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            escaped.Append(EscapeChar(c) ?? c.ToString());
        }

        return escaped.ToString();
    }

    private static List<string> DetailLines(Vacancy v, CompanyRating? rating)
    {
        var ratingText = rating is null
            ? null
            : $"⭐ {rating.Value.ToString("0.0", CultureInfo.InvariantCulture)} ({EscapeHtml(rating.Provider)})";
        var remoteText = v.WorkMode == WorkMode.Remote ? "Remote" : null;
        var salary = string.IsNullOrWhiteSpace(v.Salary) ? Placeholders.NotSpecified : EscapeHtml(v.Salary);

        var lines = new List<string>();
        AddIfPresent(lines, Line("🏢", EscapeHtml(v.Company), ratingText));
        lines.Add($"💰 {salary}");
        AddIfPresent(lines, Line("📍", EscapeHtml(v.Location), remoteText));

        return lines;
    }

    // Joins the non-blank parts after the icon; null when there is nothing to show.
    private static string? Line(string icon, string main, string? suffix)
    {
        var text = string.Join(PartSeparator, new[] { main, suffix }.Where(part => !string.IsNullOrWhiteSpace(part)));

        return text.Length == 0 ? null : $"{icon} {text}";
    }

    private static void AddIfPresent(List<string> lines, string? line)
    {
        if (line is not null)
        {
            lines.Add(line);
        }
    }

    private static string Build(string title, List<string> details, List<string> items, string link)
    {
        var lines = new List<string> { $"🆕 <b>{title}</b>" };
        lines.AddRange(details);
        lines.Add(string.Empty);
        if (items.Count > 0)
        {
            lines.Add(RequirementsHeading);
            lines.AddRange(items);
            lines.Add(string.Empty);
        }

        lines.Add(link);

        return string.Join('\n', lines);
    }

    // Cuts the raw title so that its escaped form plus the ellipsis fits the budget. Cutting before escaping
    // keeps an entity such as "&amp;" whole; a surrogate pair is never split.
    private static string Shorten(string title, int budget)
    {
        var used = 0;
        var keep = 0;
        foreach (var c in title)
        {
            var length = EscapeChar(c)?.Length ?? 1;
            if (used + length > budget)
            {
                break;
            }

            used += length;
            keep++;
        }

        if (keep > 0 && char.IsHighSurrogate(title[keep - 1]))
        {
            keep--;
        }

        return EscapeHtml(title[..keep].TrimEnd()) + Ellipsis;
    }

    private static string EscapeAttribute(string text) => EscapeHtml(text).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string? EscapeChar(char c) => c switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        _ => null,
    };
}
