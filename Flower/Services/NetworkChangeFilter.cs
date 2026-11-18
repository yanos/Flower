using System;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// Whether a reported network change is one. Every platform's signal has been
// seen to report changes that move nothing: iOS's NWPathMonitor re-reported
// one unchanged cellular path every three seconds for hours (2,896 times in a
// day of a phone log), Android raises link-property changes for a DNS server
// or a rotated temporary address, and on a Mac .NET's address-changed event
// fires for the AirDrop interface. Each one used to set off a recheck of every
// server address over the link the music was streaming on - slow enough, on
// cellular, that the server's answers timed out and it was taken for gone
// while it streamed.
//
// So a report is checked against the network itself: the description of it
// now (INetworkChangeSource.DescribeNetwork, read once the burst of reports
// has settled) against the one last acted on. A description that cannot be
// had is taken as a change - better a recheck too many than a move missed.
public sealed class NetworkChangeFilter
{
    // How often a run of reports that changed nothing is summed up in the log.
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromMinutes(1);

    private readonly Func<string?> _describe;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Stopwatch _sinceRepeatSummary = Stopwatch.StartNew();
    private string? _last;
    private int _unchangedReports;

    public NetworkChangeFilter(Func<string?> describe, ILogger logger)
    {
        _describe = describe;
        _logger = logger;
        _last = describe();
        _logger.LogInformation("Network at start: {Network}", _last ?? "(cannot be described)");
    }

    // Whether the network is somewhere other than it was the last time this
    // said yes (or at start).
    public bool HasMoved()
    {
        var now = _describe();
        lock (_gate)
        {
            if (now != null && now == _last)
            {
                _unchangedReports++;
                if (_sinceRepeatSummary.Elapsed >= RepeatSummaryInterval)
                    SummarizeRepeats();
                return false;
            }

            if (_unchangedReports > 0)
                SummarizeRepeats();
            _logger.LogInformation("Network moved: {Network} (was {Previous})",
                now ?? "(cannot be described)", _last ?? "(cannot be described)");
            _last = now;
            return true;
        }
    }

    private void SummarizeRepeats()
    {
        _logger.LogInformation(
            "Network change reported {Count} time(s) in {Seconds:F0}s with nothing different: {Network}",
            _unchangedReports, _sinceRepeatSummary.Elapsed.TotalSeconds, _last);
        _unchangedReports = 0;
        _sinceRepeatSummary.Restart();
    }
}
