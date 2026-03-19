namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Valid channel count options for relay boards.
/// </summary>
public static class ValidChannelCounts
{
    public static readonly int[] Values = { 1, 2, 4, 8, 16 };

    public static bool IsValid(int count) => Values.Contains(count);

    public static int Clamp(int count)
    {
        if (count <= 1)
            return 1;
        if (count <= 2)
            return 2;
        if (count <= 4)
            return 4;
        if (count <= 8)
            return 8;
        return 16;
    }
}
