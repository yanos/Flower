namespace Flower.Tests.TestSupport;

// PlatformNativeTextField.Factory is a process-wide static, and every
// TextInput in the process reads it: with a factory set, a box is typed in
// through the platform's own field rather than its own Text. The tests that
// set one (TextInputNativeEditingTests) set it for the length of each test -
// and under xUnit's parallel-by-class default that length overlaps with
// whatever other class is running, any of which may have a TextInput in a
// window it is driving.
//
// Unlike GaplessFormatCollection, naming a collection is not enough here: the
// readers are not a few classes that can join it but anything that builds a
// view with a box in it. So this collection runs on its own, after the
// parallel ones. It showed up as TrackInfoWindowTests' typed title not
// reaching OK on the iOS simulator, in a full run only - both classes pass
// there on their own.
[CollectionDefinition("NativeTextField", DisableParallelization = true)]
public sealed class NativeTextFieldCollection
{
}
