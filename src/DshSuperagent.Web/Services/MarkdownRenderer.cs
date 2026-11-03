using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace DshSuperagent.Web.Services;

/// <summary>
/// A deliberately small Markdown renderer for agent output. Everything is HTML-encoded first, so
/// repository text cannot inject markup. Raw HTML is never passed through.
/// </summary>
public static partial class MarkdownRenderer
{
    public static IHtmlContent Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return HtmlString.Empty;
        }

        var encoded = HtmlEncoder.Default.Encode(markdown.Replace("\r\n", "\n"));
        var builder = new StringBuilder();
        var inCode = false;
        var lines = encoded.Split('\n');
        var listOpen = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (listOpen)
                {
                    builder.AppendLine("</ul>");
                    listOpen = false;
                }

                builder.AppendLine(inCode ? "</code></pre>" : "<pre><code>");
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                builder.AppendLine(line);
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                builder.Append("<h3>").Append(Inline(line[4..])).AppendLine("</h3>");
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                builder.Append("<h2>").Append(Inline(line[3..])).AppendLine("</h2>");
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                builder.Append("<h1>").Append(Inline(line[2..])).AppendLine("</h1>");
            }
            else if (line.StartsWith("&gt; ", StringComparison.Ordinal))
            {
                builder.Append("<blockquote>").Append(Inline(line[5..])).AppendLine("</blockquote>");
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!listOpen)
                {
                    builder.AppendLine("<ul>");
                    listOpen = true;
                }

                builder.Append("<li>").Append(Inline(line[2..])).AppendLine("</li>");
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                if (listOpen)
                {
                    builder.AppendLine("</ul>");
                    listOpen = false;
                }

                builder.AppendLine();
            }
            else
            {
                if (listOpen)
                {
                    builder.AppendLine("</ul>");
                    listOpen = false;
                }

                builder.Append("<p>").Append(Inline(line)).AppendLine("</p>");
            }
        }

        if (listOpen)
        {
            builder.AppendLine("</ul>");
        }

        if (inCode)
        {
            builder.AppendLine("</code></pre>");
        }

        return new HtmlString($"<div class=\"markdown\">{builder}</div>");
    }

    private static string Inline(string value)
    {
        var result = CodePattern().Replace(value, match => $"<code>{match.Groups[1].Value}</code>");
        result = BoldPattern().Replace(result, match => $"<strong>{match.Groups[1].Value}</strong>");
        result = LinkPattern().Replace(
            result,
            match => $"<a href=\"{match.Groups[2].Value}\" target=\"_blank\" rel=\"noopener noreferrer\">{match.Groups[1].Value}</a>");
        return result;
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkPattern();
}
