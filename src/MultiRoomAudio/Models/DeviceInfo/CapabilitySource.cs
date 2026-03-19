using System.Text.Json.Serialization;

namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Source of device capability information.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilitySource
{
    /// <summary>Hardware capabilities parsed from ALSA proc filesystem.</summary>
    Alsa,

    /// <summary>Maximum capabilities inferred from PulseAudio sink configuration.</summary>
    PulseAudioMax
}
