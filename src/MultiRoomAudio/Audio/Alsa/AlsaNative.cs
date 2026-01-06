using System.Runtime.InteropServices;

namespace MultiRoomAudio.Audio.Alsa;

/// <summary>
/// P/Invoke bindings for the ALSA library (libasound.so).
/// Provides low-level PCM audio playback functionality.
/// </summary>
/// <remarks>
/// Reference: https://www.alsa-project.org/alsa-doc/alsa-lib/pcm.html
/// This uses the simplified snd_pcm_set_params API for easier configuration.
/// </remarks>
internal static class AlsaNative
{
    private const string LibAsound = "libasound.so.2";

    #region Enums

    /// <summary>
    /// PCM stream direction.
    /// </summary>
    public enum StreamType
    {
        Playback = 0,
        Capture = 1
    }

    /// <summary>
    /// PCM sample format.
    /// </summary>
    public enum Format
    {
        Unknown = -1,
        S8 = 0,           // Signed 8 bit
        U8 = 1,           // Unsigned 8 bit
        S16_LE = 2,       // Signed 16 bit Little Endian
        S16_BE = 3,       // Signed 16 bit Big Endian
        U16_LE = 4,       // Unsigned 16 bit Little Endian
        U16_BE = 5,       // Unsigned 16 bit Big Endian
        S24_LE = 6,       // Signed 24 bit Little Endian in 4-byte container (low 3 bytes used)
        S24_BE = 7,       // Signed 24 bit Big Endian in 4-byte container (low 3 bytes used)
        U24_LE = 8,       // Unsigned 24 bit Little Endian in 4-byte container (low 3 bytes used)
        U24_BE = 9,       // Unsigned 24 bit Big Endian in 4-byte container (low 3 bytes used)
        S32_LE = 10,      // Signed 32 bit Little Endian
        S32_BE = 11,      // Signed 32 bit Big Endian
        U32_LE = 12,      // Unsigned 32 bit Little Endian
        U32_BE = 13,      // Unsigned 32 bit Big Endian
        FLOAT_LE = 14,    // Float 32 bit Little Endian
        FLOAT_BE = 15,    // Float 32 bit Big Endian
        FLOAT64_LE = 16,  // Float 64 bit Little Endian
        FLOAT64_BE = 17,  // Float 64 bit Big Endian
        S24_3LE = 32,     // Signed 24 bit in 3 bytes Little Endian
        S24_3BE = 33,     // Signed 24 bit in 3 bytes Big Endian
    }

    /// <summary>
    /// PCM access type.
    /// </summary>
    public enum Access
    {
        MmapInterleaved = 0,
        MmapNonInterleaved = 1,
        MmapComplex = 2,
        RwInterleaved = 3,      // Standard read/write interleaved
        RwNonInterleaved = 4
    }

    /// <summary>
    /// PCM state.
    /// </summary>
    public enum State
    {
        Open = 0,
        Setup = 1,
        Prepared = 2,
        Running = 3,
        XRun = 4,
        Draining = 5,
        Paused = 6,
        Suspended = 7,
        Disconnected = 8
    }

    #endregion

    #region PCM Functions

    /// <summary>
    /// Open a PCM device.
    /// </summary>
    /// <param name="pcm">Pointer to the PCM handle.</param>
    /// <param name="name">Device name (e.g., "default", "hw:0,0", "zone1").</param>
    /// <param name="stream">Stream direction (playback or capture).</param>
    /// <param name="mode">Open mode (0 = blocking, 1 = non-blocking).</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_open")]
    public static extern int Open(out IntPtr pcm, string name, StreamType stream, int mode);

    /// <summary>
    /// Close a PCM device.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_close")]
    public static extern int Close(IntPtr pcm);

