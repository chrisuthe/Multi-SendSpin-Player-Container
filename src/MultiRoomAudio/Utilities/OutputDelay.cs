namespace MultiRoomAudio.Utilities;

/// <summary>
/// The player's output delay, in the Sendspin spec's own terms.
/// </summary>
/// <remarks>
/// <para>
/// The spec (roles/player/v1) defines <c>output_delay_ms</c> as an integer in 0-5000 that
/// compensates for delay <em>downstream</em> of the audio port - external speakers, amplifiers -
/// by scheduling audio that much earlier. It does not cover delay before the port (DAC latency,
/// audio buffers), which the client compensates itself. Negative values "are not supported and
/// should never be required for any compliant implementation": you cannot have negative
/// downstream delay, because audio can't arrive before it is emitted.
/// </para>
/// <para>
/// This app previously exposed the knob with the opposite sign - "positive = play later" - and
/// negated on the way to the SDK. That kept the UI meaning stable across the SDK v8.0.0 sign flip
/// without migrating saved config, but it put a negative <c>output_delay_ms</c> on the wire for
/// every positive user offset, which the spec does not permit. The knob now means what the spec
/// means, and <see cref="MigrateLegacyDelay"/> converts values saved under the old convention.
/// </para>
/// </remarks>
public static class OutputDelay
{
    /// <summary>Smallest legal output delay per spec.</summary>
    public const int MinMs = 0;

    /// <summary>Largest legal output delay per spec.</summary>
    public const int MaxMs = 5000;

    /// <summary>Constrains a delay to the spec's 0-5000 range.</summary>
    public static int Clamp(int outputDelayMs) => Math.Clamp(outputDelayMs, MinMs, MaxMs);

    /// <summary>
    /// Converts an <c>output_delay_ms</c> to the value the SDK's clock synchronizer expects.
    /// </summary>
    /// <remarks>
    /// Identity since SDK v8.0.0, which aligned <c>StaticDelayMs</c> with the spec: it is
    /// subtracted from converted server timestamps, so a positive delay schedules earlier, exactly
    /// as <c>output_delay_ms</c> means. Kept as a named seam so the convention has one place to
    /// live and one place to be pinned - a future SDK sign flip is absorbed here rather than
    /// silently inverting every player's delay.
    /// </remarks>
    public static double ToStaticDelayMs(int outputDelayMs) => outputDelayMs;

    /// <summary>
    /// Converts a delay saved under the old "positive = play later" convention to an
    /// <c>output_delay_ms</c>.
    /// </summary>
    /// <remarks>
    /// The old value was negated on its way to the SDK, so the behaviour it actually produced is
    /// its negation - that negation is what carries over. A player that played 200ms early
    /// (old <c>-200</c>) becomes <c>+200</c> and is unchanged in behaviour. A player that played
    /// later (old <c>+200</c>) cannot be expressed at all under the spec and settles at 0.
    /// </remarks>
    public static int MigrateLegacyDelay(int legacyDelayMs) => Clamp(-legacyDelayMs);

    /// <summary>
    /// Resolves the output delay to use for a saved player, migrating on first read.
    /// </summary>
    /// <param name="outputDelayMs">The persisted <c>OutputDelayMs</c>, or null if never written.</param>
    /// <param name="legacyDelayMs">The persisted legacy <c>DelayMs</c>.</param>
    /// <remarks>
    /// Presence of <paramref name="outputDelayMs"/> is the migration marker, so this is idempotent:
    /// once a player has been written under the new convention its stale legacy field is ignored,
    /// including when the migrated value is 0. Without that marker a re-read would keep re-negating
    /// the legacy value and walk a player's delay every restart.
    /// </remarks>
    public static int ResolvePersisted(int? outputDelayMs, int legacyDelayMs) =>
        outputDelayMs.HasValue ? Clamp(outputDelayMs.Value) : MigrateLegacyDelay(legacyDelayMs);
}
