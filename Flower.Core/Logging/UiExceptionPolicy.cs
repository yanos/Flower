using System;
using System.Collections.Generic;

namespace Flower.Logging
{
    // Whether the app carries on after an exception nothing caught on the UI
    // thread. Usually yes: one failed click should not stop the music. Not
    // when the runtime itself is in trouble, and not when it keeps happening,
    // which is an app that no longer works and is better off crashing, so
    // that the crash is reported as one.
    public sealed class UiExceptionPolicy
    {
        public const int MaxInWindow = 5;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

        private readonly Queue<DateTimeOffset> _recent = new();

        public bool Survives(Exception exception, DateTimeOffset now)
        {
            if (exception is OutOfMemoryException or AccessViolationException or InvalidProgramException
                or BadImageFormatException or TypeLoadException or MissingMethodException)
            {
                return false;
            }

            while (_recent.TryPeek(out var oldest) && now - oldest >= Window)
                _recent.Dequeue();

            _recent.Enqueue(now);
            return _recent.Count <= MaxInWindow;
        }
    }
}