    /// <summary>
    /// Set PCM parameters using the simplified API.
    /// This is the easiest way to configure a PCM device for basic use.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="format">Sample format.</param>
    /// <param name="access">Access type.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="rate">Sample rate in Hz.</param>
    /// <param name="softResample">Allow software resampling (1 = yes, 0 = no).</param>
    /// <param name="latency">Desired latency in microseconds.</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_set_params")]
    public static extern int SetParams(
        IntPtr pcm,
        Format format,
        Access access,
        uint channels,
        uint rate,
        int softResample,
        uint latency);

    /// <summary>
    /// Write interleaved frames to a PCM device.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="buffer">Pointer to audio data buffer.</param>
    /// <param name="frames">Number of frames to write.</param>
    /// <returns>Number of frames written, or negative error code.</returns>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_writei")]
    public static extern nint WriteInterleaved(IntPtr pcm, IntPtr buffer, nuint frames);

    /// <summary>
    /// Prepare PCM for use.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_prepare")]
    public static extern int Prepare(IntPtr pcm);

    /// <summary>
    /// Drain the PCM buffer (wait for all pending frames to be played).
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_drain")]
    public static extern int Drain(IntPtr pcm);

    /// <summary>
    /// Drop all pending frames (immediate stop).
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_drop")]
    public static extern int Drop(IntPtr pcm);

    /// <summary>
    /// Resume from suspend.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_resume")]
    public static extern int Resume(IntPtr pcm);

    /// <summary>
    /// Get current PCM state.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_state")]
    public static extern State GetState(IntPtr pcm);

    /// <summary>
    /// Recover from an error (underrun, suspend, etc.).
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="err">Error code to recover from.</param>
    /// <param name="silent">Suppress error messages (1 = yes).</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_recover")]
    public static extern int Recover(IntPtr pcm, int err, int silent);

    /// <summary>
    /// Get available frames in buffer.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_avail_update")]
    public static extern nint AvailUpdate(IntPtr pcm);

    /// <summary>
    /// Wait for PCM to become ready.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="timeout">Timeout in milliseconds (-1 = infinite).</param>
    /// <returns>Positive on success (frames available), 0 on timeout, negative on error.</returns>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_wait")]
    public static extern int Wait(IntPtr pcm, int timeout);

    #endregion

    #region Error Handling

    /// <summary>
    /// Get error message string.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_strerror")]
    private static extern IntPtr StrErrorNative(int errnum);

