using System;
using System.IO;
using System.Text;

namespace Flower.IosRunner;

// The output contract of an iOS runner (Flower.DeviceChecks.iOS,
// Flower.Tests.iOS), which is linked into both. Every line goes three places at
// once, because each is the only one that works somewhere:
//
//  - a file in the app's Documents directory, which is what the scripts under
//    scripts/ read out of the simulator's data container. Console.WriteLine
//    from a .NET iOS app does not reliably reach `simctl launch
//    --console-pty`, and a run that passes but reports nothing is
//    indistinguishable from a hang. A file in a container the script can find
//    its way into has no such failure mode.
//  - stdout, which a device run launched through devicectl --console shows
//    live.
//  - the screen (RunnerSceneDelegate), so a run on a phone with no cable
//    attached is readable by the person holding it.
//
// Appended line by line rather than rewritten whole. A script polling the file
// can catch a line half-written, but it only ever acts on the tally line, and
// a tally not there yet is one it reads on its next poll.
public static class RunnerTranscript
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Text = new();
    private static string? _path;

    // Raised with the whole transcript so far, from whichever thread wrote.
    public static event Action<string>? Changed;

    public static string Snapshot
    {
        get
        {
            lock (Gate)
                return Text.ToString();
        }
    }

    // Deletes what a previous run left, so a script never reads last run's
    // tally as this one's.
    public static void Start(string fileName)
    {
        lock (Gate)
        {
            _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName);
            File.Delete(_path);
            Text.Clear();
        }
    }

    public static void WriteLine(string line)
    {
        Console.WriteLine(line);

        string snapshot;
        lock (Gate)
        {
            Text.AppendLine(line);
            snapshot = Text.ToString();

            try
            {
                if (_path is not null)
                    File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception unwritable)
            {
                Console.WriteLine($"could not write the transcript: {unwritable.Message}");
            }
        }

        Changed?.Invoke(snapshot);
    }

    // For code that reports through a TextWriter - xunit's runner does.
    public static TextWriter Writer { get; } = new LineWriter();

    private sealed class LineWriter : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_pending)
            {
                if (value == '\n')
                {
                    RunnerTranscript.WriteLine(_pending.ToString().TrimEnd('\r'));
                    _pending.Clear();
                }
                else
                {
                    _pending.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "")
                Write(c);
        }

        public override void Flush()
        {
            lock (_pending)
            {
                if (_pending.Length == 0)
                    return;

                RunnerTranscript.WriteLine(_pending.ToString());
                _pending.Clear();
            }
        }
    }
}
