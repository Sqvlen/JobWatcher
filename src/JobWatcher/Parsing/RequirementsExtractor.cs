using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace JobWatcher.Parsing;

/// <summary>
/// Pulls the bullets under a "Requirements"-style heading out of a vacancy description (HTML or plain text).
/// The items are returned verbatim in the vacancy's own language: whitespace is normalised and over-long
/// items are cut, nothing is rephrased or translated.
/// </summary>
public static partial class RequirementsExtractor
{
    public const int MaxItems = 4;
    public const int MaxItemLength = 160;

    private const string Ellipsis = "…";

    // Longer lines are text, not headings, even when bold.
    private const int MaxHeadingLength = 80;

    // Lowercase, with the straight apostrophe (see ClassifyHeading). A heading is a requirements heading when
    // it contains one of these phrases; a plain line without a colon only when it is one of them exactly, hence
    // the full wordings next to "looking for". Extend the list as sources show new wordings.
    private static readonly string[] RequirementsHeadings =
    [
        "requirements", "qualifications", "must have", "must-have", "we expect", "what we are looking for",
        "what we're looking for", "looking for", "required skills", "required experience", "what you need", "what we need from you",
        "what you'll bring", "what you will bring",
        "вимоги", "очікуємо", "що потрібно", "кого ми шукаємо", "необхідні навички",
    ];

    // Lowercase. Sections that end a requirements section when they appear as plain "Heading:" lines.
    // Bold lines and h1-h6 end it whatever they say. "обов" covers "обов'язки" with any apostrophe.
    private static readonly string[] OtherHeadings =
    [
        "responsibilities", "duties", "what you will do", "we offer", "what we offer", "benefits", "conditions",
        "about us", "about the company", "about the project", "nice to have", "обов", "пропонуємо", "умови",
        "про нас", "про компанію", "про проєкт", "буде плюсом", "завдання",
    ];

    private static readonly HashSet<string> HeadingTags = ["h1", "h2", "h3", "h4", "h5", "h6"];

    private static readonly HashSet<string> BlockTags =
    [
        "p", "div", "ul", "ol", "dl", "dt", "dd", "section", "article", "header", "footer", "main", "aside", "nav",
        "table", "thead", "tbody", "tr", "td", "th", "blockquote", "pre", "address", "hr",
        .. HeadingTags,
    ];

