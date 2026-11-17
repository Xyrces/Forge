using System.Net;
using System.Net.Http.Headers;
using Forge.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Forge.Tests;
public sealed class DashboardMutationAuthenticationTests
{
    private const string Token="offline-test-token-that-is-at-least-32-characters";
    [Fact]
    public async Task EveryApiMutationRequiresTokenWhileReadsAndBrowserCircuitRemainUnchanged()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();new DashboardMutationAuthentication(Token).Protect(app);
        var writes=0;
        app.MapMethods("/api/{**path}",["GET","POST","PUT","PATCH","DELETE"],(HttpContext ctx)=>{if(ctx.Request.Method!="GET")writes++;return Results.Ok();});
        app.MapPost("/_blazor/negotiate",()=>Results.Ok());await app.StartAsync();
        var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client=new HttpClient{BaseAddress=new Uri(url)};
        foreach(var method in new[]{HttpMethod.Post,HttpMethod.Put,HttpMethod.Patch,HttpMethod.Delete})
        foreach(var route in new[]{"/api/gates/sprint/release","/api/intake/proposals/p/approve","/api/deployments/d/approve"})
        {
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(new(method,route))).StatusCode);
            using var bad=new HttpRequestMessage(method,route);bad.Headers.Authorization=new("Bearer","wrong");
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(bad)).StatusCode);
            using var allowed=new HttpRequestMessage(method,route);allowed.Headers.Authorization=new("Bearer",Token);
            Assert.Equal(HttpStatusCode.OK,(await client.SendAsync(allowed)).StatusCode);
        }
        Assert.Equal(12,writes);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/_blazor/negotiate",null)).StatusCode);
    }
    [Fact]
    public async Task ServerClientOnlySendsTokenToSameOriginApiMutations()
    {
        var capture=new Capture();var handler=new DashboardMutationAuthentication(Token).CreateHandler(new("http://localhost:4197"));handler.InnerHandler=capture;
        using var client=new HttpClient(handler);
        foreach(var (url,method,expected) in new[]{
            ("http://localhost:4197/api/intake/proposals/p/approve",HttpMethod.Post,true),
            ("http://localhost:4197/api/gates/sprint/release",HttpMethod.Post,true),
            ("http://localhost:4197/api/events",HttpMethod.Get,false),
            ("https://example.com/api/intake",HttpMethod.Post,false),
            ("http://localhost:4198/api/intake",HttpMethod.Post,false),
            ("http://localhost:4197/not-api",HttpMethod.Post,false)})
        {await client.SendAsync(new(method,url));Assert.Equal(expected?"Bearer "+Token:null,capture.Header);}
    }
    [Fact]
    public void ShortTokenFailsClosed()=>Assert.Throws<InvalidOperationException>(()=>new DashboardMutationAuthentication("short"));
    [Fact]
    public async Task HostHeaderCannotRetargetTokenAndCrossOriginRedirectStripsIt()
    {
        var capture=new Capture();var scoped=new DashboardMutationAuthentication(Token).CreateHandler(new("http://localhost:4197"));scoped.InnerHandler=capture;
        using(var client=new HttpClient(scoped))
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"https://example.com/api/change");
            request.Headers.Host="localhost:4197";
            await client.SendAsync(request);Assert.Null(capture.Header);
        }
        var targetBuilder=WebApplication.CreateBuilder();targetBuilder.Logging.ClearProviders();targetBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var target=targetBuilder.Build();string? received="not-called";
        target.MapPost("/api/change",(HttpContext ctx)=>{received=ctx.Request.Headers.Authorization.FirstOrDefault();return Results.Ok();});
        await target.StartAsync();
        var targetUrl=target.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var sourceBuilder=WebApplication.CreateBuilder();sourceBuilder.Logging.ClearProviders();sourceBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var source=sourceBuilder.Build();string? sent=null;
        source.MapPost("/api/change",(HttpContext ctx)=>{sent=ctx.Request.Headers.Authorization.FirstOrDefault();return Results.Redirect(targetUrl+"/api/change",preserveMethod:true);});
        await source.StartAsync();
        var sourceUrl=source.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var handler=new DashboardMutationAuthentication(Token).CreateHandler(new(sourceUrl));handler.InnerHandler=new HttpClientHandler();
        using var redirecting=new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK,(await redirecting.PostAsync(sourceUrl+"/api/change",null)).StatusCode);
        Assert.Equal("Bearer "+Token,sent);Assert.Null(received);
    }
    [Fact]
    public async Task RegisteredServerClientsReceiveProtectionWithoutBrowserToken()
    {
        var services=new ServiceCollection();services.AddLogging();
        var local=new Uri("http://localhost:4197");
        services.AddForgeUI(local,local,new DashboardMutationAuthentication(Token));
        var captures=new Dictionary<string,Capture>();
        foreach(var name in new[]{"ForgeApi","ForgeApiLongPoll",typeof(Forge.Dashboard.Features.Deployments.DeploymentsClient).FullName!})
        {
            var capture=new Capture();captures[name]=capture;
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(()=>capture);
        }
        using var provider=services.BuildServiceProvider();
        var factory=provider.GetRequiredService<IHttpClientFactory>();
        foreach(var (name,capture) in captures)
        {
            using var client=factory.CreateClient(name);
            await client.PostAsync(new Uri(local,"/api/gates/sprint/release"),null);
            Assert.Equal("Bearer "+Token,capture.Header);
        }
    }
    private sealed class Capture:HttpMessageHandler
    {public string? Header;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Header=request.Headers.Authorization?.ToString();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));}}
}
