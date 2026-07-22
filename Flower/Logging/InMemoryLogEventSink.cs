using Serilog.Core;
using Serilog.Events;

namespace Flower.Logging
{
    // Feeds InMemoryLogStore from the same Serilog pipeline that already
    // writes to file/console (see AppLogging.Initialize) - added as a third
    // sink, not a replacement for either existing one.
    public sealed class InMemoryLogEventSink : ILogEventSink
    {
        private readonly InMemoryLogStore _store;

        public InMemoryLogEventSink(InMemoryLogStore store)
        {
            _store = store;
        }

        public void Emit(LogEvent logEvent)
        {
            string? sourceContext = null;
            if (logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string s })
                sourceContext = s;

            _store.Add(new InMemoryLogEntry(
                logEvent.Timestamp,
                logEvent.Level.ToString(),
                sourceContext,
                logEvent.RenderMessage(),
                logEvent.Exception?.ToString()));
        }
    }
}
