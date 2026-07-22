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
             .WithSummary("Per-OS install commands (one line each) for Linux, macOS, and Windows.");

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
        // Hand back a tiny shell-script the operator can pipe to
        // bash on macOS / Linux. Windows users just download the
        // .crt and double-click it.
        var text = """
            #!/usr/bin/env bash
            # forge cert installer — copy this output into a terminal as root.
            # Detects the OS + runs the right command.

            set -euo pipefail

            URL=${FORGE_URL:-https://192.168.68.78/cert/}
            TMP=$(mktemp)
            trap 'rm -f "$TMP"' EXIT

            echo "Downloading certificate from $URL ..."
            # -k because we're fetching our own self-signed cert.
            # The trust-the-cert step below is the actual fix.
            if command -v curl >/dev/null 2>&1; then
                curl -fsSLk "$URL" -o "$TMP"
            else
                wget --no-check-certificate -qO "$TMP" "$URL"
            fi

            if [ "$(uname -s)" = "Darwin" ]; then
                echo "macOS detected — adding to System keychain (requires sudo)..."
                sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$TMP"
                echo "Done. Safari / Chrome will now trust 192.168.68.78 immediately."
            elif [ "$(uname -s)" = "Linux" ]; then
                if [ -d /usr/local/share/ca-certificates ]; then
                    echo "Linux (Debian/Ubuntu) detected — installing to /usr/local/share/ca-certificates/..."
                    sudo cp "$TMP" /usr/local/share/ca-certificates/forge.crt
                    sudo update-ca-certificates
                elif [ -d /etc/ca-certificates/trust-source/anchors ]; then
                    echo "Linux (Arch) detected — installing to /etc/ca-certificates/trust-source/anchors/..."
                    sudo cp "$TMP" /etc/ca-certificates/trust-source/anchors/forge.crt
                    sudo trust extract-compat
                elif [ -d /etc/pki/ca-trust/source/anchors ]; then
                    echo "Linux (Fedora/RHEL) detected — installing to /etc/pki/ca-trust/source/anchors/..."
                    sudo cp "$TMP" /etc/pki/ca-trust/source/anchors/forge.crt
                    sudo update-ca-trust
                else
                    echo "Linux distribution not auto-detected. Install the cert manually:"
                    echo "  sudo cp $TMP /your/cert/dir/forge.crt"
                    echo "  sudo update-ca-certificates   # or the equivalent for your distro"
                    exit 1
                fi
                echo "Done. Restart your browser to pick up the new trust store."
            else
                echo "Unsupported OS: $(uname -s). Install manually:"
                echo "  Download: $URL"
                echo "  Save as forge.crt, double-click, 'Install Certificate' -> Trusted Root Certification Authorities"
                exit 1
            fi
            """;
        ctx.Response.Headers["Content-Disposition"] = "inline; filename=\"install-forge-cert.sh\"";
        return Results.Content(text, "text/x-shellscript");
    }
}
