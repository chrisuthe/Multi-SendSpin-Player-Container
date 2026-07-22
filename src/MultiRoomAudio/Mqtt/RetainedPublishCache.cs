namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Remembers the last payload published to each retained topic so a byte-identical
/// republish can be skipped.
/// </summary>
/// <remarks>
/// The bridge republishes every discovery config and every state topic whenever any
/// player or trigger changes, because <c>PlayersChanged</c>/<c>TriggersChanged</c> don't
/// say which entity changed. That turned a single player state change into ~40 broker
/// messages (#256). For a retained topic the broker already holds the last value, so
/// re-sending the same bytes tells subscribers nothing — suppressing it leaves only the
/// entities that genuinely changed on the wire.
///
/// Not thread-safe by itself; callers must serialize access (MqttService does so under
/// its publish lock).
/// </remarks>
internal sealed class RetainedPublishCache
{
    private readonly Dictionary<string, string> _lastPayloads = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns whether <paramref name="payload"/> needs to go on the wire. Non-retained
    /// messages always do; a retained message only does when its payload differs from the
    /// last one recorded for <paramref name="topic"/>.
    /// </summary>
    public bool ShouldPublish(string topic, string payload, bool retain)
        => !retain || !_lastPayloads.TryGetValue(topic, out var previous) || previous != payload;

    /// <summary>
    /// Records a payload that was successfully published, so an identical follow-up is
    /// suppressed. Called only after the publish succeeds — a failed publish leaves the
    /// previous entry in place so the value is retried rather than assumed delivered.
    /// </summary>
    public void Record(string topic, string payload, bool retain)
    {
        if (retain)
            _lastPayloads[topic] = payload;
    }

    /// <summary>
    /// Drops all remembered payloads. Called on every (re)connect: a broker that restarted
    /// may have lost its retained set, so the next announce must re-prime it in full.
    /// </summary>
    public void Clear() => _lastPayloads.Clear();
}
