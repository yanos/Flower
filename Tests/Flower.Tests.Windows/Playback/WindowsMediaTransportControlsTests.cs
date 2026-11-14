using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Flower.Platform;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Windows.Playback;

// WindowsMediaTransportControls calls WinRT through hand-numbered vtable
// slots, so the only proof that each slot is the method it is meant to be is
// Windows itself: these publish a song to a real window's controls and read
// back what Windows now holds. A slot one off would throw, or store the title
// as the artist.
[SupportedOSPlatform("windows10.0.10240")]
public class WindowsMediaTransportControlsTests
{
    private static void OnWindowThread(Action<IntPtr> test)
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240), "Needs Windows' media transport controls.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // A hidden top-level window, as the controls want one: no
            // WS_VISIBLE, no parent.
            var hwnd = CreateWindowExW(0, "STATIC", "Flower media controls test", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                Assert.NotEqual(IntPtr.Zero, hwnd);
                test(hwnd);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        });
        // The UI thread Flower calls these from is an STA one.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // A Windows without the media stack (a Server Core image) has no class
    // to activate; that is a machine to skip on, not a slot that is wrong.
    private static WindowsMediaTransportControls Open(IntPtr hwnd)
    {
        const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
        try
        {
            return WindowsMediaTransportControls.ForWindow(hwnd);
        }
        catch (COMException ex) when (ex.HResult == REGDB_E_CLASSNOTREG)
        {
            Assert.Skip("This Windows has no media transport controls to activate.");
            throw;
        }
    }

    [Fact]
    public void A_song_published_is_the_song_Windows_shows()
    {
        OnWindowThread(hwnd =>
        {
            using var controls = Open(hwnd);

            controls.SetMusic("Hey Jude", "The Beatles", "1", SyntheticPng.Build(64, 64));
            controls.SetTimeline(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(431));
            controls.PlaybackStatus = MediaPlaybackStatus.Playing;

            Assert.Equal(("Hey Jude", "The Beatles", "1"), controls.ReadMusic());
            Assert.Equal(MediaPlaybackStatus.Playing, controls.PlaybackStatus);

            controls.PlaybackStatus = MediaPlaybackStatus.Paused;
            Assert.Equal(MediaPlaybackStatus.Paused, controls.PlaybackStatus);
        });
    }

    [Fact]
    public void Clearing_leaves_Windows_showing_no_song()
    {
        OnWindowThread(hwnd =>
        {
            using var controls = Open(hwnd);
            controls.SetMusic("Hey Jude", "The Beatles", "1", null);

            controls.ClearMusic();
            controls.PlaybackStatus = MediaPlaybackStatus.Stopped;

            Assert.Equal(("", "", ""), controls.ReadMusic());
            Assert.Equal(MediaPlaybackStatus.Stopped, controls.PlaybackStatus);
        });
    }

    [Fact]
    public void A_song_published_after_clearing_has_only_its_own_metadata()
    {
        OnWindowThread(hwnd =>
        {
            using var controls = Open(hwnd);
            controls.SetMusic("Hey Jude", "The Beatles", "1", null);

            controls.ClearMusic();
            Assert.Equal(("", "", ""), controls.ReadMusic());

            controls.SetMusic("Another song", null, null, null);

            Assert.Equal(("Another song", "", ""), controls.ReadMusic());
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
