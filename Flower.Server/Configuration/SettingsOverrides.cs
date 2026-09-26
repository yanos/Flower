using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Flower.Server.Configuration;

// Which settings the settings page cannot change, because something ranked
// above flower-server.json already sets them.
//
// The page writes flower-server.json, and Program.cs deliberately places that
// file below the environment and the command line, so a container can pin a
// value its data volume cannot overrule. The cost was a page that accepted an
// edit, saved it, and then went on showing the old value - Flower__Alias in a
// compose file outranking the name typed in, with nothing saying so. Asked of
// the live configuration rather than kept as a list of names, so whatever the
// deployment actually sets is what the page greys out.
public static class SettingsOverrides
{
    // Every setting the settings page edits (ServerSettingsUpdateDto), by its
    // FlowerServerOptions name - the name the page is told about locks under.
    public static readonly string[] PageSettings =
    [
        nameof(FlowerServerOptions.Alias),
        nameof(FlowerServerOptions.AdvertisedHost),
        nameof(FlowerServerOptions.AdvertiseOnLan),
        nameof(FlowerServerOptions.TrustTailscaleRange),
        nameof(FlowerServerOptions.AllowedCidrs),
        nameof(FlowerServerOptions.LibraryPaths),
        nameof(FlowerServerOptions.IntegrateWithITunes),
        nameof(FlowerServerOptions.SyncPlayCountFromITunes),
        nameof(FlowerServerOptions.SyncDateAddedFromITunes),
        nameof(FlowerServerOptions.AllowPublicAccess),
    ];

    // Each of PageSettings that something above flower-server.json sets, with
    // what sets it.
    public static Dictionary<string, string> Describe(IConfiguration configuration)
    {
        var overridden = new Dictionary<string, string>();
        foreach (var option in PageSettings)
        {
            if (OverriddenBy(configuration, option) is { } source)
                overridden[option] = source;
        }

        return overridden;
    }

    // What sets FlowerServerOptions.<option> over flower-server.json, as the
    // page should name it, or null when the page's own value is the one in
    // effect.
    public static string? OverriddenBy(IConfiguration configuration, string option)
    {
        if (configuration is not IConfigurationRoot root)
            return null;

        var key = $"{FlowerServerOptions.SectionName}:{option}";
        var providers = root.Providers.ToList();
        var settingsFile = providers.FindIndex(p =>
            p is JsonConfigurationProvider { Source.Path: { } path }
            && Path.GetFileName(path) == ServerDataDirectory.SettingsFileName);
        if (settingsFile < 0)
            return null;

        // The last provider with a value wins, so the one to name is the
        // highest-ranked of those above the file.
        //
        // A list is locked by any entry at all. Configuration merges lists
        // entry by entry, so the Docker image's Flower__LibraryPaths__0=/music
        // replaced only the page's first folder and let the rest through - a
        // list that was neither the page's nor the environment's.
        for (var i = providers.Count - 1; i > settingsFile; i--)
        {
            var path = providers[i].TryGet(key, out _)
                ? option
                : providers[i].GetChildKeys([], key).FirstOrDefault() is { } entry ? $"{option}:{entry}" : null;
            if (path is null)
                continue;

            return providers[i] switch
            {
                EnvironmentVariablesConfigurationProvider =>
                    $"the {FlowerServerOptions.SectionName}__{path.Replace(":", "__")} environment variable",
                CommandLineConfigurationProvider => $"the --{FlowerServerOptions.SectionName}:{path} command-line option",
                _ => "this server's startup configuration",
            };
        }

        return null;
    }
}
