# MQTT → Home Assistant Bridge — Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the 12V amp/relay zones to Home Assistant (status + sticky manual-override switch), add a software `VirtualRelayBoard` whose power state HA can mirror onto a smart outlet, and let HAOS users enable MQTT from the add-on configuration.

**Architecture:** All override/event logic lives in `TriggerService` (above the board layer) so it works for every board type uniformly. A new `TriggersChanged` event drives MQTT amp publishing the same way Phase 1's `PlayersChanged` drives player publishing. The virtual board is a software `IRelayBoard` (like `MockRelayBoard`) with no MQTT dependency — its power state reaches HA through the normal amp-state publish. Pure builders (topics/discovery/state/command) are extended and unit-tested; the `MqttService` glue subscribes to the new event and dispatches the new override command.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, MQTTnet v5, YamlDotNet, xUnit.

**Builds on:** Phase 1 (merged to `dev` via PR #235). This branch `feat/mqtt-ha-bridge-phase2` is based off `dev`.

## Global Constraints

- **Target framework:** .NET 8.0, `Nullable` enabled, `ImplicitUsings` enabled. Microsoft C# conventions. XML doc comments on public APIs.
- **Non-blocking startup (hard rule):** No MQTT/trigger failure may block the UI. The existing `mqtt` `StartupOrchestrator` phase runs after `triggers`; do not add a new phase. The `TriggersChanged` publish handler and all MQTT async handlers must never throw into their callers.
- **Sticky override semantics:** override ON = force relay on + suspend auto-logic for that channel (no auto-off); override OFF (release) = re-evaluate auto — stay on if `ActivePlayerCount > 0`, else start the channel's normal off-delay.
- **Virtual board:** software-only `IRelayBoard`, always `IsConnected`, no MQTT calls in the relay layer. `BoardId` is `VIRTUAL:<id>`, generated once and persisted. Created by BOTH factories.
- **Config precedence:** env var → HAOS option → yaml → default. Docker uses `MQTT_*` env vars (unchanged). HAOS uses snake_case option keys (`mqtt_enabled`, `mqtt_host`, `mqtt_port`, `mqtt_username`, `mqtt_password`, `mqtt_tls`).
- **Dropped from scope:** the per-player `sync_error_ms` sensor.
- **Do not** change the default web port (8096) or enable trimming. Vanilla JS only for UI.
- **Tests:** xUnit, in `tests/MultiRoomAudio.Tests/`. Internals already visible via `InternalsVisibleTo`.
- **Commits:** Conventional Commits. Author as the repo owner — no AI/Claude self-reference, no `Co-Authored-By`.

## File Structure

**Modify:**
- `src/MultiRoomAudio/Models/TriggerModels.cs` — add `RelayBoardType.Virtual`; add `bool IsOverridden` to `TriggerResponse`.
- `src/MultiRoomAudio/Services/TriggerService.cs` — `IsOverridden` channel state, `SetOverride`, auto-logic guards, `TriggersChanged` event, populate `IsOverridden` in `GetBoardStatus`, `VIRTUAL:` type inference + connect branch.
- `src/MultiRoomAudio/Relay/RealRelayBoardFactory.cs`, `MockRelayBoardFactory.cs` — create `VirtualRelayBoard` for `RelayBoardType.Virtual`.
- `src/MultiRoomAudio/Mqtt/MqttTopics.cs` — amp topics.
- `src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs` — `Amp(...)`.
- `src/MultiRoomAudio/Mqtt/HaDiscovery.cs` — `ForAmp(...)`.
- `src/MultiRoomAudio/Mqtt/MqttCommand.cs` — amp override parsing.
- `src/MultiRoomAudio/Services/MqttService.cs` — `TriggerService` dep, `TriggersChanged` subscription, amp publish + command dispatch.
- `src/MultiRoomAudio/Services/MqttConfigService.cs` — snake_case HAOS keys.
- `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs` — override endpoint + virtual board add.
- `src/MultiRoomAudio/wwwroot/js/app.js` (and any trigger UI partial) — virtual board + override controls.
- `multiroom-audio/config.yaml` — MQTT options/schema.

**Create:**
- `src/MultiRoomAudio/Relay/VirtualRelayBoard.cs`
- Tests: `tests/MultiRoomAudio.Tests/Mqtt/AmpTopicsTests.cs`, `AmpStatePayloadsTests.cs`, `AmpDiscoveryTests.cs`, `AmpCommandTests.cs`; `tests/MultiRoomAudio.Tests/Relay/VirtualRelayBoardTests.cs`; `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs`; add HAOS-key case to the existing `MqttConfigServiceTests.cs`.

---

### Task 1: Models — `RelayBoardType.Virtual` + `TriggerResponse.IsOverridden`

**Files:**
- Modify: `src/MultiRoomAudio/Models/TriggerModels.cs`
- Test: `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs` (created here, asserts the record default)

**Interfaces:**
- Produces: `RelayBoardType.Virtual` enum member; `TriggerResponse` gains a trailing `bool IsOverridden = false` parameter.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs`:

```csharp
using MultiRoomAudio.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

public class TriggerModelsTests
{
    [Fact]
    public void TriggerResponse_IsOverridden_DefaultsFalse()
    {
        var r = new TriggerResponse(
            Channel: 1, CustomSinkName: null, CustomSinkDisplayName: null,
            OffDelaySeconds: 60, ZoneName: null, RelayState: RelayState.Off,
            IsActive: false, LastActivated: null, ScheduledOffTime: null);
        Assert.False(r.IsOverridden);
    }

    [Fact]
    public void RelayBoardType_HasVirtual()
        => Assert.True(System.Enum.IsDefined(typeof(RelayBoardType), RelayBoardType.Virtual));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter TriggerModelsTests`
Expected: FAIL — `RelayBoardType.Virtual` doesn't exist and `TriggerResponse` has no `IsOverridden`.

- [ ] **Step 3: Add the enum member**

In `src/MultiRoomAudio/Models/TriggerModels.cs`, in the `RelayBoardType` enum, add after `Lcus`:

```csharp
    /// <summary>Software-only virtual board; power state is published over MQTT for HA to mirror onto a smart outlet.</summary>
    Virtual,
```

(Keep `Mock` last or add `Virtual` before `Mock` — order doesn't matter, but do not renumber existing members by inserting in the middle if any persisted config relies on integer values; YAML persists enum names, so appending is safe.)

- [ ] **Step 4: Add `IsOverridden` to `TriggerResponse`**

In the `TriggerResponse` record, add a trailing parameter (after `ScheduledOffTime`):

```csharp
public record TriggerResponse(
    int Channel,
    string? CustomSinkName,
    string? CustomSinkDisplayName,
    int OffDelaySeconds,
    string? ZoneName,
    RelayState RelayState,
    bool IsActive,
    DateTime? LastActivated,
    DateTime? ScheduledOffTime,
    bool IsOverridden = false
);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter TriggerModelsTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MultiRoomAudio/Models/TriggerModels.cs tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs
git commit -m "feat: add Virtual relay board type and TriggerResponse.IsOverridden"
```

---

### Task 2: TriggerService — sticky override, auto-logic guard, `TriggersChanged` event

**Files:**
- Modify: `src/MultiRoomAudio/Services/TriggerService.cs`
- Test: `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs` (add cases)

**Interfaces:**
- Consumes: `RelayBoardType.Virtual`, `TriggerResponse.IsOverridden` (Task 1).
- Produces:
  - `public event Action? TriggersChanged;`
  - `public bool SetOverride(string boardId, int channel, bool on)`
  - `TriggerChannelState` (private) gains `public bool IsOverridden { get; set; }`
  - `GetBoardStatus` populates `TriggerResponse.IsOverridden` from channel state.

**Context for the implementer:** The auto-logic methods are `ActivateTrigger` (line ~1068), `DeactivateTrigger` (~1104), and `OnOffTimerElapsed` (~1170). `CancelOffTimer` (~1160) and `StartOffTimer` (~1146) manage the off-delay `System.Timers.Timer`. `_channelStates` is a `ConcurrentDictionary<(string BoardId, int Channel), TriggerChannelState>`. `_stateLock` guards state mutations. The channel's configured off-delay is `boardConfig.Triggers.FirstOrDefault(t => t.Channel == channel)?.OffDelaySeconds ?? 60`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs`:

```csharp
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;

public class TriggerOverrideBehaviorTests
{
    // Builds a TriggerService in mock mode with one virtual board + one configured channel.
    private static async Task<(TriggerService svc, string boardId)> SetupAsync()
    {
        var svc = TriggerTestHarness.CreateMockService();
        svc.SetEnabled(true);
        var boardId = "VIRTUAL:test01";
        svc.AddBoard(boardId, "Test Zone", channelCount: 2, boardType: RelayBoardType.Virtual);
        svc.ConfigureTrigger(boardId, channel: 1, customSinkName: "sink1", offDelaySeconds: 30, zoneName: "Zone 1");
        await Task.CompletedTask;
        return (svc, boardId);
    }

    [Fact]
    public async Task Override_On_ForcesRelayOn_AndReportsOverridden()
    {
        var (svc, boardId) = await SetupAsync();
        Assert.True(svc.SetOverride(boardId, 1, true));

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);
        Assert.True(ch.IsOverridden);
    }

    [Fact]
    public async Task Override_Suspends_AutoOff_WhilePlaybackStops()
    {
        var (svc, boardId) = await SetupAsync();
        svc.OnPlayerStarted("p1", "sink1");     // auto-on
        svc.SetOverride(boardId, 1, true);      // grab manual control
        svc.OnPlayerStopped("p1", "sink1");     // would normally schedule off

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);   // still on — auto-off suppressed
        Assert.True(ch.IsOverridden);
        Assert.Null(ch.ScheduledOffTime);             // no off-timer scheduled
    }

    [Fact]
    public async Task Release_WhileIdle_SchedulesOff()
    {
        var (svc, boardId) = await SetupAsync();
        svc.SetOverride(boardId, 1, true);   // on, no players
        svc.SetOverride(boardId, 1, false);  // release while idle

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.False(ch.IsOverridden);
        Assert.NotNull(ch.ScheduledOffTime);  // off-delay started
    }

    [Fact]
    public async Task Release_WhilePlaying_StaysOn_NoOffTimer()
    {
        var (svc, boardId) = await SetupAsync();
        svc.OnPlayerStarted("p1", "sink1");
        svc.SetOverride(boardId, 1, true);
        svc.SetOverride(boardId, 1, false);  // release with a player still active

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);
        Assert.Null(ch.ScheduledOffTime);
    }

    [Fact]
    public async Task TriggersChanged_FiresOnOverride()
    {
        var (svc, boardId) = await SetupAsync();
        int fires = 0;
        svc.TriggersChanged += () => fires++;
        svc.SetOverride(boardId, 1, true);
        Assert.True(fires >= 1);
    }
}
```

- [ ] **Step 2: Add the test harness helper**

Create `tests/MultiRoomAudio.Tests/Services/TriggerTestHarness.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Tests.Services;

/// <summary>Builds a TriggerService wired entirely to mock/in-memory dependencies for unit tests.</summary>
internal static class TriggerTestHarness
{
    public static TriggerService CreateMockService()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var env = new EnvironmentService(NullLogger<EnvironmentService>.Instance);
        var sinks = new CustomSinksService(/* see note */ );
        var enumerator = new MockRelayDeviceEnumerator(loggerFactory.CreateLogger<MockRelayDeviceEnumerator>());
        var factory = new MockRelayBoardFactory(loggerFactory);
        return new TriggerService(
            loggerFactory.CreateLogger<TriggerService>(),
            loggerFactory, sinks, env, enumerator, factory, hubContext: null);
    }
}
```

**Implementer note:** `CustomSinksService`'s constructor may require arguments — read `src/MultiRoomAudio/Services/CustomSinksService.cs` and construct it with the minimal mock/null dependencies it needs (the override tests do not exercise real sinks; `ConfigureTrigger` only stores the sink *name*). If `CustomSinksService` is hard to construct in isolation, instead have the harness use a temporary `EnvironmentService` config dir (env var `CONFIG_PATH` pointed at a temp folder) so `TriggerService` writes its `triggers.yaml` there. Keep the harness minimal and document what you wired.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter TriggerOverrideBehaviorTests`
Expected: FAIL — `SetOverride` and `TriggersChanged` don't exist.

- [ ] **Step 4: Add `IsOverridden` to `TriggerChannelState`**

In `TriggerService.cs`, in the private `TriggerChannelState` class (line ~46), add:

```csharp
        public bool IsOverridden { get; set; }
```

- [ ] **Step 5: Add the `TriggersChanged` event and a raise helper**

Near the other fields/events at the top of `TriggerService`, add:

```csharp
    /// <summary>
    /// Raised whenever a relay/channel or board state changes (auto on/off, off-timer,
    /// manual control, override, connect/disconnect). The MQTT bridge subscribes to
    /// publish amp state without polling.
    /// </summary>
    public event Action? TriggersChanged;

    private void RaiseTriggersChanged() => TriggersChanged?.Invoke();
```

- [ ] **Step 6: Guard the auto-logic against overridden channels, and raise the event**

In `ActivateTrigger`, inside the `lock (_stateLock)`, after `CancelOffTimer(...)` and the count increment, wrap the relay-on so an overridden channel is left to manual control (it's already on). Replace the `if (!state.IsActive) { ... }` block's body so it still sets `IsActive` but skips a redundant `SetRelay` when overridden is harmless — the key guards are in the OFF paths. At the END of the method (after the lock), add `RaiseTriggersChanged();`.

In `DeactivateTrigger`, guard the off path. Change:

```csharp
            if (state.ActivePlayerCount == 0 && state.IsActive)
            {
```
to:
```csharp
            if (state.IsOverridden)
            {
                // Manual override holds the relay on; auto-off is suspended until released.
            }
            else if (state.ActivePlayerCount == 0 && state.IsActive)
            {
```
At the end of the method (after the lock), add `RaiseTriggersChanged();`.

In `OnOffTimerElapsed`, add the override guard at the top of the `lock`:

```csharp
        lock (_stateLock)
        {
            if (state.IsOverridden)
                return;   // override holds the relay on
            if (state.ActivePlayerCount == 0 && state.IsActive)
            { ... existing ... }
```
After the lock, add `RaiseTriggersChanged();`.

- [ ] **Step 7: Add `SetOverride`**

Add this public method (near `ManualControl`):

```csharp
    /// <summary>
    /// Sticky manual override for a channel. ON forces the relay on and suspends the
    /// playback auto-logic for that channel; OFF releases back to auto (stays on if a
    /// player is active, otherwise starts the configured off-delay).
    /// </summary>
    public bool SetOverride(string boardId, int channel, bool on)
    {
        var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
        if (boardConfig == null)
            throw new ArgumentException($"Board '{boardId}' not found", nameof(boardId));
        if (channel < 1 || channel > boardConfig.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {boardConfig.ChannelCount}");

        if (!_channelStates.TryGetValue((boardId, channel), out var state))
            return false;

        var offDelay = boardConfig.Triggers.FirstOrDefault(t => t.Channel == channel)?.OffDelaySeconds ?? 60;

        lock (_stateLock)
        {
            if (on)
            {
                CancelOffTimer(boardId, channel);
                state.IsOverridden = true;
                state.IsActive = true;
                var board = TryGetOrReconnectBoard(boardId);
                board?.SetRelay(channel, true);
                _logger.LogInformation("Override ON: Board '{BoardId}' channel {Channel} (auto-logic suspended)", boardId, channel);
            }
            else
            {
                state.IsOverridden = false;
                if (state.ActivePlayerCount > 0)
                {
                    // A player is still active — keep it on, ensure relay reflects that.
                    state.IsActive = true;
                    TryGetOrReconnectBoard(boardId)?.SetRelay(channel, true);
                    _logger.LogInformation("Override released: Board '{BoardId}' channel {Channel} → stays ON (active playback)", boardId, channel);
                }
                else if (state.IsActive)
                {
                    if (offDelay <= 0)
                    {
                        state.IsActive = false;
                        TryGetOrReconnectBoard(boardId)?.SetRelay(channel, false);
                    }
                    else
                    {
                        StartOffTimer(boardId, channel, offDelay);
                    }
                    _logger.LogInformation("Override released: Board '{BoardId}' channel {Channel} → returning to auto (off in {Delay}s)", boardId, channel, offDelay);
                }
            }
        }

        RaiseTriggersChanged();
        return true;
    }
```

- [ ] **Step 8: Populate `IsOverridden` in `GetBoardStatus`**

In `GetBoardStatus`, in the `new TriggerResponse(...)` construction (line ~231), add the trailing argument:

```csharp
                ScheduledOffTime: channelState.OffDelayTimer?.Enabled == true
                    ? channelState.LastActivated?.AddSeconds(config.OffDelaySeconds)
                    : null,
                IsOverridden: channelState.IsOverridden
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter "TriggerOverrideBehaviorTests|TriggerModelsTests"`
Expected: PASS (all). If the harness needs adjustment for `CustomSinksService`, fix per the Step 2 note until green.

- [ ] **Step 10: Commit**

```bash
git add src/MultiRoomAudio/Services/TriggerService.cs tests/MultiRoomAudio.Tests/Services/
git commit -m "feat: add sticky relay override and TriggersChanged event to TriggerService"
```

---

### Task 3: `VirtualRelayBoard` + factory/connect wiring

**Files:**
- Create: `src/MultiRoomAudio/Relay/VirtualRelayBoard.cs`
- Modify: `src/MultiRoomAudio/Relay/RealRelayBoardFactory.cs`, `src/MultiRoomAudio/Relay/MockRelayBoardFactory.cs`, `src/MultiRoomAudio/Services/TriggerService.cs` (type inference + connect branch)
- Test: `tests/MultiRoomAudio.Tests/Relay/VirtualRelayBoardTests.cs`

**Interfaces:**
- Consumes: `IRelayBoard`, `RelayBoardType.Virtual` (Task 1).
- Produces: `VirtualRelayBoard : IRelayBoard` (always `IsConnected` after `Open()`), created by both factories for `RelayBoardType.Virtual`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Relay/VirtualRelayBoardTests.cs`:

```csharp
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using Xunit;

namespace MultiRoomAudio.Tests.Relay;

public class VirtualRelayBoardTests
{
    [Fact]
    public void SetAndGet_RoundTrips()
    {
        var b = new VirtualRelayBoard(serialNumber: "VIRTUAL:x", channelCount: 4);
        Assert.True(b.Open());
        Assert.True(b.IsConnected);
        Assert.True(b.SetRelay(2, true));
        Assert.Equal(RelayState.On, b.GetRelay(2));
        Assert.Equal(RelayState.Off, b.GetRelay(1));
        Assert.Equal(0b10, b.CurrentState);
    }

    [Fact]
    public void AllOff_ClearsState()
    {
        var b = new VirtualRelayBoard(serialNumber: "VIRTUAL:x", channelCount: 4);
        b.Open();
        b.SetRelay(1, true);
        Assert.True(b.AllOff());
        Assert.Equal(0, b.CurrentState);
    }

    [Fact]
    public void RealFactory_CreatesVirtualBoard()
    {
        var f = new RealRelayBoardFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.True(f.CanCreate("VIRTUAL:x", RelayBoardType.Virtual));
        var board = f.CreateBoard("VIRTUAL:x", RelayBoardType.Virtual);
        Assert.IsType<VirtualRelayBoard>(board);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter VirtualRelayBoardTests`
Expected: FAIL — `VirtualRelayBoard` doesn't exist.

- [ ] **Step 3: Implement `VirtualRelayBoard`**

Create `src/MultiRoomAudio/Relay/VirtualRelayBoard.cs`:

```csharp
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Software-only relay board. Records channel state in memory and is always ready once opened.
/// Its power state reaches Home Assistant through the normal amp-state publish (driven by
/// TriggerService.TriggersChanged) — there is no MQTT dependency in the relay layer itself.
/// </summary>
public sealed class VirtualRelayBoard : IRelayBoard
{
    private readonly ILogger<VirtualRelayBoard>? _logger;
    private readonly string _serialNumber;
    private readonly int _channelCount;
    private readonly bool[] _states = new bool[16];
    private bool _isConnected;
    private bool _disposed;

    public VirtualRelayBoard(ILogger<VirtualRelayBoard>? logger = null, string? serialNumber = null, int channelCount = 8)
    {
        _logger = logger;
        _serialNumber = serialNumber ?? "VIRTUAL";
        _channelCount = Math.Clamp(channelCount, 1, 16);
    }

    public bool IsConnected => _isConnected;
    public string? SerialNumber => _serialNumber;
    public int ChannelCount => _channelCount;

    public int CurrentState
    {
        get
        {
            int s = 0;
            for (int i = 0; i < _channelCount; i++)
                if (_states[i]) s |= (1 << i);
            return s;
        }
    }

    public bool Open()
    {
        if (_disposed) return false;
        _isConnected = true;
        return true;
    }

    public bool OpenBySerial(string serialNumber) => Open();

    public void Close() => _isConnected = false;

    public bool SetRelay(int channel, bool on)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount) return false;
        _states[channel - 1] = on;
        _logger?.LogDebug("Virtual relay '{Serial}' channel {Channel} → {State}", _serialNumber, channel, on ? "ON" : "OFF");
        return true;
    }

    public RelayState GetRelay(int channel)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount) return RelayState.Unknown;
        return _states[channel - 1] ? RelayState.On : RelayState.Off;
    }

    public bool AllOff()
    {
        if (!_isConnected) return false;
        Array.Clear(_states);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isConnected = false;
    }
}
```

- [ ] **Step 4: Wire both factories**

In `RealRelayBoardFactory.CreateBoard`, add a switch arm before the default:

```csharp
            RelayBoardType.Virtual => new VirtualRelayBoard(_loggerFactory.CreateLogger<VirtualRelayBoard>(), boardId),
```
In `RealRelayBoardFactory.CanCreate`, add:
```csharp
            RelayBoardType.Virtual => true, // software board, always available
```

In `MockRelayBoardFactory.CreateBoard`, before the `return new MockRelayBoard(...)`, add:

```csharp
        if (boardType == RelayBoardType.Virtual)
            return new VirtualRelayBoard(_loggerFactory.CreateLogger<VirtualRelayBoard>(), boardId, channelCount);
```
(`CanCreate` already returns true for any type in the mock factory.)

- [ ] **Step 5: Teach `TriggerService` about `VIRTUAL:` IDs**

In `AddBoard` (type inference block ~322) and in the `ConnectBoard` inference block (~742), add a `VIRTUAL:` branch so an unspecified type resolves correctly:

```csharp
            else if (boardId.StartsWith("VIRTUAL:", StringComparison.OrdinalIgnoreCase))
                boardType = RelayBoardType.Virtual;
```
(Place it alongside the existing `HID:`/`MODBUS:` checks, before the `else { boardType = Ftdi; }`.)

In the `ConnectBoard` connect-branch chain (the `if (boardId.StartsWith("HID:"...))` ladder ~769), add a branch:

```csharp
            else if (boardId.StartsWith("VIRTUAL:", StringComparison.OrdinalIgnoreCase))
            {
                connected = board.Open();
            }
```
(Place it before the final `else` FTDI-serial branch.)

- [ ] **Step 6: Run tests + build**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter VirtualRelayBoardTests`
Expected: PASS.
Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/MultiRoomAudio/Relay/ src/MultiRoomAudio/Services/TriggerService.cs tests/MultiRoomAudio.Tests/Relay/
git commit -m "feat: add VirtualRelayBoard and factory/connect wiring"
```

---

### Task 4: Triggers API — override endpoint + virtual board add

**Files:**
- Modify: `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs`

**Interfaces:**
- Consumes: `TriggerService.SetOverride`, `AddBoard` (Task 2/existing).
- Produces: `PUT /api/triggers/boards/{boardId}/{channel}/override` and virtual-board support in the add-board endpoint.

**Context:** Read `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs` first to match its exact minimal-API idiom (route group, parameter binding, response shape, how `{boardId}` is bound/decoded). The existing add-board endpoint maps to `TriggerService.AddBoard(boardId, displayName, channelCount, boardType)`. The existing test relay route `POST .../{channel}/test` calls `ManualControl` — mirror its structure for the override route.

- [ ] **Step 1: Add the override endpoint**

In the trigger route group, add (matching the file's existing style for body binding and responses):

```csharp
        group.MapPut("/boards/{boardId}/{channel:int}/override", (string boardId, int channel, RelayManualControlRequest request, TriggerService triggers) =>
        {
            try
            {
                var ok = triggers.SetOverride(Uri.UnescapeDataString(boardId), channel, request.On);
                return ok
                    ? Results.Ok(new { success = true, message = $"Override {(request.On ? "engaged" : "released")}.", boardId, channel, on = request.On })
                    : Results.BadRequest(new ErrorResponse(false, "Board not connected or channel unavailable."));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(false, ex.Message)); }
        })
        .WithTags("Triggers").WithName("SetTriggerOverride");
