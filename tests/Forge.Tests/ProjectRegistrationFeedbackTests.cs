using System.Net;
using Forge.Dashboard.Features.Projects;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectRegistrationFeedbackTests
{
    [Theory]
    [InlineData("application/problem+json", "{\"detail\":\"Storage unavailable\",\"error\":\"Less specific\"}", "Storage unavailable")]
    [InlineData("application/json", "{\"error\":\"Retry provisioning\"}", "Retry provisioning")]
    [InlineData("text/plain", " Please retry later. ", "Please retry later.")]
    [InlineData("text/html", "<p>Proxy unavailable</p>", "Project registration failed (HTTP 503). Please retry.")]
    [InlineData("application/json", "{\"unexpected\":true}", "Project registration failed (HTTP 503). Please retry.")]
    public async Task DisplaysUsefulReasonWithoutRawProtocolBody(string mediaType, string body, string expected)
    {
        using var handler = new FailureHandler(mediaType, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new ProjectsClient(http);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AddAsync(
            new AddProjectRequestBody("second", "Second", "https://github.com/example/second.git", "main"), default));
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FailureHandler(string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            });
    }
}
