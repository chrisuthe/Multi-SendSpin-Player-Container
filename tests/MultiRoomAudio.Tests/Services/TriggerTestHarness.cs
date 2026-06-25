using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Audio.Mock;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Tests.Services;

/// <summary>
/// Builds a TriggerService wired entirely to mock/in-memory dependencies for unit tests.
///
/// Harness decisions:
/// - CONFIG_PATH env var is set to a per-test temp directory so TriggerService writes
///   triggers.yaml to a throwaway location (not the repo or /app/config).
/// - CustomSinksService is constructed with MockPaModuleRunner and VolumeCommandRunner
///   (both safe to run on Windows with no PulseAudio present).
/// - IServiceProvider is a minimal ServiceCollection with no services registered
///   (CustomSinksService uses it only for optional plugin lookups that don't run in tests).
/// - VIRTUAL: boards connect via the else/OpenBySerial path in ConnectBoard, which the
///   MockRelayBoard accepts unconditionally, so GetBoardStatus reflects relay state.
/// - SetEnabled(true) must be called before AddBoard so ConnectBoard runs during AddBoard.
/// </summary>
internal static class TriggerTestHarness
{
    public static TriggerService CreateMockService()
    {
        // Point config at a temp dir so file I/O doesn't touch real paths or fail on Windows.
        var tempConfig = Path.Combine(Path.GetTempPath(), "trigger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempConfig);
        Environment.SetEnvironmentVariable("CONFIG_PATH", tempConfig);

        var loggerFactory = NullLoggerFactory.Instance;
        var env = new EnvironmentService(NullLogger<EnvironmentService>.Instance);

        var moduleRunner = new MockPaModuleRunner(NullLogger<MockPaModuleRunner>.Instance);
        var volumeRunner = new VolumeCommandRunner(NullLogger<VolumeCommandRunner>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var sinks = new CustomSinksService(
            NullLogger<CustomSinksService>.Instance,
            moduleRunner,
            env,
            volumeRunner,
            services);

        var enumerator = new MockRelayDeviceEnumerator(NullLogger<MockRelayDeviceEnumerator>.Instance);
        var factory = new MockRelayBoardFactory(loggerFactory);

        return new TriggerService(
            NullLogger<TriggerService>.Instance,
            loggerFactory,
            sinks,
            env,
            enumerator,
            factory,
            hubContext: null);
    }
}
