using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Browser;

using Flower;
using Flower.Manager;

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

        await BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .LogToTrace();
}
