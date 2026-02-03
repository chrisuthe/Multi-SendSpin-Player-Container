using System.Runtime.InteropServices;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// P/Invoke bindings for PulseAudio APIs.
/// </summary>
/// <remarks>
/// Includes both the simple API (pa_simple) and async API (pa_stream with pa_threaded_mainloop).
/// The async API is used for accurate latency measurement via write callbacks.
///
/// References:
/// - Simple API: https://freedesktop.org/software/pulseaudio/doxygen/simple.html
/// - Async API: https://freedesktop.org/software/pulseaudio/doxygen/async.html
/// </remarks>
internal static class PulseAudioNative
{
    private const string LibPulseSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";

    /// <summary>
    /// Sample format for audio data.
    /// </summary>
    public enum SampleFormat
    {
        U8 = 0,           // Unsigned 8 bit PCM
        ALAW = 1,         // 8 bit a-Law
        ULAW = 2,         // 8 bit mu-Law
        S16LE = 3,        // Signed 16 bit PCM, little endian
        S16BE = 4,        // Signed 16 bit PCM, big endian
        FLOAT32LE = 5,    // 32 bit IEEE floating point, little endian
        FLOAT32BE = 6,    // 32 bit IEEE floating point, big endian
        S32LE = 7,        // Signed 32 bit PCM, little endian
        S32BE = 8,        // Signed 32 bit PCM, big endian
        S24LE = 9,        // Signed 24 bit PCM packed, little endian
        S24BE = 10,       // Signed 24 bit PCM packed, big endian
        S24_32LE = 11,    // Signed 24 bit PCM in LSB of 32 bit words, little endian
        S24_32BE = 12,    // Signed 24 bit PCM in LSB of 32 bit words, big endian
        MAX = 13,
        Invalid = -1
    }

    /// <summary>
    /// Stream direction.
    /// </summary>
    public enum StreamDirection
    {
        NoDirection = 0,
        Playback = 1,
        Record = 2,
        Upload = 3
    }

