import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

// The splash div is an opaque, full-viewport white overlay - Avalonia renders
// its canvas underneath it, so leaving it up looks exactly like "the app never
// started". Nothing in the runtime closes it for us, so watch #out for the
// canvas Avalonia inserts and hide the splash the moment it appears.
const splash = document.querySelector(".avalonia-splash");
if (splash) {
    const host = document.getElementById("out");
    const closeWhenReady = () => {
        if (!host?.querySelector("canvas"))
            return false;
        splash.classList.add("splash-close");
        // Avalonia attaches and lays out, but does not present its first frame
        // until the surface is invalidated - so the canvas sits blank
        // (transparent, i.e. page-white) until the first real resize. That is
        // why opening DevTools "fixed" it: the viewport genuinely changed size.
        //
        // A synthetic resize event is not enough. Avalonia observes the host
        // with a ResizeObserver, which fires on actual box changes only and
        // ignores dispatched events. So briefly perturb the real height by a
        // pixel and restore it on the next frame - two genuine observer
        // callbacks, the second one landing back at the correct size.
        if (host) {
            const restore = host.style.height;
            host.style.height = "calc(100% - 1px)";
            requestAnimationFrame(() => {
                host.style.height = restore;
            });
        }
        return true;
    };
    if (!closeWhenReady()) {
        const observer = new MutationObserver(() => {
            if (closeWhenReady())
                observer.disconnect();
        });
        observer.observe(host ?? document.body, { childList: true, subtree: true });
    }
}

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
