using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

/// <summary>Private operator API protection; browser circuits still require a trusted operator network.</summary>
public sealed class DashboardMutationAuthentication
{
    internal const string AuthenticatedOperatorItem = "Forge.AuthenticatedOperator";
    private readonly string _token;
    private readonly byte[] _hash;

    public DashboardMutationAuthentication(string token)
    {
        _token = token.Trim();
        if (_token.Length < 32) throw new InvalidOperationException("Dashboard mutation token must contain at least 32 characters.");
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + _token));
    }

    internal static bool RequiresAuthentication(string method, PathString path)
        => path.StartsWithSegments("/api") && !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    public void Protect(WebApplication app) => app.Use(async (context, next) =>
    {
        if (RequiresAuthentication(context.Request.Method, context.Request.Path))
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(_hash, supplied))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items[AuthenticatedOperatorItem] = "authenticated shared dashboard operator";
        }
        await next();
    });

    public DelegatingHandler CreateHandler(Uri localBaseAddress) => new MutationTokenHandler(_token, localBaseAddress);

    private sealed class MutationTokenHandler(string token, Uri localBaseAddress) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Never disclose the token to an absolute external URL or a non-API endpoint.
            if (request.RequestUri is { IsAbsoluteUri: true } uri
                && uri.Scheme == localBaseAddress.Scheme && uri.Host == localBaseAddress.Host && uri.Port == localBaseAddress.Port
                && RequiresAuthentication(request.Method.Method, new PathString(uri.AbsolutePath)))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, ct);
        }
    }
}
