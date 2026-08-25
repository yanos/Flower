using Microsoft.Extensions.Logging;

using Serilog.Events;

namespace Flower.Server.Configuration;

// Turns the standard Logging:LogLevel section into the two things Serilog
// actually takes: one floor, and a set of per-category overrides.
//
// This exists because the section did nothing at all before it. AddSerilog
// registers a provider-scoped filter rule of Trace for every category, and
// Microsoft.Extensions.Logging resolves a provider-specific rule ahead of any
// rule without a provider name - so the whole Logging:LogLevel section, which
// only ever produces provider-less rules, lost to it. Serilog does that on
// purpose: it means to own filtering itself rather than be filtered twice. The
// visible symptom was framework Debug lines pouring out under a Default of
// Information, and staying there even when the command line said Warning.
//
// So the filtering moves to where it is honoured. The section keeps its usual
// ASP.NET shape and its usual names, because that is what an operator will
// reach for - it just reaches Serilog now instead of a gate that was already
// wide open.
public static class LogLevelSettings
{
    public const string SectionName = "Logging:LogLevel";

    // Serilog has no "off": the convention is a level above the highest real
    // one, which nothing can ever reach. That is what None maps to.
    public static readonly LogEventLevel Off = LogEventLevel.Fatal + 1;

    // Default is the floor for every sink; every other key is a source-context
    // prefix override, which may sit either side of that floor - "Flower":
    // "Debug" under a Default of Information is exactly the case this server
    // ships with.
    public static (LogEventLevel Floor, IReadOnlyDictionary<string, LogEventLevel> Overrides) Read(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var floor = LogEventLevel.Debug;
        var overrides = new Dictionary<string, LogEventLevel>(StringComparer.Ordinal);

        foreach (var entry in section.GetChildren())
        {
            // The file documents its own keys with _-prefixed siblings (see
            // appsettings.json); they are prose, not categories.
            if (entry.Key.StartsWith('_'))
                continue;
            if (!TryParse(entry.Value, out var level))
                continue;

            if (string.Equals(entry.Key, "Default", StringComparison.Ordinal))
                floor = level;
            else
                overrides[entry.Key] = level;
        }

        return (floor, overrides);
    }

    // Accepts Microsoft.Extensions.Logging's names, since that is what the
    // section is spelled in, and Serilog's own where they differ - an operator
    // who writes "Verbose" or "Fatal" here means the obvious thing and should
    // not have to discover which of the two vocabularies this file is in.
    public static bool TryParse(string? value, out LogEventLevel level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var serilogLevel))
        {
            level = serilogLevel;
            return true;
        }

        if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var melLevel))
            return false;

        level = melLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => Off,
        };
        return true;
    }
}
