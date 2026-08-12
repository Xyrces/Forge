using Markdig;

namespace Forge.Dashboard.Services;

/// <summary>
/// Markdown → HTML for chat-style surfaces (intake thread bubbles).
/// Raw HTML is DISABLED — the content is model output, and the
/// operator's dashboard renders it as MarkupString; letting the model
/// smuggle script/iframe tags into the page would be an injection
/// hole. Advanced extensions (tables, task lists, strikethrough,
/// fenced code) stay on.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
        => string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : Markdown.ToHtml(markdown, Pipeline);
}