    /// <summary>
    /// Get error message as managed string.
    /// </summary>
    public static string GetErrorMessage(int errorCode)
    {
        var ptr = StrErrorNative(errorCode);
        return Marshal.PtrToStringAnsi(ptr) ?? $"Unknown error {errorCode}";
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Bytes per sample for a given format.
    /// </summary>
    public static int BytesPerSample(Format format)
    {
        return format switch
        {
            Format.S8 or Format.U8 => 1,
            Format.S16_LE or Format.S16_BE or Format.U16_LE or Format.U16_BE => 2,
            Format.S24_3LE or Format.S24_3BE => 3,
            Format.S24_LE or Format.S24_BE or Format.U24_LE or Format.U24_BE or
            Format.S32_LE or Format.S32_BE or Format.U32_LE or Format.U32_BE or
            Format.FLOAT_LE or Format.FLOAT_BE => 4,
            Format.FLOAT64_LE or Format.FLOAT64_BE => 8,
            _ => 4 // Default to 32-bit
        };
    }

    /// <summary>
    /// Calculate bytes per frame (bytes per sample * channels).
    /// </summary>
    public static int BytesPerFrame(Format format, int channels)
    {
        return BytesPerSample(format) * channels;
    }

    #endregion

    #region Error Codes

    // Common ALSA error codes (negated errno values)
    public const int EPIPE = -32;       // Broken pipe (underrun)
    public const int ESTRPIPE = -86;    // Streams pipe error (suspend)
    public const int EAGAIN = -11;      // Try again (non-blocking)
    public const int EINTR = -4;        // Interrupted system call

    #endregion

    #region Hardware Parameters (Capability Detection)

    /// <summary>
    /// Allocate a hardware parameters structure.
    /// Must be freed with HwParamsFree.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_malloc")]
    public static extern int HwParamsMalloc(out IntPtr hwParams);

    /// <summary>
    /// Free a hardware parameters structure.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_free")]
    public static extern void HwParamsFree(IntPtr hwParams);

    /// <summary>
    /// Fill params with a full configuration space for a PCM.
    /// This retrieves ALL possible hardware configurations.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_any")]
    public static extern int HwParamsAny(IntPtr pcm, IntPtr hwParams);

    /// <summary>
    /// Extract minimum sample rate from a configuration space.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_get_rate_min")]
    public static extern int GetRateMin(IntPtr hwParams, out uint val, out int dir);

    /// <summary>
    /// Extract maximum sample rate from a configuration space.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_get_rate_max")]
    public static extern int GetRateMax(IntPtr hwParams, out uint val, out int dir);

    /// <summary>
    /// Test if a specific sample rate is supported.
    /// Returns 0 if supported, negative error code otherwise.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="hwParams">Hardware params filled with HwParamsAny.</param>
    /// <param name="val">Sample rate to test.</param>
    /// <param name="dir">Direction: -1 = less, 0 = exact, 1 = greater.</param>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_test_rate")]
    public static extern int TestRate(IntPtr pcm, IntPtr hwParams, uint val, int dir);

    /// <summary>
    /// Test if a specific format is supported.
    /// Returns 0 if supported, negative error code otherwise.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_test_format")]
    public static extern int TestFormat(IntPtr pcm, IntPtr hwParams, Format format);

    /// <summary>
    /// Extract minimum channel count from a configuration space.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_get_channels_min")]
    public static extern int GetChannelsMin(IntPtr hwParams, out uint val);

    /// <summary>
    /// Extract maximum channel count from a configuration space.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_get_channels_max")]
    public static extern int GetChannelsMax(IntPtr hwParams, out uint val);

    #endregion

    #region Format Mask (Bulk Format Query)

    /// <summary>
    /// Allocate a format mask structure.
    /// Must be freed with FormatMaskFree.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_format_mask_malloc")]
    public static extern int FormatMaskMalloc(out IntPtr mask);

    /// <summary>
    /// Free a format mask structure.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_format_mask_free")]
    public static extern void FormatMaskFree(IntPtr mask);

    /// <summary>
    /// Get format mask from hardware parameters.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_hw_params_get_format_mask")]
    public static extern void GetFormatMask(IntPtr hwParams, IntPtr mask);

    /// <summary>
    /// Test if a format is present in the mask.
    /// Returns non-zero if format is in mask.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_format_mask_test")]
    public static extern int FormatMaskTest(IntPtr mask, Format format);

    /// <summary>
    /// Get the name of a format.
    /// </summary>
    [DllImport(LibAsound, EntryPoint = "snd_pcm_format_name")]
    private static extern IntPtr FormatNameNative(Format format);

    /// <summary>
    /// Get format name as managed string.
    /// </summary>
    public static string GetFormatName(Format format)
    {
        var ptr = FormatNameNative(format);
        return Marshal.PtrToStringAnsi(ptr) ?? format.ToString();
    }

    /// <summary>
    /// Get bit depth for a format.
    /// </summary>
    public static int GetBitDepth(Format format)
    {
        return format switch
        {
            Format.S8 or Format.U8 => 8,
            Format.S16_LE or Format.S16_BE or Format.U16_LE or Format.U16_BE => 16,
            Format.S24_LE or Format.S24_BE or Format.U24_LE or Format.U24_BE or
            Format.S24_3LE or Format.S24_3BE => 24,
            Format.S32_LE or Format.S32_BE or Format.U32_LE or Format.U32_BE or
            Format.FLOAT_LE or Format.FLOAT_BE => 32,
            Format.FLOAT64_LE or Format.FLOAT64_BE => 64,
            _ => 0
        };
    }

    #endregion
}
