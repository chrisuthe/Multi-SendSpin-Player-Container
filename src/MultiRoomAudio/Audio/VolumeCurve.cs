namespace MultiRoomAudio.Audio;

/// <summary>
/// Maps a 0-100 user-facing volume onto the linear amplitude gain the SDK multiplies
/// samples by.
/// </summary>
/// <remarks>
/// Feeding the percentage straight through as <c>percent / 100f</c> makes the slider
/// linear in amplitude, which does not match how loudness is heard (#263). Perceived
/// loudness roughly doubles per +10 dB, so a linear amplitude taper is louder than the
/// number suggests everywhere and crams the audible range into the bottom of the slider:
///
/// <code>
///   slider  linear gain     dB   ~perceived
///       50         0.50   -6.0         66%
///       10         0.10  -20.0         25%
///        2         0.02  -34.0          9%
/// </code>
///
/// A cubic taper is the convention the rest of the audio stack already uses -
/// PulseAudio's <c>pa_sw_volume_to_linear</c> is literally <c>(v/PA_VOLUME_NORM)^3</c>,
/// and ALSA's mixers behave the same way - so it also keeps a player's software volume
/// consistent with the hardware volume applied through <c>pactl</c>.
/// </remarks>
internal static class VolumeCurve
{
    /// <summary>
    /// Converts a 0-100 volume percentage to the amplitude gain to apply. Values outside
    /// 0-100 are clamped. 0 returns silence and 100 returns unity gain.
    /// </summary>
    public static float ToGain(int percent)
    {
        var normalized = Math.Clamp(percent, 0, 100) / 100.0;
        return (float)(normalized * normalized * normalized);
    }
}
