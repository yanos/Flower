using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Flower.Controls;
using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Guards the floor under every other test in this assembly: nothing here may
// write to the developer's own ~/Library/Application Support/Flower.
//
// This is not a hypothetical. A ColumnManager registered in TestIoc over a
// throwaway AppSettings wrote a *default* AppSettings over the real
// settings.json - wiping the library folder list, re-enabling the iTunes
// integration and forgetting the paired server - because its debounced save
// fires 500ms after a column changes, by which time the test class that had
// pinned a directory has disposed and restored the global to null, and null
// means the real one. AtomicJsonFile keeps a single generation of backup, so a
// second such write also took settings.json.bak and left nothing to recover
// from.
//
// In the collection but *not* deriving from PinnedDataDirectory, which is the
// whole point: it has to observe what an unpinned test sees. Collection
// membership only keeps it from running while one of the pinning classes has
// the shared global redirected, which would make it fail for the wrong reason.
[Collection("PlatformDataDirectory")]
public class TestDataDirectoryIsolationTests
{
    [Fact]
    public void An_unpinned_test_never_resolves_the_real_application_support_directory()
    {
        Assert.Equal(AssemblySetup.DefaultDataDirectory, PlatformDataDirectory.Current);
        Assert.Equal(AssemblySetup.DefaultDataDirectory, AppDataDirectory.Path);
    }

    // Deriving from PinnedDataDirectory is only half of the isolation; the
    // collection attribute is the other half, and nothing but this enforces it.
    // A subclass without it pins the process-global while other collections are
    // running, so their stores write into its temp directory and it deletes that
    // directory on Dispose. ViewModelDisposalTests was missing it.
    [Fact]
    public void Every_class_that_pins_a_data_directory_is_in_the_collection_that_serializes_them()
    {
        var offenders = typeof(TestDataDirectoryIsolationTests).Assembly
            .GetTypes()
            .Where(t => typeof(PinnedDataDirectory).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != "PlatformDataDirectory")
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"missing [Collection(\"PlatformDataDirectory\")]: {string.Join(", ", offenders)}");
    }

    // The exact shape of the accident, reproduced: a ColumnManager built the way
    // TestIoc builds one, a column change, and the debounced save that follows.
    [Fact]
    public async Task A_debounced_column_save_lands_in_the_test_directory_not_the_real_one()
    {
        var written = Path.Combine(AssemblySetup.DefaultDataDirectory, "settings.json");
        if (File.Exists(written))
            File.Delete(written);

        var manager = new ColumnManager(new AppSettings(), new AppSettingsStore());
        foreach (var column in manager.Columns)
        {
            column.Width += 7;
            break;
        }

        // Longer than ColumnManager's own 500ms debounce, so the save it
        // scheduled has actually run rather than merely been queued.
        await Task.Delay(1500);

        Assert.True(File.Exists(written),
            $"the debounced column save did not land in {written} - check where it went instead");
    }
}
