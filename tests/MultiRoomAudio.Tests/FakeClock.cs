using Sendspin.SDK.Synchronization;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Deterministic <see cref="IClockSynchronizer"/> test double: a perfectly converged, zero-drift
/// clock whose <c>ServerToClientTime</c> is a fixed linear map. Applies <see cref="StaticDelayMs"/>
/// per the SDK v8.0.0 convention (subtracted from the converted client time). Mirrors the fake
/// clock used by windowsSpin's sync tests.
/// </summary>
internal sealed class FakeClock : IClockSynchronizer
{
    private readonly long _offsetMicroseconds;

    public FakeClock(long offsetMicroseconds = 0) => _offsetMicroseconds = offsetMicroseconds;

    public double StaticDelayMs { get; set; }

    public long ServerToClientTime(long serverTime) =>
        serverTime + _offsetMicroseconds - (long)(StaticDelayMs * 1000);

    public long ClientToServerTime(long clientTime) =>
        clientTime - _offsetMicroseconds + (long)(StaticDelayMs * 1000);

    public bool IsConverged => true;

    public bool HasMinimalSync => true;

    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
    }

    public void Reset()
    {
    }

    public ClockSyncStatus GetStatus() => new() { IsConverged = true, IsDriftReliable = true };
}