```
(Reuse the existing `RelayManualControlRequest` — it already carries a `bool On`. If the file binds `boardId` without URL-decoding elsewhere, match that; the snippet decodes defensively.)

- [ ] **Step 2: Allow virtual boards in the add-board endpoint**

Locate the existing `POST /api/triggers/boards` handler. It binds an `AddBoardRequest` (fields `BoardId`, `DisplayName`, `BoardType`, `ChannelCount`). For a virtual board the client sends `BoardType = Virtual` and may leave `BoardId` empty; generate a stable id server-side when missing/virtual. Add, at the top of the handler body:

```csharp
            var boardId = request.BoardId;
            if (request.BoardType == RelayBoardType.Virtual && string.IsNullOrWhiteSpace(boardId))
                boardId = $"VIRTUAL:{Guid.NewGuid():N}".Substring(0, 16);
```
Then call `AddBoard(boardId, request.DisplayName, request.ChannelCount, request.BoardType)` using this `boardId`. Keep the rest of the handler (validation, response) as-is. If the handler currently rejects empty `BoardId` before this point, move the generation above that check.

- [ ] **Step 3: Build + smoke**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: 0 errors.
Optional runtime smoke (`MOCK_HARDWARE=true dotnet run ...`): `POST /api/triggers/boards` with `{"boardType":"Virtual","displayName":"Test","channelCount":2}` returns success and a `VIRTUAL:` board appears in `GET /api/triggers`; `PUT /api/triggers/boards/{id}/1/override` with `{"on":true}` returns success. Kill the app after. If runtime isn't feasible, verify by build + code inspection and note it.

- [ ] **Step 4: Commit**

```bash
git add src/MultiRoomAudio/Controllers/TriggersEndpoint.cs
git commit -m "feat: add trigger override endpoint and virtual board creation"
```

---

### Task 5: `MqttTopics` — amp topics

**Files:**
- Modify: `src/MultiRoomAudio/Mqtt/MqttTopics.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/AmpTopicsTests.cs`

**Interfaces:**
- Consumes: existing `MqttTopics` (ctor `(baseTopic, discoveryPrefix)`, `Sanitize`).
- Produces: `AmpStateTopic(string boardId, int channel)`, `AmpCommandTopic(string boardId, int channel, string command)`, `string AmpCommandSubscription`. Zone key = `Sanitize(boardId)_<channel>`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/AmpTopicsTests.cs`:

