using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Markdig;

namespace LGLauncher;

[Flags]
internal enum RssTextStyle { Normal = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Code = 16 }
internal enum RssBlockStyle { Paragraph, Heading, Quote, Code }
internal sealed record RssRun(string Text, RssTextStyle Style);
internal sealed record RssBlock(RssRun[]? Runs = null, RssBlockStyle Style = RssBlockStyle.Paragraph,
    string? Image = null, string? Alt = null);

internal sealed class RssMarkup
{
    private const int MaximumBlocks = 256;
    private const int MaximumImageCount = 12;
    private const int MaximumDepth = 64;
    private const int MaximumRuns = 4096;
    private const int MaximumTextLength = 160_000;
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    private const RegexOptions HtmlOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private static readonly Regex Tags = new(
        """<!--[\s\S]*?(?:-->|$)|<![^>]*>|<(?<end>/)?(?<tag>[A-Za-z][A-Za-z0-9:_.-]*)(?<attrs>(?:[^"'<>]|"[^"]*"|'[^']*')*)>""",
        HtmlOptions);
    private static readonly Regex Attributes = new(
        """(?<key>[^\s=/"'<>]+)(?:\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'=<>]+)))?""",
        HtmlOptions);
    private static readonly Regex Whitespace = new(@"[ \t\r\n\f]+", HtmlOptions);

    private readonly List<RssBlock> _blocks = [];
    private readonly List<RssRun> _runs = [];
    private readonly List<(string Tag, RssTextStyle Style)> _styles = [];
    private readonly Stack<int?> _lists = [];
    private RssBlockStyle _blockStyle;
    private int _quoteDepth;
    private int _imageCount;
    private int _runCount;
    private int _textLength;
    private bool _pendingSpace;
    private string? _listPrefix;

    private bool IsFull => _blocks.Count >= MaximumBlocks || _runCount >= MaximumRuns || _textLength >= MaximumTextLength;

    internal static RssBlock[] Read(XElement? element, bool html, Uri? origin)
    {
        if (element == null) return [];
        var markup = new RssMarkup();
        if (element.HasElements) markup.ReadNodes(element, origin, 0);
        else if (html) markup.ReadHtml(element.Value, origin);
        else markup.AppendText(element.Value, preserveWhitespace: true);
        markup.Flush();
        return markup._blocks.ToArray();
    }

    internal static RssBlock[] ReadMarkdown(string markdown)
    {
        var markup = new RssMarkup();
        markup.ReadHtml(Markdown.ToHtml(markdown, MarkdownPipeline), origin: null);
        markup.Flush();
        return markup._blocks.ToArray();
    }

    internal static string PlainText(IEnumerable<RssBlock> blocks) => String.Join("\n",
        blocks.Where(block => block.Runs != null).Select(block => String.Concat(block.Runs!.Select(run => run.Text))));

    private void ReadNodes(XElement element, Uri? origin, int depth)
    {
        if (depth == MaximumDepth) return;
        foreach (var node in element.Nodes())
        {
            if (IsFull) return;
            if (node is XText text) AppendText(text.Value);
            else if (node is XElement child)
            {
                var tag = child.Name.LocalName.ToLowerInvariant();
                if (IsHidden(tag)) continue;
                var relative = (string?)child.Attribute(XNamespace.Xml + "base");
                var childOrigin = RssReader.ResolveWebAddress(relative, origin) ?? origin;
                if (tag == "img") AddImage((string?)child.Attribute("src"), (string?)child.Attribute("alt"), childOrigin);
                else
                {
                    Open(tag);
                    ReadNodes(child, childOrigin, depth + 1);
                    Close(tag);
                }
            }
        }
    }

    private void ReadHtml(string html, Uri? origin)
    {
        var offset = 0;
        var hidden = new Stack<string>();
        foreach (Match match in Tags.Matches(html))
        {
            if (IsFull) return;
            if (hidden.Count == 0) AppendText(WebUtility.HtmlDecode(html[offset..match.Index]));
            offset = match.Index + match.Length;
            if (!match.Groups["tag"].Success) continue;
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var closing = match.Groups["end"].Success;
            if (hidden.TryPeek(out var container))
            {
                if (closing && tag == container) hidden.Pop();
                else if (!closing && container is "template" or "object" or "svg" or "math" && IsHiddenOpening(tag, match))
                {
                    if (hidden.Count == MaximumDepth) return;
                    hidden.Push(tag);
                }
                continue;
            }
            if (closing) Close(tag);
            else if (IsHiddenOpening(tag, match)) hidden.Push(tag);
            else if (tag == "img")
            {
                string? source = null, alt = null;
                foreach (Match attribute in Attributes.Matches(match.Groups["attrs"].Value))
                {
                    var value = WebUtility.HtmlDecode(attribute.Groups["double"].Value +
                        attribute.Groups["single"].Value + attribute.Groups["bare"].Value);
                    switch (attribute.Groups["key"].Value.ToLowerInvariant())
                    {
                        case "src": source ??= value; break;
                        case "alt": alt ??= value; break;
                    }
                }
                AddImage(source, alt, origin);
            }
            else Open(tag);
        }
        if (hidden.Count == 0) AppendText(WebUtility.HtmlDecode(html[offset..]), preserveWhitespace: offset == 0);
    }

