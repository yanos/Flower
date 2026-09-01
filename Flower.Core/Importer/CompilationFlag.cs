namespace Flower.Importer
{
    // The "part of a compilation" flag, read and written in one place.
    //
    // It is the one tag Flower touches that has no home on the generic
    // TagLib.Tag every other field goes through: each container spells it
    // differently (ID3v2 TCMP, MP4 cpil, Xiph COMPILATION) and TagLib exposes it
    // only on the concrete tag types. So it needs a per-format lookup, and
    // having the read and the write sit next to each other is what stops them
    // covering different sets of formats - which is exactly the drift that
    // would show up as "I ticked Compilation, it saved, and the next rescan
    // unticked it".
    //
    // Covers every format Importer scans (mp3 via Id3v2, m4a/alac via
    // Apple/Mpeg4, flac via Xiph). WAV has no equivalent tag type in TagLib# at
    // all, so Write finds nothing to write to and says so by returning false -
    // which is why the caller only records the new value on the Track when the
    // write actually landed.
    //
    // See Track.IsCompilation/EffectiveAlbumArtist for why it matters: it is the
    // one reliable, non-heuristic signal that a various-artists album should be
    // grouped as a single tile instead of fragmenting by each track's Artists.
    public static class CompilationFlag
    {
        public static bool Read(TagLib.File file) =>
            file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3v2 && id3v2.IsCompilation
            || file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple && apple.IsCompilation
            || file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph && xiph.IsCompilation;

        // create: true, unlike the read above - a track that has never been
        // marked as a compilation may well have no ID3v2 tag at all yet, and
        // refusing to make one would make the tick silently do nothing on
        // exactly the files most likely to need it.
        public static bool Write(TagLib.File file, bool value)
        {
            var written = false;

            if (file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3v2)
            {
                id3v2.IsCompilation = value;
                written = true;
            }

            if (file.GetTag(TagLib.TagTypes.Apple, true) is TagLib.Mpeg4.AppleTag apple)
            {
                apple.IsCompilation = value;
                written = true;
            }

            if (file.GetTag(TagLib.TagTypes.Xiph, true) is TagLib.Ogg.XiphComment xiph)
            {
                xiph.IsCompilation = value;
                written = true;
            }

            return written;
        }
    }
}
