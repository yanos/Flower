using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using Flower.Server.Services;

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
        // from the API surfaces so a mistyped /rest/... still fails as an API
        // call rather than silently returning HTML that a Subsonic client will
        // try to parse as XML.
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/rest"))
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
    public static string BuildBrowserPairingUrl(string origin, string code) =>
        $"{origin.TrimEnd('/')}/#pair={Uri.EscapeDataString(code)}&page=settings";
}