```csharp
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpTopicsTests
{
    private readonly MqttTopics _t = new("multiroom-audio", "homeassistant");

    [Fact]
    public void AmpState_UsesSanitizedBoardAndChannel()
        => Assert.Equal("multiroom-audio/amp/virtual_x_1/state", _t.AmpStateTopic("VIRTUAL:x", 1));

    [Fact]
    public void AmpCommand_AppendsSetSuffix()
        => Assert.Equal("multiroom-audio/amp/virtual_x_1/override/set", _t.AmpCommandTopic("VIRTUAL:x", 1, "override"));

    [Fact]
    public void AmpCommandSubscription_IsWildcard()
        => Assert.Equal("multiroom-audio/amp/+/+/set", _t.AmpCommandSubscription);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpTopicsTests`
Expected: FAIL — methods don't exist.

- [ ] **Step 3: Implement**

In `MqttTopics.cs` add:

```csharp
    private string AmpZone(string boardId, int channel) => $"{Sanitize(boardId)}_{channel}";

    /// <summary>State topic for an amp/zone (one JSON document per zone).</summary>
    public string AmpStateTopic(string boardId, int channel) => $"{_base}/amp/{AmpZone(boardId, channel)}/state";

    /// <summary>Command topic for an amp/zone control (e.g. "override").</summary>
    public string AmpCommandTopic(string boardId, int channel, string command) =>
        $"{_base}/amp/{AmpZone(boardId, channel)}/{command}/set";

    /// <summary>Single wildcard subscription covering all amp command topics.</summary>
    public string AmpCommandSubscription => $"{_base}/amp/+/+/set";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpTopicsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttTopics.cs tests/MultiRoomAudio.Tests/Mqtt/AmpTopicsTests.cs
git commit -m "feat: add MQTT amp topic helpers"
```