    public static IReadOnlyList<string> Extract(string? htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText))
        {
            return [];
        }

        var items = new List<string>();
        var inRequirements = false;
        foreach (var line in ReadLines(htmlOrText))
        {
            if (line.IsBullet)
            {
                if (!inRequirements)
                {
                    continue;
                }

                items.Add(Truncate(line.Text));
                if (items.Count == MaxItems)
                {
                    break;
                }
            }
            else
            {
                switch (ClassifyHeading(line))
                {
                    case HeadingKind.Requirements:
                        inRequirements = true;
                        break;
                    case HeadingKind.Other:
                        inRequirements = false;
                        break;
                }
            }
        }

        return items;
    }

    private static List<Line> ReadLines(string htmlOrText)
    {
        var document = new HtmlDocument();
        document.LoadHtml(htmlOrText);

        // In HTML, line breaks in the source are plain whitespace; in plain text they separate lines.
        var reader = new LineReader(newlinesSeparateLines: !HtmlTag().IsMatch(htmlOrText));
        reader.Visit(document.DocumentNode);

        return reader.Finish();
    }

    private static HeadingKind ClassifyHeading(Line line)
    {
        var title = line.Text.TrimEnd(':', ' ').ToLowerInvariant().Replace('\u2019', '\'');
        if (title.Length == 0 || title.Length > MaxHeadingLength)
        {
            return HeadingKind.None;
        }

        var kind = ContainsAny(title, RequirementsHeadings) ? HeadingKind.Requirements
            : ContainsAny(title, OtherHeadings) ? HeadingKind.Other
            : HeadingKind.None;

        if (line.IsEmphasised)
        {
            return kind == HeadingKind.None ? HeadingKind.Other : kind;
        }

        // A plain line is only a heading when it is a known section name on its own or followed by a
        // colon, so that "You should have:" or "Requirements are strict." stay ordinary text.
        var isSectionName = line.Text.EndsWith(':')
            || RequirementsHeadings.Contains(title)
            || OtherHeadings.Contains(title);

        return isSectionName ? kind : HeadingKind.None;
    }

    private static bool ContainsAny(string text, string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));

    private static string Truncate(string item)
    {
        if (item.Length <= MaxItemLength)
        {
            return item;
        }

        var keep = MaxItemLength - Ellipsis.Length;
        if (char.IsHighSurrogate(item[keep - 1]))
        {
            keep--;
        }

        return item[..keep].TrimEnd() + Ellipsis;
    }

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A bullet glyph, or a dash/asterisk followed by whitespace ("- item" but not "-5%" or "**bold**").
    [GeneratedRegex(@"^(?:[•·●▪‣◦○]\s*|[-–—*]\s+)")]
    private static partial Regex BulletMarker();

    private enum HeadingKind
    {
        None,
        Requirements,
        Other,
    }

    /// <param name="IsEmphasised">An h1-h6 line, or a line whose words are all bold.</param>
    private readonly record struct Line(string Text, bool IsBullet, bool IsEmphasised);

    /// <summary>Flattens a parsed document into text lines, remembering which are bullets and headings.</summary>
    private sealed class LineReader(bool newlinesSeparateLines)
    {
        private readonly List<Line> lines = [];
        private readonly StringBuilder text = new();
        private bool inBullet;
        private bool inHeadingTag;
        private int boldDepth;
        private int boldLetters;
        private int otherLetters;

        public List<Line> Finish()
        {
            EndLine();

            return lines;
        }

        public void Visit(HtmlNode node)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Text:
                    AddText(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
                    break;
                case HtmlNodeType.Document:
                    VisitChildren(node);
                    break;
                case HtmlNodeType.Element:
                    VisitElement(node);
                    break;
            }
        }

        private void VisitElement(HtmlNode element)
        {
            var name = element.Name.ToLowerInvariant();
            switch (name)
            {
                case "script" or "style":
                    return;
                case "br":
                    BreakLine();
                    return;
                case "li":
                    EndLine();
                    inBullet = true;
                    VisitChildren(element);
                    EndLine();
                    return;
            }

            var isBlock = BlockTags.Contains(name);
            var isBold = name is "b" or "strong";
            if (isBlock)
            {
                BreakLine();
            }

            inHeadingTag |= HeadingTags.Contains(name);
            if (isBold)
            {
                boldDepth++;
            }

            VisitChildren(element);
            if (isBold)
            {
                boldDepth--;
            }

            if (isBlock)
            {
                BreakLine();
            }
        }

        private void VisitChildren(HtmlNode node)
        {
            foreach (var child in node.ChildNodes)
            {
                Visit(child);
            }
        }

        private void AddText(string value)
        {
            var pieces = newlinesSeparateLines ? value.Split('\n') : [value];
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0)
                {
                    BreakLine();
                }

                text.Append(pieces[i]);
                if (boldDepth > 0)
                {
                    boldLetters += pieces[i].Count(char.IsLetterOrDigit);
                }
                else
                {
                    otherLetters += pieces[i].Count(char.IsLetterOrDigit);
                }
            }
        }

        // Inside a list item blocks and <br> only separate words: the whole item stays one line.
        private void BreakLine()
        {
            if (inBullet)
            {
                text.Append(' ');
            }
            else
            {
                EndLine();
            }
        }

        private void EndLine()
        {
            var value = Whitespace().Replace(text.ToString(), " ").Trim();
            var isBullet = inBullet;
            var isEmphasised = inHeadingTag || (boldLetters > 0 && otherLetters == 0);

            if (BulletMarker().Match(value) is { Success: true } marker)
            {
                value = value[marker.Length..].Trim();
                isBullet = true;
            }

            if (value.Length > 0)
            {
                lines.Add(new Line(value, isBullet, isEmphasised));
            }

            text.Clear();
            inBullet = false;
            inHeadingTag = false;
            boldLetters = 0;
            otherLetters = 0;
        }
    }
}
