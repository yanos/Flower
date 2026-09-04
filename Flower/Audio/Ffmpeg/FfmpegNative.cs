using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Flower.Audio.Ffmpeg
{
    // P/Invoke for native/ffmpeg/flower_ffmpeg.h. Nothing above this file
    // knows FFmpeg exists; nothing in it knows anything about FFmpeg either,
    // because the façade's whole purpose is that its ABI is eight functions
    // over ints and byte buffers. See that header for why.
    internal static class FfmpegNative
    {
        internal const string Library = "flower_ffmpeg";

        // Must match FLOWER_FFMPEG_ABI_VERSION. Checked once at load, because
        // the failure mode of a mismatched library is a struct read at the
        // wrong offsets rather than an error.
        internal const int ExpectedAbiVersion = 1;

        internal const int Ok = 0;
        internal const int EndOfStream = 1;

        internal const int SeekSize = 0x10000;

        static FfmpegNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(FfmpegNative).Assembly, Resolve);
        }

        // The façade is not on a default search path in any of the three
        // situations that matter - a dev build reading it out of
        // native/ffmpeg/artifacts, a test run, and a packaged app - so each is
        // named rather than left to the loader. FLOWER_FFMPEG is first so a
        // bisect against a differently-built FFmpeg needs no rebuild.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Library)
                return IntPtr.Zero;

            if (Environment.GetEnvironmentVariable("FLOWER_FFMPEG") is { Length: > 0 } explicitPath
                && NativeLibrary.TryLoad(explicitPath, out var fromEnvironment))
                return fromEnvironment;

            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }

            return NativeLibrary.TryLoad(Library, assembly, path, out var byName) ? byName : IntPtr.Zero;
        }

        private static string[] Candidates()
        {
            var file = OperatingSystem.IsWindows() ? "flower_ffmpeg.dll"
                : OperatingSystem.IsMacOS() ? "libflower_ffmpeg.dylib"
                : "libflower_ffmpeg.so";

            var baseDirectory = AppContext.BaseDirectory;
            var platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "macos"
                : "linux";

            // Walking up to the repo root is a development convenience only -
            // a packaged app finds the library beside itself on the first
            // candidate and never looks further.
            var repoRelative = Path.Combine("native", "ffmpeg", "artifacts", platform, file);
            var walked = new string[6];
            var directory = baseDirectory;
            for (var i = 0; i < walked.Length; i++)
            {
                walked[i] = Path.Combine(directory, repoRelative);
                directory = Path.Combine(directory, "..");
            }

            var candidates = new string[walked.Length + 1];
            candidates[0] = Path.Combine(baseDirectory, file);
            Array.Copy(walked, 0, candidates, 1, walked.Length);
            return candidates;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DecoderFormat
        {
            public int SampleRate;
            public int Channels;
            public int SampleFormat;
            public int SourceBitDepth;
            public int SourceSampleRate;
            public int SourceChannels;
            public long DurationMs;
        }

        [DllImport(Library, EntryPoint = "flower_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AbiVersion();

        [DllImport(Library, EntryPoint = "flower_decoder_open_path", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int OpenPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                            int requestedFormat, int requestedSampleRate, int requestedChannels,
                                            out IntPtr decoder);

        [DllImport(Library, EntryPoint = "flower_decoder_open_io", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int OpenIo(IntPtr opaque,
                                          IntPtr read, IntPtr seek,
                                          long size, int seekable,
                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string? formatHint,
                                          int requestedFormat, int requestedSampleRate, int requestedChannels,
                                          out IntPtr decoder);

        [DllImport(Library, EntryPoint = "flower_decoder_get_format", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetFormat(IntPtr decoder, out DecoderFormat format);

        [DllImport(Library, EntryPoint = "flower_decoder_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Read(IntPtr decoder, byte* buffer, int bufferBytes, out int bytesWritten);

        [DllImport(Library, EntryPoint = "flower_decoder_seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Seek(IntPtr decoder, long positionMs, out long landedMs);

        [DllImport(Library, EntryPoint = "flower_decoder_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr decoder);

        [DllImport(Library, EntryPoint = "flower_error_string", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void ErrorString(int code, byte* buffer, int bufferBytes);

        internal static unsafe string Describe(int code)
        {
            const int capacity = 256;
            var buffer = stackalloc byte[capacity];
            ErrorString(code, buffer, capacity);
            return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {code}";
        }
    }
}
