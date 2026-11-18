using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace Forge.Dashboard.Services;

/// <summary>Shared renderer for untrusted recorded prose. Only passive formatting is enabled.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        using var output = new StringWriter();
        var renderer = new HtmlRenderer(output);
        Pipeline.Setup(renderer);
        // Handle both Markdown links and angle-bracket autolinks. Generic attributes,
        // media embeds and other advanced extensions are deliberately not enabled.
        renderer.ObjectRenderers.Insert(0, new SafeLinkRenderer());
        renderer.ObjectRenderers.Insert(0, new SafeAutolinkRenderer());
        renderer.Render(Markdown.Parse(markdown, Pipeline));
        return output.ToString();
    }

    private static bool IsSafeDestination(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
            return false;
        if (url.StartsWith("//", StringComparison.Ordinal)) return false;
        if (url.StartsWith("/", StringComparison.Ordinal)) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "https" or "http" or "mailto";
        // Relative repository/document links and local fragments remain usable.
        return !url.Contains(':') && Uri.TryCreate(url, UriKind.Relative, out _);
    }

    private static void OpenLink(HtmlRenderer renderer, string url, string? title = null)
    {
        renderer.Write("<a href=\"").WriteEscapeUrl(url).Write('"');
        if (!string.IsNullOrEmpty(title)) renderer.Write(" title=\"").WriteEscape(title).Write('"');
        renderer.Write('>');
    }

    private sealed class SafeLinkRenderer : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
            var safe = IsSafeDestination(url);
            if (safe) OpenLink(renderer, url!, link.Title);
            // Images are explicit links instead of automatic remote requests. Keep their alt text.
            if (link.IsImage) renderer.Write("Image: ");
            renderer.WriteChildren(link);
            if (safe) renderer.Write("</a>");
        }
    }

    private sealed class SafeAutolinkRenderer : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link)
        {
            var url = link.IsEmail ? "mailto:" + link.Url : link.Url;
            var safe = IsSafeDestination(url);
            if (safe) OpenLink(renderer, url);
            renderer.WriteEscape(link.Url);
            if (safe) renderer.Write("</a>");
        }
    }
}