    private static bool IsHidden(string tag) =>
        tag is "script" or "style" or "template" or "iframe" or "object" or "embed" or "svg" or "math" or "textarea";

    private static bool IsHiddenOpening(string tag, Match match) =>
        tag != "embed" && IsHidden(tag) && !match.Groups["attrs"].Value.TrimEnd().EndsWith('/');

    private static RssTextStyle InlineStyle(string tag) => tag switch
    {
        "b" or "strong" => RssTextStyle.Bold,
        "i" or "em" or "cite" => RssTextStyle.Italic,
        "u" or "ins" => RssTextStyle.Underline,
        "s" or "strike" or "del" => RssTextStyle.Strike,
        "code" or "kbd" or "samp" => RssTextStyle.Code,
        _ => RssTextStyle.Normal
    };

    private static bool IsHeading(string tag) => tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    private static bool IsParagraph(string tag) =>
        tag is "p" or "div" or "section" or "article" or "header" or "footer" or "figcaption" or "figure" or "li" or "tr";

    private void Open(string tag)
    {
        if (tag == "br")
        {
            _pendingSpace = false;
            AppendRun("\n", RssTextStyle.Normal);
            return;
        }
        if (IsParagraph(tag) || IsHeading(tag) || tag is "blockquote" or "pre" or "hr" or "ol" or "ul") Flush();
        if (IsHeading(tag)) _blockStyle = RssBlockStyle.Heading;
        if (tag == "pre") _blockStyle = RssBlockStyle.Code;
        if (tag == "blockquote") _quoteDepth++;
        if (tag is "ol" or "ul" && _lists.Count < MaximumDepth) _lists.Push(tag == "ol" ? 1 : null);
        if (tag == "li")
        {
            var prefix = "• ";
            if (_lists.TryPeek(out var number) && number.HasValue)
            {
                prefix = number.Value.ToString(CultureInfo.CurrentCulture) + ". ";
                _lists.Pop();
                _lists.Push(number.Value + 1);
            }
            _listPrefix = prefix;
        }
        var style = InlineStyle(tag);
        if (style != RssTextStyle.Normal && _styles.Count < MaximumDepth) _styles.Add((tag, style));
    }

    private void Close(string tag)
    {
        if (IsParagraph(tag) || IsHeading(tag) || tag is "blockquote" or "pre" or "ol" or "ul") Flush();
        if (IsHeading(tag) || tag == "pre") _blockStyle = RssBlockStyle.Paragraph;
        if (tag == "blockquote" && _quoteDepth > 0) _quoteDepth--;
        if (tag == "li") _listPrefix = null;
        if (tag is "ol" or "ul") _lists.TryPop(out _);
        if (InlineStyle(tag) == RssTextStyle.Normal) return;
        var index = _styles.FindLastIndex(entry => entry.Tag == tag);
        if (index >= 0) _styles.RemoveAt(index);
    }

    private void AppendText(string text, bool preserveWhitespace = false)
    {
        if (text.Length == 0 || IsFull) return;
        var style = RssTextStyle.Normal;
        foreach (var entry in _styles) style |= entry.Style;
        if (preserveWhitespace || _blockStyle == RssBlockStyle.Code)
        {
            if (!String.IsNullOrWhiteSpace(text)) AppendListPrefix();
            AppendRun(preserveWhitespace && _blockStyle != RssBlockStyle.Code ? text.Trim() : text, style);
            return;
        }
        var normalized = Whitespace.Replace(text, " ");
        var trimmed = normalized.Trim(' ');
        if (trimmed.Length != 0)
        {
            AppendListPrefix();
            if ((_pendingSpace || normalized[0] == ' ') && _runs.Count > 0 &&
                _runs[^1].Text[^1] is not ('\n' or ' '))
                AppendRun(" ", _runs[^1].Style);
            AppendRun(trimmed, style);
        }
        _pendingSpace = normalized[^1] == ' ';
    }

    private void AppendRun(string text, RssTextStyle style)
    {
        var length = Math.Min(text.Length, MaximumTextLength - _textLength);
        if (length <= 0 || IsFull) return;
        _runCount++;
        _textLength += length;
        _runs.Add(new RssRun(text[..length], style));
    }

    private void AppendListPrefix()
    {
        if (_listPrefix == null) return;
        AppendRun(_listPrefix, RssTextStyle.Normal);
        _listPrefix = null;
    }

    private void Flush()
    {
        if (_runs.Any(run => !String.IsNullOrWhiteSpace(run.Text)))
            _blocks.Add(new RssBlock(_runs.ToArray(), _blockStyle == RssBlockStyle.Paragraph && _quoteDepth > 0
                ? RssBlockStyle.Quote : _blockStyle));
        _runs.Clear();
        _pendingSpace = false;
    }

    private void AddImage(string? source, string? alt, Uri? origin)
    {
        if (_imageCount >= MaximumImageCount || source?.Length > 4096 ||
            RssReader.ResolveWebAddress(source, origin) is not { } address) return;
        Flush();
        if (IsFull) return;
        _imageCount++;
        alt = alt?.Trim();
        _blocks.Add(new RssBlock(Image: address.AbsoluteUri, Alt: alt?.Length > 300 ? alt[..300] : alt));
    }
}
