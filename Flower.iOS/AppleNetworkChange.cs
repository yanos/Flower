using System;
using System.Collections.Generic;
using System.Diagnostics;

using CoreFoundation;

using Microsoft.Extensions.Logging;

using Network;

using Flower.Logging;
using Flower.Services;

namespace Flower.iOS;

// Network changes on iOS, from NWPathMonitor - Apple's own answer to "has the
// route out of this device changed", and the one that sees Wi-Fi giving way to
// cellular, or a VPN such as Tailscale coming up, as it happens. See
// INetworkChangeSource for what the app does with it.
public sealed class AppleNetworkChange : INetworkChangeSource
{
    // Built before the container exists (AppDelegate wires this before
    // Avalonia starts), so it cannot be injected; the same as
    // BonjourMdnsBackend's.
    private static readonly ILogger Logger =
        AppLogging.CreateLogger(typeof(AppleNetworkChange).FullName!);

    // How often a run of unchanged re-reports is summed up in the log.
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromMinutes(1);

    private readonly NWPathMonitor _monitor = new();
    private bool _seenFirst;

    // What the monitor last reported, as a line, and how many times since then
    // it has reported exactly that again. All of it is touched only on the
    // monitor's own serial queue.
    private string? _lastPath;
    private int _unchangedRepeats;
    private readonly Stopwatch _sinceRepeatSummary = Stopwatch.StartNew();

    public event Action? Changed;

    public AppleNetworkChange()
    {
        _monitor.SnapshotHandler = path =>
        {
            Describe(path);

            // The monitor reports the current path the moment it starts. That
            // describes where the device already is, not a move, so it is not
            // passed on.
            if (!_seenFirst)
            {
                _seenFirst = true;
                return;
            }

            Changed?.Invoke();
        };
        _monitor.SetQueue(new DispatchQueue("flower.network-change"));
        _monitor.Start();
    }

    // What the path is, every time the monitor says it changed. A phone log
    // from 2026-11-04 showed "Network changed" every three seconds for hours,
    // each one setting off a check of every server address, and nothing said
    // what had changed - whether the phone really was moving between Wi-Fi and
    // cellular while a stream stalled, or the monitor was re-reporting one
    // unchanged path. A real change is logged as it happens; a re-report is
    // counted and summed up once a minute, so the log answers that question
    // without a line every three seconds.
    private void Describe(NWPath path)
    {
        string summary;
        try
        {
            summary = Summarize(path);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not describe the network path");
            return;
        }

        if (summary == _lastPath)
        {
            _unchangedRepeats++;
            if (_sinceRepeatSummary.Elapsed >= RepeatSummaryInterval)
                SummarizeRepeats(summary);
            return;
        }

        if (_unchangedRepeats > 0)
            SummarizeRepeats(_lastPath!);

        Logger.LogInformation(
            _lastPath == null ? "Network path at start: {Path}" : "Network path changed: {Path} (was {Previous})",
            summary, _lastPath);
        _lastPath = summary;
        _sinceRepeatSummary.Restart();
    }

    private void SummarizeRepeats(string summary)
    {
        Logger.LogInformation(
            "NWPathMonitor re-reported an unchanged path {Count} time(s) in {Seconds:F0}s: {Path}",
            _unchangedRepeats, _sinceRepeatSummary.Elapsed.TotalSeconds, summary);
        _unchangedRepeats = 0;
        _sinceRepeatSummary.Restart();
    }

    private static string Summarize(NWPath path)
    {
        var interfaces = new List<string>();
        path.EnumerateInterfaces(nwInterface =>
        {
            interfaces.Add($"{nwInterface.InterfaceType}:{nwInterface.Name}");
            return true;
        });

        var families = (path.HasIPV4 ? "v4" : "") + (path.HasIPV6 ? "v6" : "");
        return $"Status={path.Status} Interfaces=[{string.Join(", ", interfaces)}] Expensive={path.IsExpensive} "
            + $"IP={(families.Length == 0 ? "none" : families)} Dns={path.HasDns}";
    }
}
