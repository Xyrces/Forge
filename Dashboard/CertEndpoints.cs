using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace Forge.Dashboard;

/// <summary>
/// Serves the dashboard's public TLS certificate for download
/// (PEM format, file extension .crt). Used by the first-time
/// install flow: operators hit GET /cert, save the file, then
/// install it as a trusted root CA on their workstation +
/// anything else that needs to call the API (CI runners, agents
/// on other machines, the e2e harness, ...).
///
/// <para>
/// The endpoint is intentionally unauthenticated — the cert is
/// not a secret. It's also served over both HTTP and HTTPS
/// (HTTPS requires the cert to already be trusted, which
/// defeats the purpose of this download).
/// </para>
/// </summary>
public static class CertEndpoints
{
    public static IEndpointRouteBuilder MapCertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/cert");

        group.MapGet("/", ServeCertAsync)
             .WithName("DownloadCert")
             .WithSummary("Download the dashboard's public TLS certificate (PEM). Use this to install the cert as a trusted root on machines that need to talk to the dashboard.");

        group.MapGet("/install", ServeInstallHelperAsync)
             .WithName("CertInstallHelper")
             .WithSummary("Paste-safe per-OS install one-liners (Linux / macOS / Windows), host-aware.");

        return endpoints;
    }

    private static IResult ServeCertAsync(HttpContext ctx, IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "certs", "forge.crt");
        if (!File.Exists(path))
        {
            // Fall back to the user's home-dir cert (the dev path
            // we use for the user-mode systemd install).
            var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "forge", "certs", "forge.crt");
            if (File.Exists(alt)) path = alt;
            else return Results.NotFound(new { error = "cert not found; was the dashboard installed?" });
        }

        var bytes = File.ReadAllBytes(path);
        ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"forge.crt\"";
        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.File(bytes, "application/x-pem-file");
    }

    private static IResult ServeInstallHelperAsync(HttpContext ctx)
    {
        // Emit paste-safe one-liners (not a multi-line script):
        // multi-line pastes into bash break on the sudo password
        // prompt — the prompt swallows the remaining pasted lines.
        // One line = one paste = sudo prompts cleanly.
        //
        // The cert URL is built from the request's own scheme+host
        // so the commands work from any machine that can reach the
        // dashboard, without hardcoding an IP.
        var certUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/cert/";

        var text = $"""
            # Forge cert install — copy the ONE line for your OS and paste it
            # into a terminal. It downloads the cert (with -k, since the cert
            # is self-signed and not yet trusted) and installs it as a
            # trusted root.

            # Linux — system trust store (Debian/Ubuntu):
            curl -fsSLk {certUrl} | sudo tee /usr/local/share/ca-certificates/forge.crt >/dev/null && sudo update-ca-certificates

            # Linux — Chrome/Chromium browser store (Chrome does NOT use the
            # system store; run this in addition):
            curl -fsSLk {certUrl} -o /tmp/forge.crt && certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n forge -i /tmp/forge.crt

            # Linux — Arch:
            curl -fsSLk {certUrl} | sudo tee /etc/ca-certificates/trust-source/anchors/forge.crt >/dev/null && sudo trust extract-compat

            # Linux — Fedora/RHEL:
            curl -fsSLk {certUrl} | sudo tee /etc/pki/ca-trust/source/anchors/forge.crt >/dev/null && sudo update-ca-trust

            # macOS:
            curl -fsSLk {certUrl} -o /tmp/forge.crt && sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain /tmp/forge.crt

            # Windows — PowerShell (run as Administrator):
            curl.exe -fsSLk {certUrl} -o forge.crt; Import-Certificate -FilePath .\forge.crt -CertStoreLocation Cert:\LocalMachine\Root

            # Afterwards: restart your browser.
            """;
        ctx.Response.Headers["Content-Disposition"] = "inline; filename=\"install-forge-cert.txt\"";
        return Results.Content(text, "text/plain");
    }
}
