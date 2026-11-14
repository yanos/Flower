using System;
using System.Collections.Generic;

namespace Flower.Tests.TestSupport;

// Every report, kept in order on the reporting thread. Progress<T> would post
// each one to a captured context and leave the test racing its own assertions.
public sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_reports)
                return [.. _reports];
        }
    }

    public void Report(T value)
    {
        lock (_reports)
            _reports.Add(value);
    }
}
