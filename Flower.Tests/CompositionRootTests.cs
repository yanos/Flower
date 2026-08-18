using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Headless.XUnit;

using Flower.Controls;
using Flower.Logging;
using Flower.Manager;
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

namespace Flower.Tests;

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
// resolving the real IAudioManager calls VlcNativeSetup.Initialize() and opens
// an actual miniaudio playback device on the test machine. Everything else,
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
            typeof(TrustedPeerStore), typeof(PlaylistSyncStateStore), typeof(ClientLogStore),
            typeof(InMemoryLogStore),
            typeof(AppSettings), typeof(Library), typeof(MainPlaylist),
            typeof(Importer.IMusicImporter), typeof(ColumnManager), typeof(AlbumArtLoader),
            typeof(DeviceSigningKey), typeof(DeviceIdentity),
            typeof(NetworkDiscoveryService), typeof(SyncHttpServer), typeof(PlaylistSyncService),
            typeof(LibrarySyncService), typeof(LibraryDownloadService), typeof(PeerPairingService),
            typeof(PeerUnpairNotifier), typeof(PairedServerReachability), typeof(PeerTrackResolver),
        }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(EveryService))]
    public void Every_registered_service_resolves(Type serviceType)
    {
        using var provider = BuildContainer();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    // The ViewModels resolve last because they are the widest part of the
    // graph - MainViewModel alone takes eighteen constructor parameters - and
    // an [AvaloniaFact] because several of them touch Dispatcher/UI types on
    // construction.
    [AvaloniaFact]
    public void Every_registered_view_model_resolves()
    {
        using var provider = BuildContainer();

        Assert.NotNull(provider.GetRequiredService<PlaylistControlViewModel>());
        Assert.NotNull(provider.GetRequiredService<VolumeControlViewModel>());
        Assert.NotNull(provider.GetRequiredService<CurrentlyPlayingControlViewModel>());
        Assert.NotNull(provider.GetRequiredService<LogViewModel>());
        Assert.NotNull(provider.GetRequiredService<EqualizerViewModel>());
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        Assert.NotNull(provider.GetRequiredService<MobileMainViewModel>());
        Assert.NotNull(provider.GetRequiredService<NowPlayingIntegrationService>());
    }

    // MainViewModel's ten sync-stack parameters are nullable *and defaulted*,
    // so the container is free to pick that constructor and pass null for every
    // one of them - which would compile, resolve, start, and leave the entire
    // sync feature silently dead on a platform that has it, with no error
    // anywhere. The defaults exist only for Flower.Web/WASM, where these are
    // deliberately not registered (see MainViewModel's own doc comment).
    //
    // Read off the fields by reflection because none of them is exposed: the
    // alternative is asserting on ten separate downstream behaviours, and this
    // states the actual invariant - on a platform that registers them, none of
    // these arrived as its default.
    [AvaloniaFact]
    public void MainViewModel_gets_the_real_sync_services_not_the_null_defaults()
    {
        using var provider = BuildContainer();

        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        var optionalTypes = typeof(MainViewModel)
            .GetConstructors()
            .Single(c => c.GetParameters().Length > 1)
            .GetParameters()
            .Where(p => p.HasDefaultValue)
            .Select(p => p.ParameterType)
            .ToList();

        Assert.NotEmpty(optionalTypes);

        var fields = typeof(MainViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

        foreach (var type in optionalTypes)
        {
            var field = fields.SingleOrDefault(f => f.FieldType == type);
            if (field == null)
            {
                // SyncHttpServer is the one optional dependency MainViewModel
                // keeps no field for - it only subscribes to its
                // PeerUnpairNotified/PeerApprovalRequested events in the
                // constructor and never touches it again. Named explicitly
                // rather than skipped silently, so a future parameter that
                // quietly stops being stored fails here instead of passing
                // vacuously.
                Assert.Equal(typeof(SyncHttpServer), type);
                continue;
            }

            Assert.NotNull(field.GetValue(mainViewModel));
        }
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

    // The same AppSettings instance the container hands out has to be the one
    // every consumer captured - two separate loads of settings.json would mean
    // the Settings UI and MainWindow.Closing writing over each other. Asserted
    // through MainViewModel because that is the consumer where it would show
    // up as a user-visible bug: a setting toggled elsewhere in the app simply
    // not being the setting this screen reads.
    [AvaloniaFact]
    public void Mutating_the_shared_AppSettings_is_visible_to_the_view_model_that_took_it()
    {
        using var provider = BuildContainer();

        var settings = provider.GetRequiredService<AppSettings>();
        settings.IsServer = !settings.IsServer;

        Assert.Equal(settings.IsServer, provider.GetRequiredService<MainViewModel>().IsServer);
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
