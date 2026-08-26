using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Browser;

using Flower;
using Flower.Manager;
using Flower.Services;

internal sealed partial class Program
{
    private static async Task Main(string[] args)
    {
        // Loaded before Bootstrap() runs (during StartBrowserAppAsync below)
        // so WebAudioManager's constructor can call its [JSImport] methods
        // synchronously without itself needing to be async. Must be
        // site-root-relative, not "./webaudio.js" - the dotnet WASM loader
        // resolves relative import specifiers against /_framework/, not
        // wwwroot's own root, so a plain relative path 404s.
        await JSHost.ImportAsync(WebAudioManager.ModuleName, "/webaudio.js");

        // Same reason and the same site-root-relative path rule: App's browser
        // branch reads the URL fragment synchronously during Bootstrap() to find
        // the pairing code the desktop client's "Server Settings..." button put
        // there, so the module has to already be loaded by then.
        await JSHost.ImportAsync(BrowserLocation.ModuleName, "/weblocation.js");

        // This tab's device keypair (see BrowserSigningKey). Not read during
        // Bootstrap() - the key is loaded lazily by the first request that needs
        // to be signed - but imported here with the others so that first request
        // does not have to wait on a module load, and so a browser that cannot
        // provide the module fails here rather than deep inside a sync call.
        await JSHost.ImportAsync(BrowserSigningKey.ModuleName, "/webcrypto.js");

        await BuildAvaloniaApp()
            .WithFlowerFonts()
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .LogToTrace();
}
