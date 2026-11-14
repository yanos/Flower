using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Flower.Controls;
using Flower.Logging;
using Flower.Audio;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;

using Xunit;

namespace Flower.Tests.AppShell;

// The container itself, as App.Bootstrap actually builds it. Worth testing
// now and impossible to test before: registration used to be ~30 hand-`new`ed
// instances interleaved with startup sequencing inside one 330-line method
// (docs/ARCHITECTURE-REVIEW.md 2.3), so the only way to find out whether the
// graph resolved was to launch the app. It is App.RegisterServices - a static
// method over an IServiceCollection, nothing else - so a test can build the
// real thing.
//
// What that catches: a service that gains a constructor parameter nobody
// registered, a factory lambda asking for a type that is not there, and a
// singleton silently becoming two instances. All three are startup crashes or
// split-brain state bugs that no other test in this suite would see.
//
// The audio registrations are replaced with a FakeAudioManager throughout -
// resolving the real IAudioManager opens an actual miniaudio playback device
// on the test machine. Everything else,
// including the whole P2P sync stack, is the genuine registration: constructing
// those services is inert, it is Start() that opens sockets and nothing here
// calls it.
[Collection("PlatformDataDirectory")]
public class CompositionRootTests : PinnedDataDirectory
{
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddSerilog(Serilog.Core.Logger.None));

        App.RegisterServices(services);

        // Last registration wins, so this displaces the real IAudioManager
        // without RegisterServices needing a test-only seam of its own.
        services.AddSingleton<IAudioManager>(new FakeAudioManager());

        // ValidateOnBuild is the point of the exercise: it walks every
        // *type*-based registration up front and throws on any constructor
        // parameter that has nothing to satisfy it, rather than waiting for
        // whichever screen happens to resolve it first at runtime.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    // Factory-lambda registrations are invisible to ValidateOnBuild - the
    // container cannot see inside the lambda - so they are resolved for real
    // here instead. These are exactly the registrations that read a file or a
    // platform hook, which is why they are lambdas in the first place.
    public static IEnumerable<object[]> EveryService() =>
        new[]
        {
            typeof(LibraryStore), typeof(AppSettingsStore), typeof(PlaylistStore),
            typeof(DeviceKeyStore), typeof(DeviceIdentityStore), typeof(DeviceNicknameStore),
            typeof(TrustedPeerStore), typeof(PlaylistSyncStateStore), typeof(ServerStarBaselineStore),
            typeof(InMemoryLogStore),
            typeof(AppSettings), typeof(Library), typeof(MainPlaylist),
            typeof(Importer.IMusicImporter), typeof(ColumnManager), typeof(AlbumArtLoader),
            typeof(DeviceSigningKey), typeof(DeviceIdentity),
            typeof(NetworkDiscoveryService), typeof(PlaylistSyncService),
            typeof(LibrarySyncService), typeof(LibraryDownloadService), typeof(PeerPairingService),
            typeof(PairedServerReachability), typeof(PeerTrackResolver),
            typeof(LibraryMirrorService),
        }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(EveryService))]
    public void Every_registered_service_resolves(Type serviceType)
    {
        using var provider = BuildContainer();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    // Everything is registered AddSingleton, and several of these are shared
    // mutable state that only works because of it: DeviceIdentity is edited in
    // place when the user renames the device and every service reads .Alias
    // live off the same instance, and AppSettings is the object the settings
    // UI mutates and MainWindow.Closing persists.
    [Fact]
    public void Shared_state_is_a_single_instance_across_the_whole_container()
    {
        using var provider = BuildContainer();

        Assert.Same(provider.GetRequiredService<AppSettings>(), provider.GetRequiredService<AppSettings>());
        Assert.Same(provider.GetRequiredService<DeviceIdentity>(), provider.GetRequiredService<DeviceIdentity>());
        Assert.Same(provider.GetRequiredService<Library>(), provider.GetRequiredService<Library>());
    }

    // Fingerprint is the hash of the signing key's public key, not an
    // independent random value (see DeviceIdentityStore.Load) - which is only
    // true because the DeviceIdentity factory resolves DeviceSigningKey first
    // and passes its Fingerprint in. Registering the two independently would
    // give this device an identity its own signatures do not verify against.
    [Fact]
    public void DeviceIdentity_is_derived_from_the_signing_key()
    {
        using var provider = BuildContainer();

        Assert.Equal(
            provider.GetRequiredService<DeviceSigningKey>().Fingerprint,
            provider.GetRequiredService<DeviceIdentity>().Fingerprint);
    }

    // The container's ILoggerFactory registration is what makes an injected
    // ILogger<T> and Flower.Core's static-field loggers the same pipeline
    // (App.OnFrameworkInitializationCompleted registers the instance
    // AppLogging is using). A container that quietly builds its own factory
    // would send half the app's logs somewhere else.
    [Fact]
    public void An_explicitly_registered_logger_factory_wins_over_AddLoggings_own()
    {
        var factory = LoggerFactory.Create(_ => { });
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddSerilog(Serilog.Core.Logger.None))
            .AddSingleton<ILoggerFactory>(factory);

        using var provider = services.BuildServiceProvider();

        Assert.Same(factory, provider.GetRequiredService<ILoggerFactory>());
    }
}