---

### Task 6: `MqttStatePayloads.Amp`

**Files:**
- Modify: `src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/AmpStatePayloadsTests.cs`

**Interfaces:**
- Consumes: `TriggerResponse` (with `IsOverridden`, `RelayState`, `ScheduledOffTime`).
- Produces: `static string Amp(TriggerResponse t, bool boardConnected)` → JSON `{ power, scheduled_off, override, board_connected }`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/AmpStatePayloadsTests.cs`:

```csharp
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpStatePayloadsTests
{
    private static TriggerResponse Zone(RelayState s, bool overridden) => new(
        Channel: 1, CustomSinkName: "sink", CustomSinkDisplayName: "Sink",
        OffDelaySeconds: 30, ZoneName: "Living Room", RelayState: s,
        IsActive: s == RelayState.On, LastActivated: null, ScheduledOffTime: null,
        IsOverridden: overridden);

    [Fact]
    public void Amp_SerializesPowerOverrideAndConnectivity()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Amp(Zone(RelayState.On, overridden: true), boardConnected: true));
        var root = doc.RootElement;
        Assert.Equal("ON", root.GetProperty("power").GetString());
        Assert.Equal("ON", root.GetProperty("override").GetString());
        Assert.Equal("ON", root.GetProperty("board_connected").GetString());
    }

    [Fact]
    public void Amp_PowerOff_WhenRelayOff()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Amp(Zone(RelayState.Off, overridden: false), boardConnected: false));
        var root = doc.RootElement;
        Assert.Equal("OFF", root.GetProperty("power").GetString());
        Assert.Equal("OFF", root.GetProperty("override").GetString());
        Assert.Equal("OFF", root.GetProperty("board_connected").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpStatePayloadsTests`
Expected: FAIL — `Amp` doesn't exist.

- [ ] **Step 3: Implement**

In `MqttStatePayloads.cs` add (the `OnOff` helper already exists in the class):

```csharp
    /// <summary>Per-amp/zone state document. RelayState.On → power ON; Unknown → OFF.</summary>
    public static string Amp(MultiRoomAudio.Models.TriggerResponse t, bool boardConnected) => JsonSerializer.Serialize(new
    {
        power = OnOff(t.RelayState == MultiRoomAudio.Models.RelayState.On),
        scheduled_off = t.ScheduledOffTime,
        @override = OnOff(t.IsOverridden),
        board_connected = OnOff(boardConnected),
    });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpStatePayloadsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs tests/MultiRoomAudio.Tests/Mqtt/AmpStatePayloadsTests.cs
git commit -m "feat: add MQTT amp state payload builder"
```

---

### Task 7: `HaDiscovery.ForAmp`

**Files:**
- Modify: `src/MultiRoomAudio/Mqtt/HaDiscovery.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/AmpDiscoveryTests.cs`

**Interfaces:**
- Consumes: `MqttTopics` (amp topics from Task 5), `TriggerResponse`.
- Produces: `IReadOnlyList<DiscoveryMessage> ForAmp(string boardId, string? boardDisplayName, TriggerResponse t)` — four entities (power binary_sensor, scheduled-off sensor, override switch w/ command_topic, board-connected diagnostic binary_sensor) sharing device id `mra_amp_<sanitized boardId>_<channel>`.

**Context:** Mirror the existing `ForPlayer`/`ForContainer` structure (the private `Build(...)` helper and `Device(...)` callback already exist and produce the device block + availability fields). Reuse them.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/AmpDiscoveryTests.cs`:

```csharp
using System.Linq;
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpDiscoveryTests
{
    private readonly HaDiscovery _d = new(new MqttTopics("multiroom-audio", "homeassistant"), "1.2.3");

    private static TriggerResponse Zone() => new(
        Channel: 1, CustomSinkName: "sink", CustomSinkDisplayName: "Sink",
        OffDelaySeconds: 30, ZoneName: "Living Room", RelayState: RelayState.On,
        IsActive: true, LastActivated: null, ScheduledOffTime: null, IsOverridden: false);

    [Fact]
    public void Amp_EmitsOverrideSwitchWithCommandTopic()
    {
        var sw = _d.ForAmp("VIRTUAL:x", "Living Room Amp", Zone()).Single(m => m.Topic.Contains("/switch/"));
        using var doc = JsonDocument.Parse(sw.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/amp/virtual_x_1/override/set", root.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/amp/virtual_x_1/state", root.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void Amp_AllEntitiesShareDeviceIdentifier()
    {
        var ids = _d.ForAmp("VIRTUAL:x", "Living Room Amp", Zone()).Select(m =>
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.GetProperty("device").GetProperty("identifiers")[0].GetString();
        }).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal("mra_amp_virtual_x_1", ids[0]);
    }

    [Fact]
    public void Amp_EmitsPowerBinarySensor()
        => Assert.Contains(_d.ForAmp("VIRTUAL:x", null, Zone()), m => m.Topic.Contains("/binary_sensor/") && m.Payload.Contains("value_json.power"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpDiscoveryTests`
Expected: FAIL — `ForAmp` doesn't exist.

- [ ] **Step 3: Implement**

In `HaDiscovery.cs` add:

```csharp
    public IReadOnlyList<DiscoveryMessage> ForAmp(string boardId, string? boardDisplayName, MultiRoomAudio.Models.TriggerResponse t)
    {
        var zone = $"{MqttTopics.Sanitize(boardId)}_{t.Channel}";
        var stateTopic = _topics.AmpStateTopic(boardId, t.Channel);
        var deviceId = $"mra_amp_{zone}";
        var label = !string.IsNullOrWhiteSpace(t.ZoneName) ? t.ZoneName
                  : !string.IsNullOrWhiteSpace(boardDisplayName) ? $"{boardDisplayName} CH{t.Channel}"
                  : $"{boardId} CH{t.Channel}";
        var device = Device(deviceId, label!, "Amplifier Zone");

        DiscoveryMessage Entity(string component, string key, string name, Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_amp_{zone}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("binary_sensor", "power", $"{label} Power", w =>
            {
                w.WriteString("value_template", "{{ value_json.power }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "power");
            }),
            Entity("sensor", "scheduled_off", $"{label} Scheduled Off", w =>
            {
                w.WriteString("value_template", "{{ value_json.scheduled_off }}");
                w.WriteString("device_class", "timestamp");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("switch", "override", $"{label} Override", w =>
            {
                w.WriteString("command_topic", _topics.AmpCommandTopic(boardId, t.Channel, "override"));
                w.WriteString("value_template", "{{ value_json.override }}");
                w.WriteString("state_on", "ON");
                w.WriteString("state_off", "OFF");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
            }),
            Entity("binary_sensor", "board_connected", $"{label} Board Connected", w =>
            {
                w.WriteString("value_template", "{{ value_json.board_connected }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "connectivity");
                w.WriteString("entity_category", "diagnostic");
            }),
        };
    }
```

**Implementer note:** confirm the existing `Build` and `Device` helper signatures match this usage (they were created in Phase 1 Task 6). If `Build`'s parameter order differs, adapt the calls — do not change `Build`/`Device` themselves.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpDiscoveryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/HaDiscovery.cs tests/MultiRoomAudio.Tests/Mqtt/AmpDiscoveryTests.cs
git commit -m "feat: add HA discovery builders for amp zones"
```

---

### Task 8: `MqttCommand` — amp override parsing

**Files:**
- Modify: `src/MultiRoomAudio/Mqtt/MqttCommand.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/AmpCommandTests.cs`

**Interfaces:**
- Produces: `MqttCommandKind.AmpOverride`; `ParsedCommand` gains amp fields. To avoid breaking Phase 1's `ParsedCommand` shape used by `MqttService`, extend it with optional fields:
  `record ParsedCommand(MqttCommandKind Kind, string PlayerClientId, int? IntValue, string? AmpZone = null, bool? BoolValue = null)`.
  `Parse` now also recognizes `{base}/amp/{zone}/override/set` → `(AmpOverride, AmpZone=zone, BoolValue=payload=="ON")`.

**Context:** `AmpZone` is the sanitized `boardId_channel` string from the topic; `MqttService` (Task 9) maps it back to a real `(boardId, channel)` by matching `Sanitize(boardId)_channel` over the live board list.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/AmpCommandTests.cs`:

```csharp
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpCommandTests
{
    [Fact]
    public void Parse_AmpOverrideOn()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/amp/virtual_x_1/override/set", "ON");
        Assert.Equal(MqttCommandKind.AmpOverride, c.Kind);
        Assert.Equal("virtual_x_1", c.AmpZone);
        Assert.True(c.BoolValue);
    }

    [Fact]
    public void Parse_AmpOverrideOff()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/amp/virtual_x_1/override/set", "OFF");
        Assert.Equal(MqttCommandKind.AmpOverride, c.Kind);
        Assert.False(c.BoolValue);
    }

    [Fact]
    public void Parse_PlayerOffset_StillWorks()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "250");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Equal(250, c.IntValue);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter AmpCommandTests`
Expected: FAIL — `AmpOverride`/`AmpZone`/`BoolValue` don't exist.

- [ ] **Step 3: Implement**

Replace `MqttCommand.cs` contents with:

```csharp
using System.Globalization;

namespace MultiRoomAudio.Mqtt;

public enum MqttCommandKind { Unknown, PlayerOffset, PlayerRestart, AmpOverride }

public record ParsedCommand(
    MqttCommandKind Kind,
    string PlayerClientId,
    int? IntValue,
    string? AmpZone = null,
    bool? BoolValue = null);

/// <summary>
/// Parses inbound MQTT command topics into typed commands. Pure — no dispatch.
/// Player: {base}/player/{sanitizedClientId}/{command}/set
/// Amp:    {base}/amp/{sanitizedBoardId_channel}/{command}/set
/// </summary>
public static class MqttCommand
{
    public static ParsedCommand Parse(string baseTopic, string topic, string payload)
    {
        var root = baseTopic.TrimEnd('/');

        var playerPrefix = root + "/player/";
        if (topic.StartsWith(playerPrefix, StringComparison.Ordinal))
        {
            var parts = topic[playerPrefix.Length..].Split('/');   // {id}/{command}/set
            if (parts.Length != 3 || parts[2] != "set")
                return Unknown();
            var id = parts[0];
            return parts[1] switch
            {
                "offset" => new ParsedCommand(MqttCommandKind.PlayerOffset, id,
                    int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null),
                "restart" => new ParsedCommand(MqttCommandKind.PlayerRestart, id, null),
                _ => new ParsedCommand(MqttCommandKind.Unknown, id, null),
            };
        }

        var ampPrefix = root + "/amp/";
        if (topic.StartsWith(ampPrefix, StringComparison.Ordinal))
        {
            var parts = topic[ampPrefix.Length..].Split('/');      // {zone}/{command}/set
            if (parts.Length != 3 || parts[2] != "set")
                return Unknown();
            var zone = parts[0];
            return parts[1] switch
            {
                "override" => new ParsedCommand(MqttCommandKind.AmpOverride, "", null,
                    AmpZone: zone, BoolValue: payload.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase)),
                _ => Unknown(),
            };
        }

        return Unknown();
    }

    private static ParsedCommand Unknown() => new(MqttCommandKind.Unknown, "", null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter "AmpCommandTests|MqttCommandTests"`
Expected: PASS (the Phase 1 `MqttCommandTests` still pass — the player path is preserved).

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttCommand.cs tests/MultiRoomAudio.Tests/Mqtt/AmpCommandTests.cs
git commit -m "feat: parse amp override commands in MqttCommand"
```

---

### Task 9: `MqttService` — TriggerService dependency, amp publish, override dispatch

**Files:**
- Modify: `src/MultiRoomAudio/Services/MqttService.cs`

**Interfaces:**
- Consumes: `TriggerService` (`GetStatus()`, `SetOverride(boardId, channel, on)`, `TriggersChanged`), amp builders (Tasks 5-8).
- Produces: amp discovery + state published on connect and on `TriggersChanged`; amp override commands dispatched.

**Context:** Read the current `MqttService.cs` first. Phase 1 established: `ConnectAndAnnounceAsync` (subscribes, publishes availability/discovery/state), `PublishDiscoveryAsync`, `PublishAllStateAsync`, `OnPlayersChanged` (fire-and-forget republish), `OnMessageReceivedAsync` (parses + dispatches), `PublishAsync`, the `_topics`/`_discovery` fields, and the constructor. `TriggerFeatureResponse` from `GetStatus()` has `.Boards` (each `TriggerBoardResponse` with `.BoardId`, `.DisplayName`, `.IsConnected`, `.Triggers` — each a `TriggerResponse`).

- [ ] **Step 1: Add the `TriggerService` dependency**

Add a field `private readonly TriggerService _triggers;` and add `TriggerService triggers` to the constructor parameter list, assigning `_triggers = triggers;`. (DI already has `TriggerService` as a singleton — no Program.cs change needed for the dependency, but verify the constructor still resolves.)

- [ ] **Step 2: Publish amp discovery + state on connect**

In `PublishDiscoveryAsync`, after the player + container discovery loops, add:

```csharp
        var trig = _triggers.GetStatus();
        foreach (var board in trig.Boards)
            foreach (var t in board.Triggers)
                foreach (var m in _discovery!.ForAmp(board.BoardId, board.DisplayName, t))
                    await PublishAsync(m.Topic, m.Payload, retain: true, ct);
```

In `PublishAllStateAsync`, after the player + container state, add:

```csharp
        var trigState = _triggers.GetStatus();
        foreach (var board in trigState.Boards)
            foreach (var t in board.Triggers)
                await PublishAsync(_topics!.AmpStateTopic(board.BoardId, t.Channel),
                    MqttStatePayloads.Amp(t, board.IsConnected), retain: true, ct);
```

**Note on the virtual "MQTT not connected" diagnostic:** for amp `board_connected`, pass `board.IsConnected` as above for all boards. (A virtual board reports `IsConnected = true` once opened; surfacing "MQTT not connected" specifically is handled by the bridge availability LWT — when MQTT drops, all entities including the amp go `unavailable` in HA, which is the clearest signal. No extra per-board logic needed here.)

- [ ] **Step 3: Subscribe to the amp command topic**

In `ConnectAndAnnounceAsync`, where it subscribes to `PlayerCommandSubscription`, add a second subscribe:

```csharp
        await _client.SubscribeAsync(_topics!.AmpCommandSubscription, MqttQualityOfServiceLevel.AtLeastOnce, ct);
```

- [ ] **Step 4: Subscribe to `TriggersChanged`**

In `InitializeAsync`, right after `_players.PlayersChanged += OnPlayersChanged;`, add:

```csharp
        _triggers.TriggersChanged += OnTriggersChanged;
```
In `ShutdownAsync`, alongside the `PlayersChanged -= ...` unsubscribe, add:

```csharp
        _triggers.TriggersChanged -= OnTriggersChanged;
```
Add the handler (mirror `OnPlayersChanged` exactly — fire-and-forget, swallow exceptions):

```csharp
    private void OnTriggersChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!IsConnected) return;
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishAllStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish MQTT state on trigger change");
            }
        });
    }
