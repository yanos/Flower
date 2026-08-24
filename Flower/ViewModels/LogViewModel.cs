using System;

using Flower.Logging;
using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels
{
    // Backs the Log window (View > Log...) - see LogWindow.axaml. This
    // instance's own live log, fed by InMemoryLogStore, and nothing else.
    //
    // It used to also list the paired server and every device on that server's
    // roster, each fetched over the admin API. Those rows have moved to where
    // the person reading them actually is: the server's own settings screen
    // (SettingsPanel's Logs tab), which now uses this same viewer. A client
    // shows the client's log; the server shows everyone's.
    public sealed class LogViewModel : LogViewerViewModel, IDisposable
    {
        private readonly InMemoryLogStore _logStore;

        public LogViewModel(
            InMemoryLogStore logStore,
            AppSettings appSettings,
            AppSettingsStore appSettingsStore)
            : base(appSettings, appSettingsStore)
        {
            _logStore = logStore;

            _subscriptions.Add<EventHandler<InMemoryLogEntry>>(OnEntryAdded,
                h => _logStore.EntryAdded += h, h => _logStore.EntryAdded -= h);

            Reload();
        }

        // Re-reads the buffer from scratch. Called on construction and again
        // every time the window is (re)opened: this is a DI singleton whose
        // events have already fired by then, so the freshly attached View has
        // nothing to paint from until something is emitted for it.
        public void Reload() => ShowLog(_logStore.Snapshot());

        private void OnEntryAdded(object? sender, InMemoryLogEntry entry) => Append(entry);

        // Every event this class attaches to in its constructor, paired with
        // its teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md
        // Tier 2.3.
        private readonly SubscriptionBag _subscriptions = new();

        public void Dispose() => _subscriptions.Dispose();
    }
}
