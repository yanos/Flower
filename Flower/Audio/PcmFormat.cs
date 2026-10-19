namespace Flower.Audio
{
    // The PCM one pipeline carries: its rate and sample format, with the
    // channel count fixed at GaplessFormat.Channels.
    //
    // A value handed to each piece rather than read off a process-wide static,
    // because the static could be moved by anything: the app negotiates once at
    // startup and never again, but a test suite builds pipelines in parallel,
    // and one test widening the format to S24 moved it under every other test
    // reading it. Now the sink settles it (IAudioSink.Format), and the
    // coordinator, every decoder and the position math are given that answer.
    //
    // A class rather than a struct so a reader on the render thread sees one
    // whole answer or the other, never half of each.
    public sealed record PcmFormat(uint SampleRate, PcmSampleFormat SampleFormat)
    {
        // What a pipeline carries before anything is negotiated: every device
        // check, and every test that does not say otherwise.
        public static PcmFormat Default { get; } = new(GaplessFormat.DefaultSampleRate, GaplessFormat.DefaultSampleFormat);

        public int BytesPerSample => GaplessFormat.BytesPerSampleOf(SampleFormat);

        public int BytesPerFrame => BytesPerSample * (int)GaplessFormat.Channels;
    }
}
