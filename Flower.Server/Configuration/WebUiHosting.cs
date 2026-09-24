using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Configuration;

// Serves the browser UI - the published output of Flower.Web, an Avalonia
// WebAssembly build of the same Views and ViewModels the desktop app runs.
//
// Hosting it is optional on purpose. Flower.Web cannot be built without the
// wasm-tools workload's Emscripten toolchain (WasmBuildNative=true; without it
// Avalonia's Skia renderer throws DllNotFoundException on first paint), so making
// it a project reference would mean this server - the one head that has to build
// on a headless box or in a minimal container image - could no longer be compiled
// without a browser toolchain installed. Instead Flower.Server.csproj builds the
// bundle into $(OutDir)wwwroot when that toolchain is present and skips it when
// it is not, and this serves whatever it finds; a server without one answers with
// a short page saying so, which is a far better failure than a 404 on the address
// a client's "Server Settings..." button just opened.
public static class WebUiHosting
{
    // Where it comes from, in order of how it got there: an explicitly
    // configured path; the copy Flower.Server.csproj makes next to the binary on
    // every build (and every publish, so `dotnet publish Flower.Server` is a
    // complete deployment on its own); or the data directory, for a container
    // that mounts data but bakes the binary.
    // A configured Flower:WebUiPath is authoritative: if it is set, it is the
    // only place looked at, and a bundle that isn't there means none is
    // deployed. Falling through to the conventional locations instead would
    // quietly serve some *other* bundle than the one the operator named -
    // which, for a path they pointed at deliberately, is worse than the
    // not-deployed page.
    public static string? Resolve(IWebHostEnvironment environment, FlowerServerOptions options)
    {
        foreach (var candidate in Candidates(environment, options))
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> Candidates(IWebHostEnvironment environment, FlowerServerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WebUiPath))
        {
            yield return options.WebUiPath;
            yield break;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "wwwroot");
        yield return Path.Combine(environment.ContentRootPath, "wwwroot");
        yield return Path.Combine(options.DataDirectory, "wwwroot");
    }

    public static void MapWebUi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
        var root = Resolve(app.Environment, options);

        if (root != null)
        {
            app.Logger.LogInformation("Serving the web UI from: {WebUiPath}", root);

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(root),
                ContentTypeProvider = ContentTypes(),
                // The bundle is versioned by whatever build produced it and has
                // no cache-busting in its own filenames, so a stale .wasm served
                // after an upgrade is a real hazard. The assets are local-network
                // sized and this is not a public CDN, so revalidating is cheap.
                OnPrepareResponse = context =>
                    context.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        else
        {
            app.Logger.LogInformation(
                "No web UI bundle found - this server was built without the wasm-tools workload, "
                + "or with -p:IncludeWebUi=false. Install the workload and rebuild, or set "
                + "Flower:WebUiPath. Checked: {Candidates}",
                string.Join(", ", Candidates(app.Environment, options).Where(c => !string.IsNullOrWhiteSpace(c))));
        }

        // Single-page fallback: the browser UI reads its route out of the URL
        // fragment (see Flower.Web), so in practice only "/" is ever requested,
        // but a refresh on any other path must not 404 into nothing. Scoped away
        // from the API surface so a mistyped /api/... still fails as an API call
        // rather than silently returning HTML that a client will try to parse as
        // JSON.
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (root != null && File.Exists(Path.Combine(root, "index.html")))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(Path.Combine(root, "index.html"));
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(NotDeployedPage);
        });
    }

    // The .NET WebAssembly bundle ships several extensions ASP.NET Core's default
    // provider has never heard of, and a wrong (or missing) content type on any of
    // them is a blank page rather than a diagnosable error - the runtime refuses
    // to instantiate a .wasm served as application/octet-stream.
    private static FileExtensionContentTypeProvider ContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".blat"] = "application/octet-stream";
        provider.Mappings[".dat"] = "application/octet-stream";
        provider.Mappings[".dll"] = "application/octet-stream";
        provider.Mappings[".pdb"] = "application/octet-stream";
        provider.Mappings[".br"] = "application/octet-stream";
        provider.Mappings[".webcil"] = "application/octet-stream";
        return provider;
    }

    private const string NotDeployedPage = """
        <!doctype html>
        <meta charset="utf-8">
        <title>Flower Server</title>
        <style>
          body { font: 15px/1.6 system-ui, sans-serif; margin: 12vh auto; max-width: 34rem; padding: 0 1.5rem; }
          code { background: rgba(127,127,127,.15); padding: .1em .35em; border-radius: .25em; }
        </style>
        <h1>Flower Server</h1>
        <p>This server is running, but no web UI was built with it.</p>
        <p>The bundle is built automatically alongside the server when the
        WebAssembly toolchain is installed:</p>
        <pre><code>dotnet workload install wasm-tools</code></pre>
        <p>Then rebuild the server. Or point <code>Flower:WebUiPath</code> at an
        existing bundle. The API is unaffected either way.</p>
        """;

    // How a pairing code reaches a browser tab: in the URL fragment, which -
    // unlike a query string - is never sent to the server as part of the
    // request, so it cannot land in an access log or a Referer header. The tab
    // reads it once, redeems it for a trusted-peer record against its own
    // WebCrypto keypair, and erases it from the address bar (see
    // BrowserPeerCredentials and weblocation.js).
    //
    // A code, not a session token. What used to travel here was a 60-minute
    // full-admin bearer credential, live for its whole lifetime wherever the URL
    // ended up; this is single-use and spent within a second of the page
    // loading. That is the whole of docs/OPEN-INTERNET-REVIEW.md finding 7.
    //
    // page=settings is separate from the code because pairing and administering
    // are different things: an ordinary listener pairs a tab and gets a jukebox,
    // and only a link that says so opens the settings overlay.
    // Where a person should open the web UI, best first: the origin an operator
    // advertised, then this server's own TLS listener at each address the
    // machine holds, then plain http on this machine.
    //
    // Only https ones from anywhere but here, because of the same browser rule
    // BrowserOriginFor works around: crypto.subtle - which a tab needs to hold
    // a device key and pair - exists only in a secure context. http://localhost
    // is one; http://192.168.x.y is not, and a tab opened there cannot pair.
    // The TLS listener's own certificate names every one of these addresses
    // (ServerTls), so a browser warns once that it does not know the issuer
    // and, past that, is a secure context like any other.
    //
    // On a headless server, which is most of them, the localhost entry is the
    // one nobody can open - it is last for that reason, and still there for
    // the operator sitting at the machine.
    public static List<string> BrowserOrigins(FlowerServerOptions options, string localOrigin)
    {
        var origins = new List<string>();

        if (options.HttpsPort > 0)
        {
            // Reachable puts an advertised origin first; after it, IPv4 before
            // IPv6, since 192.168.1.40 is the one a person recognises and types.
            origins.AddRange(LocalAddresses.Reachable(options.HttpsPort, options.AdvertisedHost, "https")
                .Where(origin => origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .Select((origin, index) => (origin, index))
                .OrderBy(entry => entry.index == 0 && !string.IsNullOrWhiteSpace(options.AdvertisedHost) ? 0 : entry.origin.Contains("://[") ? 2 : 1)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.origin));
        }
        else if (Uri.TryCreate(options.AdvertisedHost, UriKind.Absolute, out var advertised) && advertised.Scheme == Uri.UriSchemeHttps)
        {
            // TLS off here, but something in front of this server terminates it.
            origins.Add(advertised.GetLeftPart(UriPartial.Authority));
        }

        origins.Add(localOrigin);
        return origins;
    }

    public static string BuildBrowserPairingUrl(string origin, string code) =>
        $"{origin.TrimEnd('/')}/#pair={Uri.EscapeDataString(code)}&page=settings";

    // Which origin that link should name, given the request that asked for it.
    //
    // Normally the one the caller dialled, because that is demonstrably an
    // address which reaches this server. The exception is a caller on this very
    // machine, and it exists because of a browser rule rather than a Flower one:
    // crypto.subtle is exposed only in a secure context, so a tab at
    // http://192.168.x.y cannot hold a device key and cannot pair at all, while
    // the same tab at http://localhost can (see BrowserPeerCredentials).
    //
    // A client on this machine that found us over mDNS dials our LAN address, so
    // without this it would hand its own browser a link that is guaranteed to
    // fail - and fail as a flat "not paired", which names neither the cause nor
    // the fix. Program.cs's console link already resolves to localhost for the
    // same reason; this is the other way into the same page.
    //
    // Nothing here helps a browser on a *different* machine over plain http.
    // That case wants TLS, and is why the certificate design in
    // docs/REMOTE-TRANSPORT-PLAN.md gates the transports it does.
    public static string BrowserOriginFor(HttpRequest request)
    {
        var scheme = request.Scheme;
        if (scheme == Uri.UriSchemeHttps || !LocalAddresses.IsThisMachine(request.HttpContext.Connection.RemoteIpAddress))
            return $"{scheme}://{request.Host}";

        return request.Host.Port is { } port
            ? $"{scheme}://localhost:{port}"
            : $"{scheme}://localhost";
    }
}
