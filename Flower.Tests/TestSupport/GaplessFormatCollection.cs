namespace Flower.Tests.TestSupport;

// GaplessFormat's sample format and rate are process-wide statics, settled once
// at startup by whatever opens the output device and then read by everything
// downstream - GaplessCoordinator's staging maths included, through
// BytesPerFrame.
//
// Only one test constructs a GaplessAudioManager through its production
// constructor, because only one test is about the startup ordering that
// constructor performs. That construction settles the canonical format as a
// side effect, so under xUnit's parallel-by-class default it can be observed by
// a class that is midway through its own arithmetic - which is exactly how it
// first showed up, as a failure in GaplessCoordinatorTests that passed when run
// on its own.
//
// Same shape as PlatformDataDirectoryCollection next door, and for the same
// reason: a shared static needs the classes that touch it serialised, and a
// named collection is the whole of what that takes.
[CollectionDefinition("GaplessFormat")]
public sealed class GaplessFormatCollection
{
}