```

- [ ] **Step 5: Dispatch the amp override command**

In `OnMessageReceivedAsync`, after the existing player-command handling, add amp handling. After parsing `var cmd = MqttCommand.Parse(_baseTopic, topic, payload);` and the existing `Unknown` early-return, add an amp branch (before or after the player-player lookup, guarded by kind):

```csharp
            if (cmd.Kind == MqttCommandKind.AmpOverride && cmd.AmpZone is not null)
            {
                foreach (var board in _triggers.GetStatus().Boards)
                {
                    foreach (var t in board.Triggers)
                    {
                        if ($"{MqttTopics.Sanitize(board.BoardId)}_{t.Channel}" == cmd.AmpZone)
                        {
                            _triggers.SetOverride(board.BoardId, t.Channel, cmd.BoolValue ?? false);
                            return;
                        }
                    }
                }
                _logger.LogWarning("MQTT amp override for unknown zone {Zone}", cmd.AmpZone);
                return;
            }
```
Ensure the existing player-dispatch path only runs for player kinds (it already maps by `PlayerClientId`; the amp branch returns before reaching it).

- [ ] **Step 6: Build**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: 0 errors. Run `dotnet test tests/MultiRoomAudio.Tests` → existing suite still green.

- [ ] **Step 7: Commit**

```bash
git add src/MultiRoomAudio/Services/MqttService.cs
git commit -m "feat: publish amp state and dispatch override commands in MqttService"
```

---

### Task 10: `MqttConfigService` — snake_case HAOS keys

**Files:**
- Modify: `src/MultiRoomAudio/Services/MqttConfigService.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs` (add a case)

**Interfaces:**
- Consumes: `EnvironmentService.GetHaosOption<T>` / `IsHaos` (existing).
- Produces: HAOS option lookups keyed by snake_case (`mqtt_enabled`, `mqtt_host`, `mqtt_port`, `mqtt_username`, `mqtt_password`, `mqtt_tls`). Env-var keys (`MQTT_*`) unchanged. `ApplyOverrides` signature unchanged; precedence env → HAOS → yaml → default preserved.

**Context:** In Phase 1, `Reload()` built lambdas like `key => _env.GetHaosOption<bool?>(key)` and `ApplyOverrides` called them with the **env-var key** (`"MQTT_ENABLED"`). Since `ApplyOverrides` takes the env dictionary AND the HAOS accessor funcs, the cleanest fix is: inside `ApplyOverrides`, look up HAOS using the snake_case key while reading env with the `MQTT_*` key. The HAOS funcs are passed the snake_case key.

- [ ] **Step 1: Write the failing test**

Add to `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs`:

```csharp
public class MqttConfigHaosKeyTests
{
    [Fact]
    public void HaosSnakeCaseKeys_Resolve_AndOverrideYaml()
    {
        var result = MultiRoomAudio.Services.MqttConfigService.ApplyOverrides(
            new MqttSettings { Host = "yaml-host", Enabled = false },
            new Dictionary<string, string?>(),                 // no env
            haosBool: key => key == "mqtt_enabled" ? true : (bool?)null,
            haosString: key => key == "mqtt_host" ? "haos-host" : null,
            haosInt: _ => null);

        Assert.True(result.Enabled);              // from mqtt_enabled
        Assert.Equal("haos-host", result.Host);   // from mqtt_host overriding yaml
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttConfigHaosKeyTests`
Expected: FAIL — HAOS lookups currently use `"MQTT_ENABLED"`/`"MQTT_HOST"`, so the snake_case funcs return null and yaml values win.

- [ ] **Step 3: Update `ApplyOverrides` HAOS keys**

In `ApplyOverrides`, change each HAOS accessor call to the snake_case key while keeping `EnvStr(...)` on the `MQTT_*` key:

```csharp
            Enabled = EnvStr("MQTT_ENABLED") is { } en ? IsTruthy(en)
                      : haosBool("mqtt_enabled") ?? fromYaml.Enabled,
            Host = EnvStr("MQTT_HOST") ?? haosString("mqtt_host") ?? fromYaml.Host,
            Port = (EnvStr("MQTT_PORT") is { } p && int.TryParse(p, out var pv)) ? pv
                   : haosInt("mqtt_port") ?? fromYaml.Port,
            Username = EnvStr("MQTT_USERNAME") ?? haosString("mqtt_username") ?? fromYaml.Username,
            Password = EnvStr("MQTT_PASSWORD") ?? haosString("mqtt_password") ?? fromYaml.Password,
            UseTls = EnvStr("MQTT_TLS") is { } tls ? IsTruthy(tls)
                     : haosBool("mqtt_tls") ?? fromYaml.UseTls,
            DiscoveryPrefix = EnvStr("MQTT_DISCOVERY_PREFIX") ?? haosString("mqtt_discovery_prefix") ?? fromYaml.DiscoveryPrefix,
            BaseTopic = EnvStr("MQTT_BASE_TOPIC") ?? haosString("mqtt_base_topic") ?? fromYaml.BaseTopic,
```

Also update `ResolveSource` in `Reload()`/the class: where it checks `_env.GetHaosOption<string?>("MQTT_HOST")`, change to `"mqtt_host"`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter "MqttConfigHaosKeyTests|MqttConfigOverrideTests"`
Expected: PASS (env-precedence tests from Phase 1 still pass — env keys unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Services/MqttConfigService.cs tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs
git commit -m "fix: resolve HAOS MQTT options by snake_case keys"
```

---

### Task 11: HAOS add-on `config.yaml` MQTT options

**Files:**
- Modify: `multiroom-audio/config.yaml`

**Interfaces:** none (declarative add-on schema). Keys must match Task 10's snake_case lookups.

- [ ] **Step 1: Add options + schema**

In `multiroom-audio/config.yaml`, replace the `options:` and `schema:` blocks with:

```yaml
# User-configurable options
options:
  log_level: info
  mqtt_enabled: false

schema:
  log_level: list(debug|info|warning|error)
  mqtt_enabled: bool
  mqtt_host: str?
  mqtt_port: port?
  mqtt_username: str?
  mqtt_password: password?
  mqtt_tls: bool?
```

Do NOT touch the `version:` field (CI auto-updates it).

- [ ] **Step 2: Validate YAML**

Run: `python -c "import yaml,sys; yaml.safe_load(open('multiroom-audio/config.yaml')); print('ok')"` (or any YAML linter available).
Expected: `ok` / no parse error.

- [ ] **Step 3: Commit**

```bash
git add multiroom-audio/config.yaml
git commit -m "feat: expose MQTT options in HAOS add-on config"
```

---

### Task 12: Web UI — virtual board add + override toggle

**Files:**
- Modify: `src/MultiRoomAudio/wwwroot/js/app.js` (and any trigger-related markup/partial it renders into)

**Interfaces:**
- Consumes: `POST /api/triggers/boards` (with `boardType: "Virtual"`), `PUT /api/triggers/boards/{boardId}/{channel}/override` (`{ on: bool }`), `GET /api/triggers` (each trigger now has `isOverridden`).

**Context:** This is integration with existing vanilla JS. Read the current triggers UI in `app.js` first (search for the triggers/relay rendering and the existing "test relay" control, which already calls `POST .../{channel}/test`). Match its DOM-building and fetch patterns (the project uses `textContent`, not `innerHTML`, for user data). Do not introduce frameworks.

- [ ] **Step 1: Read the existing triggers UI**

Run: `grep -n "triggers\|relay\|board" src/MultiRoomAudio/wwwroot/js/app.js | head -40`
Identify: the function that renders boards/channels, the "add board" flow, and the "test relay" handler. These are your templates.

- [ ] **Step 2: Add "Add virtual board" control**

In the add-board UI, add an option/button that POSTs to `/api/triggers/boards` with body `{ boardType: "Virtual", displayName: <user input>, channelCount: <user-selected, default 2> }` (no `boardId` — the server generates it). On success, refresh the triggers view (reuse the existing refresh function). Match the existing add-board fetch/error handling.

- [ ] **Step 3: Add a per-channel override toggle**

For each channel row, add an "Override" toggle reflecting `trigger.isOverridden`. On change, `PUT /api/triggers/boards/{encodeURIComponent(boardId)}/{channel}/override` with `{ on: <checked> }`, then refresh. Place it near the existing per-channel controls; label it clearly (e.g. "Manual override").

- [ ] **Step 4: Show the MQTT-not-connected hint for virtual boards**

When a board's type is `Virtual`, render a small note that it requires MQTT to be enabled/connected to reach Home Assistant (link to the MQTT settings section if one exists). Keep it lightweight; no new dependencies.

- [ ] **Step 5: Manual verification**

Run the app (`MOCK_HARDWARE=true dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj`), open the UI, add a virtual board, toggle a channel override, and confirm the triggers view updates and the API calls succeed (check the network tab / logs). Kill the app afterward. If a browser isn't available in this environment, verify the fetch URLs/bodies by code inspection against the Task 4 endpoints and note that manual UI verification is pending.

- [ ] **Step 6: Commit**

```bash
git add src/MultiRoomAudio/wwwroot/
git commit -m "feat: add virtual board and override controls to triggers UI"
```

---

### Task 13: Full verification pass

**Files:** none (verification only)

- [ ] **Step 1: Run the whole test suite**

Run: `dotnet test tests/MultiRoomAudio.Tests`
Expected: all pass, including the new amp/override/virtual-board tests and the Phase 1 tests.

- [ ] **Step 2: Build the solution**

Run: `dotnet build squeezelite-docker.sln -c Debug`
Expected: 0 errors; no new warnings from new files.

- [ ] **Step 3: End-to-end against a local broker (manual, recommended)**

With a broker (`docker run -it -p 1883:1883 eclipse-mosquitto`), set `MQTT_ENABLED=true MQTT_HOST=localhost`, run with `MOCK_HARDWARE=true`, add a virtual board with a sink-mapped channel, and `mosquitto_sub -t 'homeassistant/#' -v` + `-t 'multiroom-audio/#' -v`. Expected: amp discovery configs appear; toggling the HA override switch publishes to `multiroom-audio/amp/<zone>/override/set` and the amp `power` state flips. Note results.

- [ ] **Step 4: Commit (if fixups were needed)**

```bash
git add -A
git commit -m "test: verify MQTT bridge phase 2 end to end"
```

---

## Self-Review

**1. Spec coverage:**
- Sticky override (ON suspends auto, OFF re-evaluates) → Task 2 (`SetOverride` + guards) + tests.
- `TriggersChanged` event → Task 2; consumed in Task 9.
- `IsOverridden` in status → Tasks 1 + 2 (GetBoardStatus).
- Virtual board (software, both factories, manual add, `VIRTUAL:` id) → Tasks 1, 3, 4.
- Amp entities (power, scheduled-off, override switch, board-connected) → Tasks 6, 7.
- Amp topics + command → Tasks 5, 8.
- MqttService amp publish + override dispatch + TriggersChanged → Task 9.
- HAOS enable: config.yaml options → Task 11; snake_case key alignment → Task 10.
- Docker unchanged → Task 10 keeps `MQTT_*` env keys.
- API (override endpoint, virtual add) → Task 4. UI → Task 12.
- `sync_error_ms` dropped → no task (correct).
- Non-blocking: no new startup phase; `TriggersChanged` handler swallows exceptions → Task 9.

**2. Placeholder scan:** No TBD/TODO. Two tasks (4, 12) instruct reading an existing file to match its idiom before editing — they carry exact API contracts, routes, payloads, and behaviors, not vague directives. The `CustomSinksService` harness construction (Task 2) has an explicit fallback path rather than a placeholder.

**3. Type consistency:** `RelayBoardType.Virtual` and `TriggerResponse.IsOverridden` (Task 1) are used in Tasks 2/3/6/7. `TriggersChanged`/`SetOverride` (Task 2) consumed in Tasks 4/9. `MqttTopics.AmpStateTopic/AmpCommandTopic/AmpCommandSubscription` (Task 5) used in Tasks 7/9. `MqttStatePayloads.Amp(TriggerResponse, bool)` (Task 6) used in Task 9. `HaDiscovery.ForAmp(string, string?, TriggerResponse)` (Task 7) used in Task 9. `ParsedCommand` extended fields `AmpZone`/`BoolValue` (Task 8) used in Task 9. HAOS keys (Task 10) match `config.yaml` (Task 11). Zone key formula `Sanitize(boardId)_channel` is identical in Tasks 5, 7, 8, 9.

## Follow-ups (not in this plan)

- Decouple discovery republish from every state tick (publish discovery only on player/board-set changes) — carried over from Phase 1; both player and amp discovery currently republish on each change (idempotent but chatty).
- HAOS Supervisor `services/mqtt` broker auto-detect (optional convenience on top of the config-options path).
- Onboarding wizard: decide whether the virtual-board/MQTT toggle belongs in `wwwroot/js/wizard.js` (raise at Task 12 time).
