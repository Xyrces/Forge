using Forge.Dashboard.Components;
using Forge.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.Tests;

public sealed class MarkdownRenderingSafetyTests
{
    [Theory]
    [InlineData("[Inert](unapproved-demo:note)")]
    [InlineData("[Inert][ref]\n\n[ref]: unapproved-demo:note")]
    [InlineData("<unapproved-demo:note>")]
    [InlineData("[Inert](//example.invalid/content)")]
    public void DisallowedDestinationsNeverBecomeNavigable(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown);
        Assert.DoesNotContain("href=", html);
        Assert.DoesNotContain("src=", html);
        Assert.NotEmpty(html);
    }

    [Theory]
    [InlineData("https://github.com/Xyrces/Forge/pull/150")]
    [InlineData("http://example.invalid/readme")]
    [InlineData("mailto:reviewer@example.invalid")]
    [InlineData("/tasks/example?project=forge")]
    [InlineData("../docs/plan.md")]
    public void OrdinarySafeLinksRemainLinks(string destination)
    {
        var html = MarkdownRenderer.ToHtml($"[Reference]({destination})");
        Assert.Contains($"href=\"{destination}\"", html);
    }

    [Fact]
    public void AutolinksAndInertTitlesAreEncodedWithoutActivatingAttributesOrEmbeds()
    {
        var html = MarkdownRenderer.ToHtml("""
            <https://github.com/Xyrces/Forge/pull/150>

            [Reference](https://example.invalid/note 'A "quoted" title & notes'){data-demo="inert"}

            https://www.youtube.com/watch?v=example
            """);
        Assert.Contains("href=\"https://github.com/Xyrces/Forge/pull/150\"", html);
        Assert.Contains("title=\"A &quot;quoted&quot; title &amp; notes\"", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<video", html);
        Assert.DoesNotContain("<audio", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
    }

    [Fact]
    public void RawMarkupAndGenericAttributesStayInertAndImagesRequireExplicitNavigation()
    {
        var html = MarkdownRenderer.ToHtml("""
            <span data-demo="inert">Recorded prose</span>

            [PR](https://github.com/Xyrces/Forge/pull/150){#custom .external data-demo="inert"}

            ![Reference art](https://example.invalid/art.png)
            """);
        Assert.DoesNotContain("<span", html);
        Assert.DoesNotContain("id=\"custom\"", html);
        Assert.DoesNotContain("class=\"external\"", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
        Assert.Contains("Image: Reference art", html);
        Assert.Contains("href=\"https://example.invalid/art.png\"", html);
    }

    [Fact]
    public async Task RecordedReviewComponentPreservesSafeStructureCodeAndPullRequestLinks()
    {
        const string markdown = """
            ## Review

            **Verified** and ~~superseded~~. [PR #150](https://github.com/Xyrces/Forge/pull/150 "Original review")

            - [x] Checked
            - [ ] Follow-up

            | Check | Result |
            | --- | --- |
            | Build | Passed |

            ```text
            <sample data-demo="inert">
            ```

            [Local section](#review) and [Plan](docs/plan.md).
            """;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<RecordedMarkdown>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(RecordedMarkdown.Text)] = markdown }));
            return component.ToHtmlString();
        });
        Assert.Contains("<h2>Review</h2>", html);
        Assert.Contains("<strong>Verified</strong>", html);
        Assert.Contains("<del>superseded</del>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("disabled", html);
        Assert.Contains("<pre><code", html);
        Assert.Contains("&lt;sample", html);
        Assert.DoesNotContain("<sample", html);
        Assert.Contains("href=\"https://github.com/Xyrces/Forge/pull/150\"", html);
        Assert.Contains("title=\"Original review\"", html);
        Assert.Contains("href=\"#review\"", html);
        Assert.Contains("href=\"docs/plan.md\"", html);
    }
}
