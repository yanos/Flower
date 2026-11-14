using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flower.Platform;

public enum MediaTransportButton
{
    Play = 0,
    Pause = 1,
    Stop = 2,
    Next = 6,
    Previous = 7
}

public enum MediaPlaybackStatus
{
    Closed = 0,
    Changing = 1,
    Stopped = 2,
    Playing = 3,
    Paused = 4
}

// Windows' SystemMediaTransportControls for one window: the media flyout
// over the volume overlay, the lock screen's card, and where the keyboard's
// media keys and a Bluetooth headset's buttons go. See WindowsNowPlaying,
// which adapts this to IPlatformNowPlaying.
//
// Raw WinRT over COM vtables rather than the C#/WinRT projection, because
// Flower.Desktop is plain net10.0 (one build for Windows and Linux) and the
// projection needs a net10.0-windows10.0.x target. Every slot number below is
// an interface's vtable position: 0-2 IUnknown, 3-5 IInspectable, then the
// methods in declaration order (windows.media.idl,
// windows.storage.streams.idl, systemmediatransportcontrolsinterop.h). A
// wrong slot calls the wrong method, which is why WindowsMediaTransportControlsTests
// reads every value back from Windows rather than trusting what was sent.
//
// Not thread-affine by contract - the controls are agile - but
// WindowsNowPlaying only ever calls this from the UI thread, the thread that
// owns the window.
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class WindowsMediaTransportControls : IDisposable
{
    private static readonly Guid IID_ISystemMediaTransportControlsInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid IID_ISystemMediaTransportControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    private static readonly Guid IID_ISystemMediaTransportControls2 = new("ea98d2f6-7f3c-4af2-a586-72889808efb1");
    private static readonly Guid IID_IMusicDisplayProperties2 = new("00368462-97d3-44b9-b00f-008afcefaf18");
    private static readonly Guid IID_ITimelineProperties = new("5125316a-c3a2-475b-8507-93534dc88f15");
    private static readonly Guid IID_IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IID_IRandomAccessStreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");

    // ISystemMediaTransportControls
    private const int Smtc_get_PlaybackStatus = 6;
    private const int Smtc_put_PlaybackStatus = 7;
    private const int Smtc_get_DisplayUpdater = 8;
    private const int Smtc_put_IsEnabled = 11;
    private const int Smtc_put_IsPlayEnabled = 13;
    private const int Smtc_put_IsPauseEnabled = 17;
    private const int Smtc_put_IsPreviousEnabled = 25;
    private const int Smtc_put_IsNextEnabled = 27;
    private const int Smtc_add_ButtonPressed = 32;
    private const int Smtc_remove_ButtonPressed = 33;

    // ISystemMediaTransportControls2
    private const int Smtc2_UpdateTimelineProperties = 12;

    // ISystemMediaTransportControlsDisplayUpdater
    private const int Updater_get_Type = 6;
    private const int Updater_put_Type = 7;
    private const int Updater_put_Thumbnail = 11;
    private const int Updater_get_MusicProperties = 12;
    private const int Updater_ClearAll = 16;
    private const int Updater_Update = 17;

    // IMusicDisplayProperties
    private const int Music_get_Title = 6;
    private const int Music_put_Title = 7;
    private const int Music_get_Artist = 10;
    private const int Music_put_Artist = 11;

    // IMusicDisplayProperties2
    private const int Music2_get_AlbumTitle = 6;
    private const int Music2_put_AlbumTitle = 7;

    // ISystemMediaTransportControlsTimelineProperties
    private const int Timeline_put_StartTime = 7;
    private const int Timeline_put_EndTime = 9;
    private const int Timeline_put_MinSeekTime = 11;
    private const int Timeline_put_MaxSeekTime = 13;
    private const int Timeline_put_Position = 15;

    // IRandomAccessStreamReferenceStatics
    private const int Statics_CreateFromStream = 8;

    private const int MediaPlaybackType_Music = 1;

    private IntPtr _controls;
    private IntPtr _updater;
    private readonly ButtonPressedHandler _handler;
    private readonly long _buttonToken;

    public event Action<MediaTransportButton>? ButtonPressed;

    private WindowsMediaTransportControls(IntPtr controls)
    {
        _controls = controls;

        IntPtr updater;
        Check(Call(_controls, Smtc_get_DisplayUpdater, &updater));
        _updater = updater;
        Check(CallInt(_updater, Updater_put_Type, MediaPlaybackType_Music));

        Check(CallBool(_controls, Smtc_put_IsEnabled, true));
        Check(CallBool(_controls, Smtc_put_IsPlayEnabled, true));
        Check(CallBool(_controls, Smtc_put_IsPauseEnabled, true));
        Check(CallBool(_controls, Smtc_put_IsNextEnabled, true));
        Check(CallBool(_controls, Smtc_put_IsPreviousEnabled, true));

        _handler = new ButtonPressedHandler(button => ButtonPressed?.Invoke(button));
        long token;
        var vtbl = *(IntPtr**)_controls;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)vtbl[Smtc_add_ButtonPressed])(_controls, _handler.Pointer, &token));
        _buttonToken = token;
    }

    // The controls Windows keeps for this top-level window; the same object
    // every time for the same window.
    public static WindowsMediaTransportControls ForWindow(IntPtr hwnd)
    {
        var interop = ActivationFactory("Windows.Media.SystemMediaTransportControls", IID_ISystemMediaTransportControlsInterop);
        try
        {
            IntPtr controls;
            var iid = IID_ISystemMediaTransportControls;
            var vtbl = *(IntPtr**)interop;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[6])(interop, hwnd, &iid, &controls));
            return new WindowsMediaTransportControls(controls);
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    public MediaPlaybackStatus PlaybackStatus
    {
        get
        {
            int status;
            Check(Call(_controls, Smtc_get_PlaybackStatus, &status));
            return (MediaPlaybackStatus)status;
        }
        set => Check(CallInt(_controls, Smtc_put_PlaybackStatus, (int)value));
    }

    public void SetMusic(string? title, string? artist, string? album, byte[]? thumbnail)
    {
        Check(Call(_updater, Updater_ClearAll));
        Check(CallInt(_updater, Updater_put_Type, MediaPlaybackType_Music));

        var music = MusicProperties();
        try
        {
            PutString(music, Music_put_Title, title);
            PutString(music, Music_put_Artist, artist);
            var music2 = QueryInterface(music, IID_IMusicDisplayProperties2);
            try
            {
                PutString(music2, Music2_put_AlbumTitle, album);
            }
            finally
            {
                Marshal.Release(music2);
            }
        }
        finally
        {
            Marshal.Release(music);
        }

        if (thumbnail is { Length: > 0 })
            PutThumbnail(thumbnail);

        Check(Call(_updater, Updater_Update));
    }

    public void ClearMusic()
    {
        Check(Call(_updater, Updater_ClearAll));
        Check(Call(_updater, Updater_Update));
    }

    // What Windows holds, read back - for the tests that check every slot
    // above landed where it was meant to.
    public (string? Title, string? Artist, string? Album) ReadMusic()
    {
        // ClearAll resets Type to Unknown. Windows rejects MusicProperties
        // unless Type is Music, so read the cleared state without asking for it.
        int type;
        Check(Call(_updater, Updater_get_Type, &type));
        if (type != MediaPlaybackType_Music)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var music = MusicProperties();
        try
        {
            var title = GetString(music, Music_get_Title);
            var artist = GetString(music, Music_get_Artist);
            var music2 = QueryInterface(music, IID_IMusicDisplayProperties2);
            try
            {
                return (title, artist, GetString(music2, Music2_get_AlbumTitle));
            }
            finally
            {
                Marshal.Release(music2);
            }
        }
        finally
        {
            Marshal.Release(music);
        }
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration)
    {
        var controls2 = QueryInterface(_controls, IID_ISystemMediaTransportControls2);
        var timeline = ActivateInstance("Windows.Media.SystemMediaTransportControlsTimelineProperties", IID_ITimelineProperties);
        try
        {
            Check(CallLong(timeline, Timeline_put_StartTime, 0));
            Check(CallLong(timeline, Timeline_put_EndTime, duration.Ticks));
            Check(CallLong(timeline, Timeline_put_MinSeekTime, 0));
            Check(CallLong(timeline, Timeline_put_MaxSeekTime, duration.Ticks));
            Check(CallLong(timeline, Timeline_put_Position, position.Ticks));
            var vtbl = *(IntPtr**)controls2;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[Smtc2_UpdateTimelineProperties])(controls2, timeline));
        }
        finally
        {
            Marshal.Release(timeline);
            Marshal.Release(controls2);
        }
    }

    public void Dispose()
    {
        if (_controls == IntPtr.Zero)
            return;

        var vtbl = *(IntPtr**)_controls;
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[Smtc_remove_ButtonPressed])(_controls, _buttonToken);
        Marshal.Release(_updater);
        Marshal.Release(_controls);
        _updater = IntPtr.Zero;
        _controls = IntPtr.Zero;
        _handler.Dispose();
    }

    private IntPtr MusicProperties()
    {
        IntPtr music;
        Check(Call(_updater, Updater_get_MusicProperties, &music));
        return music;
    }

    // Bytes to a RandomAccessStreamReference: a COM IStream over a copy of
    // them (SHCreateMemStream), wrapped as a WinRT stream, wrapped as a
    // reference, which is the only thing the updater's Thumbnail takes.
    private void PutThumbnail(byte[] bytes)
    {
        IntPtr stream;
        fixed (byte* data = bytes)
            stream = SHCreateMemStream(data, (uint)bytes.Length);
        if (stream == IntPtr.Zero)
            return;

        IntPtr randomAccess = IntPtr.Zero;
        var statics = IntPtr.Zero;
        var reference = IntPtr.Zero;
        try
        {
            var iid = IID_IRandomAccessStream;
            Check(CreateRandomAccessStreamOverStream(stream, 0, &iid, &randomAccess));
            statics = ActivationFactory("Windows.Storage.Streams.RandomAccessStreamReference", IID_IRandomAccessStreamReferenceStatics);
            var vtbl = *(IntPtr**)statics;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[Statics_CreateFromStream])(statics, randomAccess, &reference));
            var updaterVtbl = *(IntPtr**)_updater;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)updaterVtbl[Updater_put_Thumbnail])(_updater, reference));
        }
        finally
        {
            if (reference != IntPtr.Zero)
                Marshal.Release(reference);
            if (statics != IntPtr.Zero)
                Marshal.Release(statics);
            if (randomAccess != IntPtr.Zero)
                Marshal.Release(randomAccess);
            Marshal.Release(stream);
        }
    }

    // ---- Calls through a vtable --------------------------------------------

    private static int Call(IntPtr self, int slot)
    {
        var vtbl = *(IntPtr**)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[slot])(self);
    }

    private static int Call(IntPtr self, int slot, void* result)
    {
        var vtbl = *(IntPtr**)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)vtbl[slot])(self, result);
    }

    private static int CallInt(IntPtr self, int slot, int value)
    {
        var vtbl = *(IntPtr**)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[slot])(self, value);
    }

    // WinRT's boolean is one byte.
    private static int CallBool(IntPtr self, int slot, bool value)
    {
        var vtbl = *(IntPtr**)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[slot])(self, value ? (byte)1 : (byte)0);
    }

    // Windows.Foundation.TimeSpan is one Int64 of 100 ns ticks, as .NET's is.
    private static int CallLong(IntPtr self, int slot, long value)
    {
        var vtbl = *(IntPtr**)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[slot])(self, value);
    }

    private static void PutString(IntPtr self, int slot, string? value)
    {
        var hstring = CreateHString(value ?? string.Empty);
        try
        {
            var vtbl = *(IntPtr**)self;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[slot])(self, hstring));
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static string? GetString(IntPtr self, int slot)
    {
        IntPtr hstring;
        Check(Call(self, slot, &hstring));
        try
        {
            uint length;
            var buffer = WindowsGetStringRawBuffer(hstring, &length);
            return length == 0 ? string.Empty : new string(buffer, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr QueryInterface(IntPtr self, Guid iid)
    {
        Check(Marshal.QueryInterface(self, in iid, out var result));
        return result;
    }

    private static IntPtr ActivationFactory(string className, Guid iid)
    {
        var name = CreateHString(className);
        try
        {
            IntPtr factory;
            Check(RoGetActivationFactory(name, &iid, &factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static IntPtr ActivateInstance(string className, Guid iid)
    {
        var name = CreateHString(className);
        IntPtr inspectable;
        try
        {
            Check(RoActivateInstance(name, &inspectable));
        }
        finally
        {
            WindowsDeleteString(name);
        }
        try
        {
            return QueryInterface(inspectable, iid);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static IntPtr CreateHString(string value)
    {
        IntPtr hstring;
        fixed (char* chars = value)
            Check(WindowsCreateString(chars, (uint)value.Length, &hstring));
        return hstring;
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, IntPtr* factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, IntPtr* instance);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(char* sourceString, uint length, IntPtr* hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);

    [DllImport("shlwapi.dll", ExactSpelling = true)]
    private static extern IntPtr SHCreateMemStream(byte* init, uint size);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int CreateRandomAccessStreamOverStream(IntPtr stream, int options, Guid* riid, IntPtr* result);

    // ---- The ButtonPressed delegate ----------------------------------------

    // TypedEventHandler<SystemMediaTransportControls,
    // SystemMediaTransportControlsButtonPressedEventArgs> as a COM object of
    // Flower's own: a vtable of IUnknown plus Invoke, in native memory,
    // pointing back at this object through a GCHandle. Agile, so Windows may
    // call it from any thread, which it does.
    private sealed class ButtonPressedHandler : IDisposable
    {
        // The parameterized interface's IID, which WinRT derives from the
        // type's signature (UUIDv5 of
        // "pinterface({9de1c534-...};rc(...SystemMediaTransportControls;{99fa3ff4-...});rc(...ButtonPressedEventArgs;{b7f47116-...}))").
        private static readonly Guid IID_Handler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        private const int ButtonPressedArgs_get_Button = 6;
        private const int E_NOINTERFACE = unchecked((int)0x80004002);

        private static readonly IntPtr* Vtable = CreateVtable();

        private readonly Action<MediaTransportButton> _onPressed;
        private GCHandle _self;

        public IntPtr Pointer { get; private set; }

        public ButtonPressedHandler(Action<MediaTransportButton> onPressed)
        {
            _onPressed = onPressed;
            _self = GCHandle.Alloc(this);
            var instance = (IntPtr*)NativeMemory.Alloc((nuint)(2 * sizeof(IntPtr)));
            instance[0] = (IntPtr)Vtable;
            instance[1] = GCHandle.ToIntPtr(_self);
            Pointer = (IntPtr)instance;
        }

        // Only once Windows has been told to drop it (remove_ButtonPressed);
        // the reference count is not tracked, so the object must outlive
        // every call Windows could still make.
        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
                return;
            NativeMemory.Free((void*)Pointer);
            Pointer = IntPtr.Zero;
            _self.Free();
        }

        private static IntPtr* CreateVtable()
        {
            var vtable = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
            vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterfaceImpl;
            vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefImpl;
            vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseImpl;
            vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&InvokeImpl;
            return vtable;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int QueryInterfaceImpl(IntPtr self, Guid* iid, IntPtr* result)
        {
            if (*iid == IID_Handler || *iid == IID_IUnknown || *iid == IID_IAgileObject)
            {
                *result = self;
                return 0;
            }
            *result = IntPtr.Zero;
            return E_NOINTERFACE;
        }

        // Lifetime is this class's, not Windows': see Dispose.
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint AddRefImpl(IntPtr self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint ReleaseImpl(IntPtr self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int InvokeImpl(IntPtr self, IntPtr sender, IntPtr args)
        {
            try
            {
                int button;
                var vtbl = *(IntPtr**)args;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[ButtonPressedArgs_get_Button])(args, &button);
                if (hr < 0)
                    return hr;

                var handle = GCHandle.FromIntPtr(((IntPtr*)self)[1]);
                if (handle.Target is ButtonPressedHandler handler)
                    handler._onPressed((MediaTransportButton)button);
                return 0;
            }
            catch (Exception)
            {
                // Nothing may unwind into Windows.
                return unchecked((int)0x80004005);
            }
        }
    }
}