    /// <summary>
    /// Sample specification structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public SampleFormat Format;
        public uint Rate;
        public byte Channels;
    }

    /// <summary>
    /// Buffer attributes for stream configuration.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        /// <summary>Maximum length of the buffer in bytes.</summary>
        public uint MaxLength;
        /// <summary>Target length of the buffer in bytes (playback only).</summary>
        public uint TLength;
        /// <summary>Pre-buffering in bytes.</summary>
        public uint PreBuf;
        /// <summary>Minimum request size in bytes.</summary>
        public uint MinReq;
        /// <summary>Fragment size in bytes (record only).</summary>
        public uint FragSize;
    }

    /// <summary>
    /// Create a new connection to the PulseAudio server for simple playback or recording.
    /// </summary>
    /// <param name="server">Server name, or NULL for default.</param>
    /// <param name="name">A descriptive name for this client.</param>
    /// <param name="dir">Stream direction (playback or record).</param>
    /// <param name="dev">Sink/source name, or NULL for default.</param>
    /// <param name="streamName">A descriptive name for this stream.</param>
    /// <param name="ss">Sample specification.</param>
    /// <param name="map">Channel map, or NULL for default.</param>
    /// <param name="attr">Buffer attributes, or NULL for default.</param>
    /// <param name="error">Pointer to store error code on failure.</param>
    /// <returns>Handle to the connection, or NULL on failure.</returns>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_new")]
    public static extern IntPtr SimpleNew(
        string? server,
        string name,
        StreamDirection dir,
        string? dev,
        string streamName,
        ref SampleSpec ss,
        IntPtr map,  // pa_channel_map*, NULL for default
        IntPtr attr, // pa_buffer_attr*, NULL for default
        out int error);

    /// <summary>
    /// Create a new connection with buffer attributes.
    /// </summary>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_new")]
    public static extern IntPtr SimpleNewWithAttr(
        string? server,
        string name,
        StreamDirection dir,
        string? dev,
        string streamName,
        ref SampleSpec ss,
        IntPtr map,
        ref BufferAttr attr,
        out int error);

    /// <summary>
    /// Close and free the connection.
    /// </summary>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_free")]
    public static extern void SimpleFree(IntPtr s);

    /// <summary>
    /// Write audio data to the server.
    /// </summary>
    /// <param name="s">The connection handle.</param>
    /// <param name="data">Pointer to audio data.</param>
    /// <param name="bytes">Number of bytes to write.</param>
    /// <param name="error">Pointer to store error code on failure.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_write")]
    public static extern int SimpleWrite(IntPtr s, IntPtr data, UIntPtr bytes, out int error);

    /// <summary>
    /// Wait until all data in the buffer has been played.
    /// </summary>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_drain")]
    public static extern int SimpleDrain(IntPtr s, out int error);

    /// <summary>
    /// Flush the playback buffer.
    /// </summary>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_flush")]
    public static extern int SimpleFlush(IntPtr s, out int error);

    /// <summary>
    /// Get the playback latency in microseconds.
    /// </summary>
    [DllImport(LibPulseSimple, EntryPoint = "pa_simple_get_latency")]
    public static extern ulong SimpleGetLatency(IntPtr s, out int error);

    /// <summary>
    /// Get a human-readable error message for an error code.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_strerror")]
    public static extern IntPtr Strerror(int error);

    /// <summary>
    /// Get the error message as a managed string.
    /// </summary>
    public static string GetErrorMessage(int error)
    {
        var ptr = Strerror(error);
        return Marshal.PtrToStringAnsi(ptr) ?? $"Unknown error {error}";
    }

    /// <summary>
    /// Calculate bytes per frame for a sample spec.
    /// </summary>
    public static int BytesPerFrame(ref SampleSpec ss)
    {
        var bytesPerSample = ss.Format switch
        {
            SampleFormat.U8 or SampleFormat.ALAW or SampleFormat.ULAW => 1,
            SampleFormat.S16LE or SampleFormat.S16BE => 2,
            SampleFormat.S24LE or SampleFormat.S24BE => 3,
            SampleFormat.FLOAT32LE or SampleFormat.FLOAT32BE or
            SampleFormat.S32LE or SampleFormat.S32BE or
            SampleFormat.S24_32LE or SampleFormat.S24_32BE => 4,
            _ => 4
        };
        return bytesPerSample * ss.Channels;
    }

    /// <summary>
    /// Calculate bytes for a given duration in milliseconds.
    /// </summary>
    public static uint BytesForMs(ref SampleSpec ss, int milliseconds)
    {
        var bytesPerFrame = BytesPerFrame(ref ss);
        return (uint)(ss.Rate * bytesPerFrame * milliseconds / 1000);
    }

    #region Async API - Enums

    /// <summary>
    /// Context state machine states.
    /// </summary>
    public enum ContextState
    {
        Unconnected = 0,
        Connecting = 1,
        Authorizing = 2,
        SettingName = 3,
        Ready = 4,
        Failed = 5,
        Terminated = 6
    }

    /// <summary>
    /// Stream state machine states.
    /// </summary>
    public enum StreamState
    {
        Unconnected = 0,
        Creating = 1,
        Ready = 2,
        Failed = 3,
        Terminated = 4
    }

    /// <summary>
    /// Flags for stream connections.
    /// </summary>
    [Flags]
    public enum StreamFlags : uint
    {
        None = 0,
        StartCorked = 0x0001,
        InterpolateTiming = 0x0002,
        NotMonotonic = 0x0004,
        AutoTimingUpdate = 0x0008,
        NoRemap = 0x0010,
        NoRemix = 0x0020,
        FixFormat = 0x0040,
        FixRate = 0x0080,
        FixChannels = 0x0100,
        DontMove = 0x0200,
        VariableRate = 0x0400,
        PeakDetect = 0x0800,
        StartMuted = 0x1000,
        AdjustLatency = 0x2000,
        EarlyRequests = 0x4000,
        DontInhibitAutoSuspend = 0x8000,
        StartUnmuted = 0x10000,
        FailOnSuspend = 0x20000,
        RelativeVolume = 0x40000,
        Passthrough = 0x80000
    }

    /// <summary>
    /// Seek mode for stream writes.
    /// </summary>
    public enum SeekMode
    {
        Relative = 0,
        Absolute = 1,
        RelativeOnRead = 2,
        RelativeEnd = 3
    }

    #endregion

    #region Async API - Callbacks

    /// <summary>
    /// Callback for context state changes.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ContextNotifyCallback(IntPtr context, IntPtr userdata);

    /// <summary>
    /// Callback for stream state changes.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StreamNotifyCallback(IntPtr stream, IntPtr userdata);

    /// <summary>
    /// Callback for stream write requests (called when PA needs more audio data).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StreamRequestCallback(IntPtr stream, UIntPtr nbytes, IntPtr userdata);

    #endregion

    #region Async API - Threaded Mainloop

    /// <summary>
    /// Create a new threaded mainloop object.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_new")]
    public static extern IntPtr ThreadedMainloopNew();

    /// <summary>
    /// Free a threaded mainloop object.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_free")]
    public static extern void ThreadedMainloopFree(IntPtr m);

    /// <summary>
    /// Start the mainloop thread.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_start")]
    public static extern int ThreadedMainloopStart(IntPtr m);

    /// <summary>
    /// Stop the mainloop thread.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_stop")]
    public static extern void ThreadedMainloopStop(IntPtr m);

    /// <summary>
    /// Lock the mainloop. Must be called before any PA API calls from other threads.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_lock")]
    public static extern void ThreadedMainloopLock(IntPtr m);

    /// <summary>
    /// Unlock the mainloop.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_unlock")]
    public static extern void ThreadedMainloopUnlock(IntPtr m);

    /// <summary>
    /// Wait for a signal on the mainloop.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_wait")]
    public static extern void ThreadedMainloopWait(IntPtr m);

    /// <summary>
    /// Signal the mainloop to wake waiting threads.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_signal")]
    public static extern void ThreadedMainloopSignal(IntPtr m, int waitForAccept);

    /// <summary>
    /// Get the mainloop API vtable.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_get_api")]
    public static extern IntPtr ThreadedMainloopGetApi(IntPtr m);

    /// <summary>
    /// Check if the current thread is the mainloop thread.
    /// </summary>
    /// <returns>Non-zero if called from within the mainloop thread, zero otherwise.</returns>
    /// <remarks>
    /// Use this to avoid calling pa_threaded_mainloop_lock() from within callbacks,
    /// which would cause an assertion failure (deadlock prevention).
    /// </remarks>
    [DllImport(LibPulse, EntryPoint = "pa_threaded_mainloop_in_thread")]
    public static extern int ThreadedMainloopInThread(IntPtr m);

    #endregion

    #region Async API - Context

    /// <summary>
    /// Create a new context.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_new")]
    public static extern IntPtr ContextNew(IntPtr mainloopApi, string name);

    /// <summary>
    /// Free a context.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_unref")]
    public static extern void ContextUnref(IntPtr context);

    /// <summary>
    /// Set the context state callback.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_set_state_callback")]
    public static extern void ContextSetStateCallback(IntPtr context, ContextNotifyCallback cb, IntPtr userdata);

    /// <summary>
    /// Connect to the PulseAudio server.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_connect")]
    public static extern int ContextConnect(IntPtr context, string? server, uint flags, IntPtr api);

    /// <summary>
    /// Disconnect from the server.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_disconnect")]
    public static extern void ContextDisconnect(IntPtr context);

    /// <summary>
    /// Get the current context state.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_get_state")]
    public static extern ContextState ContextGetState(IntPtr context);

    /// <summary>
    /// Get the error number of the last failed operation on the context.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_context_errno")]
    public static extern int ContextErrno(IntPtr context);

    /// <summary>
    /// Get the error message for the last failed operation on the context.
    /// </summary>
    public static string GetContextError(IntPtr context)
    {
        var errno = ContextErrno(context);
        return GetErrorMessage(errno);
    }

    #endregion

    #region Async API - Stream

    /// <summary>
    /// Create a new stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_new")]
    public static extern IntPtr StreamNew(IntPtr context, string name, ref SampleSpec ss, IntPtr channelMap);

    /// <summary>
    /// Free a stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_unref")]
    public static extern void StreamUnref(IntPtr stream);

    /// <summary>
    /// Get the stream state.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_get_state")]
    public static extern StreamState StreamGetState(IntPtr stream);

    /// <summary>
    /// Connect the stream for playback.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_connect_playback")]
    public static extern int StreamConnectPlayback(
        IntPtr stream,
        string? dev,
        ref BufferAttr attr,
        StreamFlags flags,
        IntPtr volume,
        IntPtr syncStream);

    /// <summary>
    /// Connect the stream for playback with default attributes.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_connect_playback")]
    public static extern int StreamConnectPlaybackDefault(
        IntPtr stream,
        string? dev,
        IntPtr attr,
        StreamFlags flags,
        IntPtr volume,
        IntPtr syncStream);

    /// <summary>
    /// Disconnect the stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_disconnect")]
    public static extern int StreamDisconnect(IntPtr stream);

    /// <summary>
    /// Set the write callback (called when PA needs more audio).
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_set_write_callback")]
    public static extern void StreamSetWriteCallback(IntPtr stream, StreamRequestCallback cb, IntPtr userdata);

    /// <summary>
    /// Set the underflow callback.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_set_underflow_callback")]
    public static extern void StreamSetUnderflowCallback(IntPtr stream, StreamNotifyCallback cb, IntPtr userdata);

    /// <summary>
    /// Set the overflow callback.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_set_overflow_callback")]
    public static extern void StreamSetOverflowCallback(IntPtr stream, StreamNotifyCallback cb, IntPtr userdata);

    /// <summary>
    /// Set the stream state callback.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_set_state_callback")]
    public static extern void StreamSetStateCallback(IntPtr stream, StreamNotifyCallback cb, IntPtr userdata);

    /// <summary>
    /// Write data to the stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_write")]
    public static extern int StreamWrite(
        IntPtr stream,
        IntPtr data,
        UIntPtr nbytes,
        IntPtr freeCallback,
        long offset,
        SeekMode seek);

    /// <summary>
    /// Get how much can be written to the stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_writable_size")]
    public static extern UIntPtr StreamWritableSize(IntPtr stream);

    /// <summary>
    /// Get the stream latency.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="usec">Output: latency in microseconds.</param>
    /// <param name="negative">Output: 1 if negative (stream is ahead).</param>
    /// <returns>0 on success, negative on error (timing info not available yet).</returns>
    [DllImport(LibPulse, EntryPoint = "pa_stream_get_latency")]
    public static extern int StreamGetLatency(IntPtr stream, out ulong usec, out int negative);

    /// <summary>
    /// Get the current playback time of the stream in microseconds.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="usec">Output: playback time in microseconds (sound card clock domain).</param>
    /// <returns>0 on success, negative on error.</returns>
    /// <remarks>
    /// This returns time in the sound card's clock domain, making it immune to VM
    /// wall clock issues. The time is based on how much audio has actually been
    /// played through the DAC, not wall clock time.
    /// Requires PA_STREAM_AUTO_TIMING_UPDATE flag for automatic updates.
    /// </remarks>
    [DllImport(LibPulse, EntryPoint = "pa_stream_get_time")]
    public static extern int StreamGetTime(IntPtr stream, out ulong usec);

    /// <summary>
    /// Cork (pause) or uncork (resume) the stream.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_cork")]
    public static extern IntPtr StreamCork(IntPtr stream, int cork, IntPtr callback, IntPtr userdata);

    /// <summary>
    /// Flush the stream buffer.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_flush")]
    public static extern IntPtr StreamFlush(IntPtr stream, IntPtr callback, IntPtr userdata);

    /// <summary>
    /// Drain the stream (wait for all data to play).
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_drain")]
    public static extern IntPtr StreamDrain(IntPtr stream, IntPtr callback, IntPtr userdata);

    /// <summary>
    /// Update the timing info.
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_update_timing_info")]
    public static extern IntPtr StreamUpdateTimingInfo(IntPtr stream, IntPtr callback, IntPtr userdata);

    /// <summary>
    /// Check if the stream is corked (paused).
    /// </summary>
    [DllImport(LibPulse, EntryPoint = "pa_stream_is_corked")]
    public static extern int StreamIsCorked(IntPtr stream);

    #endregion
}
