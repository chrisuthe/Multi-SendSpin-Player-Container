namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Denkovi FTDI relay board models.
/// Only these specific Denkovi models are supported - generic FTDI boards are not supported.
/// All models use FT245RL chip with synchronous bitbang mode (0x04).
/// </summary>
public enum DenkoviBoardModel
{
    /// <summary>
    /// DAE-CB/Ro8-USB - 8 channel relay board.
    /// Uses sequential pin mapping: Relay 1-8 → Bits 0-7.
    /// </summary>
    Ro8,

    /// <summary>
    /// DAE-CB/Ro4-USB - 4 channel relay board.
    /// Uses odd pin mapping: Relay 1-4 → Bits 1,3,5,7 (pins D1,D3,D5,D7).
    /// </summary>
    Ro4
}
