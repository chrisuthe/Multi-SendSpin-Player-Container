# Multi-Sink Triggers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a single relay trigger channel reference multiple sinks/zones (issue #250), with full many-to-many sink↔channel mapping, surfaced in the main app and the onboarding wizard via a tag/chip picker.

**Architecture:** Replace the single `CustomSinkName` field on `TriggerConfiguration` with a `List<string> CustomSinkNames`, keeping the old field as a write-only YAML migration shim. The relay engine's existing `ActivePlayerCount` reference counter already keeps a channel ON until its last contributing sink stops; the only engine change is to match **all** channels containing a sink instead of the first. The UI gets a reusable chip-picker.

**Tech Stack:** C# / ASP.NET Core 8.0, YamlDotNet (UnderscoredNamingConvention), xUnit, vanilla JS + Bootstrap 5.

## Global Constraints

- Target framework: **.NET 8.0**, nullable enabled.
- YAML config uses **UnderscoredNamingConvention**; deserializer has `IgnoreUnmatchedProperties()`; serializer has `DefaultValuesHandling.OmitNull | OmitDefaults`.
- JSON API casing is **camelCase** (ASP.NET default) — JS reads `trigger.customSinkNames`, etc.
- Vanilla JS only (no frameworks); use `textContent`/`escapeHtml` for any user-derived strings (XSS).
- Do **not** add unused `using` directives (the repo enforces unused-usings as build errors).
- Build: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj`
- Test: `dotnet test tests/MultiRoomAudio.Tests/MultiRoomAudio.Tests.csproj`
- Two PRs against `dev`: **PR 1** = Tasks 1–2 (closes #250); **PR 2** = Task 3 (wizard step).

---

## File Structure

| File | Responsibility | Change |
|------|----------------|--------|
| `src/MultiRoomAudio/Models/TriggerModels.cs` | Data models for triggers | Modify: `TriggerConfiguration`, `TriggerResponse`, `TriggerConfigureRequest` |
| `src/MultiRoomAudio/Services/TriggerService.cs` | Engine + persistence | Modify: matching, `ConfigureTrigger`, `OnSinkDeleted`, response builder, save filter |
| `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs` | REST endpoints | Modify: 3 `ConfigureTrigger` call sites + 2 unassign sites |
| `tests/MultiRoomAudio.Tests/Services/MultiSinkTriggerTests.cs` | New test coverage | Create |
| `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs` | Existing tests | Modify: update to new signatures |
| `src/MultiRoomAudio/wwwroot/js/app.js` | Main-app trigger panel | Modify: chip picker |
| `src/MultiRoomAudio/wwwroot/js/wizard.js` | Onboarding wizard | Modify: new relay step (PR 2) |

---

## Task 1: Backend — multi-sink data model, engine, and API

This is an atomic compile unit: renaming `CustomSinkName` cascades through the
service, controller, response/request models, and existing tests. All changes
land together so the project compiles and tests pass.

**Files:**
- Modify: `src/MultiRoomAudio/Models/TriggerModels.cs`
- Modify: `src/MultiRoomAudio/Services/TriggerService.cs`
- Modify: `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs`
- Create: `tests/MultiRoomAudio.Tests/Services/MultiSinkTriggerTests.cs`
- Modify: `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs`

**Interfaces:**
- Produces:
  - `TriggerConfiguration.CustomSinkNames` : `List<string>` (default `new()`)
  - `TriggerConfiguration.CustomSinkName` : write-only `string?` shim (getter returns `null`)
  - `TriggerService.ConfigureTrigger(string boardId, int channel, List<string> customSinkNames, int offDelaySeconds, string? zoneName)` : `bool`
  - `TriggerResponse(int Channel, List<string> CustomSinkNames, List<string> CustomSinkDisplayNames, int OffDelaySeconds, string? ZoneName, RelayState RelayState, bool IsActive, DateTime? LastActivated, DateTime? ScheduledOffTime, bool IsOverridden = false)`
  - `TriggerConfigureRequest.CustomSinkNames` : `List<string>`; `TriggerConfigureRequest.ResolveSinkNames()` : `List<string>`
- Consumes (existing, unchanged): `TriggerService.OnPlayerStarted/OnPlayerStopped/GetBoardStatus/AddBoard/SetEnabled`, `TriggerTestHarness.CreateMockService()`.

---

- [ ] **Step 1: Write the failing migration + many-to-many tests**

Create `tests/MultiRoomAudio.Tests/Services/MultiSinkTriggerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Tests.Services;

public class MultiSinkTriggerMigrationTests
{
    private static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static ISerializer Serializer() => new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    [Fact]
    public void LegacySingleSink_MigratesIntoList()
    {
        var yaml = "channel: 1\ncustom_sink_name: sink1\noff_delay_seconds: 30\n";
        var cfg = Deserializer().Deserialize<TriggerConfiguration>(yaml);
        Assert.Equal(new[] { "sink1" }, cfg.CustomSinkNames);
    }

    [Fact]
    public void Serialization_OmitsLegacyKey_EmitsPluralKey()
    {
        var cfg = new TriggerConfiguration { Channel = 1, CustomSinkNames = { "sink1" } };
        var outYaml = Serializer().Serialize(cfg);
        // Note: "custom_sink_name:" (singular + colon) is NOT a substring of "custom_sink_names:".
        Assert.DoesNotContain("custom_sink_name:", outYaml);
        Assert.Contains("custom_sink_names", outYaml);
    }
}

public class MultiSinkTriggerEngineTests
{
    private static (TriggerService svc, string boardId) Setup()
    {
        var svc = TriggerTestHarness.CreateMockService();
        svc.SetEnabled(true);
        var boardId = "VIRTUAL:multi01";
        svc.AddBoard(boardId, "Test", channelCount: 2, boardType: RelayBoardType.Virtual);
        return (svc, boardId);
    }

    private static TriggerResponse Channel(TriggerService svc, string boardId, int channel)
        => svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == channel);

    [Fact]
    public void OneSinkOnTwoChannels_ActivatesBoth()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Zone A");
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 30, "Master");

        svc.OnPlayerStarted("p1", "zoneA");

        Assert.Equal(RelayState.On, Channel(svc, boardId, 1).RelayState);
        Assert.Equal(RelayState.On, Channel(svc, boardId, 2).RelayState);
    }

    [Fact]
    public void ChannelWithTwoSinks_StaysOnUntilLastStops()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 0, "Master");

        svc.OnPlayerStarted("p1", "zoneA");
        svc.OnPlayerStarted("p2", "zoneB");
        svc.OnPlayerStopped("p1", "zoneA");
        Assert.Equal(RelayState.On, Channel(svc, boardId, 2).RelayState);   // zoneB still active

        svc.OnPlayerStopped("p2", "zoneB");
        Assert.Equal(RelayState.Off, Channel(svc, boardId, 2).RelayState);  // last sink stopped, delay 0 ⇒ immediate off
    }

    [Fact]
    public void DeletingSink_RemovesItFromTriggerLists()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 30, "Master");

        svc.OnSinkDeleted("zoneA");

        Assert.Equal(new[] { "zoneB" }, Channel(svc, boardId, 2).CustomSinkNames);
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail to compile**

Run: `dotnet test tests/MultiRoomAudio.Tests/MultiRoomAudio.Tests.csproj`
Expected: BUILD FAILS — `TriggerConfiguration` has no `CustomSinkNames`, `ConfigureTrigger` has no `List<string>` overload, `TriggerResponse` has no `CustomSinkNames`. This confirms the tests exercise the new API.

- [ ] **Step 3: Update the data models** — `src/MultiRoomAudio/Models/TriggerModels.cs`

In `TriggerConfiguration`, replace the `CustomSinkName` property (the `string?` around line 132) with:

```csharp
/// <summary>
/// Names of the custom sinks that trigger this relay.
/// Empty means this trigger is not assigned. The relay turns on when any
/// listed sink starts and off (after the delay) when all have stopped.
/// </summary>
public List<string> CustomSinkNames { get; set; } = new();

/// <summary>
/// Legacy single-sink property. Retained ONLY so old triggers.yaml
/// (custom_sink_name) migrates into <see cref="CustomSinkNames"/> on load.
/// The null getter combined with the serializer's OmitNull handling means it
/// is never written back to disk.
/// </summary>
[Obsolete("Use CustomSinkNames. Retained for config migration.")]
public string? CustomSinkName
{
    get => null;
    set
    {
        if (!string.IsNullOrEmpty(value) && !CustomSinkNames.Contains(value))
            CustomSinkNames.Add(value);
    }
}
```

Replace the `TriggerResponse` record (around line 290) `CustomSinkName` / `CustomSinkDisplayName` members with lists:

```csharp
public record TriggerResponse(
    int Channel,
    List<string> CustomSinkNames,
    List<string> CustomSinkDisplayNames,
    int OffDelaySeconds,
    string? ZoneName,
    RelayState RelayState,
    bool IsActive,
    DateTime? LastActivated,
    DateTime? ScheduledOffTime,
    bool IsOverridden = false
);
```

In `TriggerConfigureRequest` (around line 405), replace the `CustomSinkName` property with the plural list, keep a legacy singular, and add a resolver:

```csharp
/// <summary>
/// Custom sink names to assign to this trigger. Empty list unassigns.
/// </summary>
public List<string> CustomSinkNames { get; set; } = new();

/// <summary>
/// Legacy single-sink field. If <see cref="CustomSinkNames"/> is empty and this
/// is set, it is folded into the list (back-compat for older API callers).
/// </summary>
public string? CustomSinkName { get; set; }

/// <summary>
/// Resolve the effective sink list, folding in the legacy singular field.
/// </summary>
public List<string> ResolveSinkNames()
{
    if (CustomSinkNames.Count == 0 && !string.IsNullOrEmpty(CustomSinkName))
        return new List<string> { CustomSinkName };
    return CustomSinkNames;
}
```

- [ ] **Step 4: Update the response builder** — `src/MultiRoomAudio/Services/TriggerService.cs` (~L231–254)

Replace the single-sink display-name block and the `TriggerResponse` construction with list-based versions:

```csharp
// Resolve display names for each assigned sink.
var sinkNames = config.CustomSinkNames ?? new List<string>();
var sinkDisplayNames = sinkNames
    .Select(n =>
    {
        var sink = _sinksService.GetSink(n);
        return sink?.Description ?? sink?.Name ?? n;
    })
    .ToList();

var relayState = relayBoard?.GetRelay(channel) ?? RelayState.Unknown;

triggers.Add(new TriggerResponse(
    Channel: channel,
    CustomSinkNames: sinkNames,
    CustomSinkDisplayNames: sinkDisplayNames,
    OffDelaySeconds: config.OffDelaySeconds,
    ZoneName: config.ZoneName,
    RelayState: relayState,
    IsActive: channelState.IsActive,
    LastActivated: channelState.LastActivated,
    ScheduledOffTime: channelState.OffDelayTimer?.Enabled == true
        ? channelState.LastActivated?.AddSeconds(config.OffDelaySeconds)
        : null,
    IsOverridden: channelState.IsOverridden
));
```

- [ ] **Step 5: Update the player-event matching to many-to-many** — `src/MultiRoomAudio/Services/TriggerService.cs` (~L701–741)

Replace the bodies of `OnPlayerStarted` and `OnPlayerStopped`:

```csharp
public void OnPlayerStarted(string playerName, string? deviceId)
{
    if (!_config.Enabled || string.IsNullOrEmpty(deviceId))
        return;

    // Activate EVERY channel that lists this sink (full many-to-many).
    foreach (var boardConfig in _config.Boards)
    {
        if (!_boardStates.ContainsKey(boardConfig.BoardId))
            continue;

        foreach (var trigger in boardConfig.Triggers)
        {
            if (trigger.CustomSinkNames.Any(s =>
                    string.Equals(s, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                ActivateTrigger(boardConfig.BoardId, trigger.Channel, playerName);
            }
        }
    }
}

public void OnPlayerStopped(string playerName, string? deviceId)
{
    if (!_config.Enabled || string.IsNullOrEmpty(deviceId))
        return;

    foreach (var boardConfig in _config.Boards)
    {
        if (!_boardStates.ContainsKey(boardConfig.BoardId))
            continue;

        foreach (var trigger in boardConfig.Triggers)
        {
            if (trigger.CustomSinkNames.Any(s =>
                    string.Equals(s, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                DeactivateTrigger(boardConfig.BoardId, trigger.Channel, trigger.OffDelaySeconds, playerName);
            }
        }
    }
}
```

- [ ] **Step 6: Update `ConfigureTrigger`** — `src/MultiRoomAudio/Services/TriggerService.cs` (~L510–564)

Change the signature and body to take a list:

```csharp
public bool ConfigureTrigger(string boardId, int channel, List<string> customSinkNames, int offDelaySeconds, string? zoneName)
{
    var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
    if (boardConfig == null)
        throw new ArgumentException($"Board '{boardId}' not found", nameof(boardId));

    if (channel < 1 || channel > boardConfig.ChannelCount)
        throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {boardConfig.ChannelCount}");

    var sinks = customSinkNames ?? new List<string>();

    lock (_configLock)
    {
        var trigger = boardConfig.Triggers.FirstOrDefault(t => t.Channel == channel);
        if (trigger == null)
        {
            trigger = new TriggerConfiguration { Channel = channel };
            boardConfig.Triggers.Add(trigger);
        }

        // Validate each sink exists (warn but do not fail — a sink may be created later).
        foreach (var sinkName in sinks)
        {
            if (_sinksService.GetSink(sinkName) == null)
            {
                _logger.LogWarning("Custom sink '{SinkName}' not found for trigger {BoardId}/{Channel}",
                    sinkName, boardId, channel);
            }
        }

        trigger.CustomSinkNames = sinks;
        trigger.OffDelaySeconds = offDelaySeconds;
        trigger.ZoneName = zoneName;

        // If unassigning (no sinks), turn off the relay and cancel timer.
        if (sinks.Count == 0)
        {
            CancelOffTimer(boardId, channel);
            if (_relayBoards.TryGetValue(boardId, out var board))
            {
                board.SetRelay(channel, false);
            }
            if (_channelStates.TryGetValue((boardId, channel), out var state))
            {
                state.IsActive = false;
                state.ActivePlayerCount = 0;
            }
        }

        SaveConfiguration();
        _logger.LogInformation("Trigger {BoardId}/{Channel} configured: sinks=[{Sinks}], delay={Delay}s, zone={Zone}",
            boardId, channel, string.Join(", ", sinks), offDelaySeconds, zoneName ?? "(none)");

        return true;
    }
}
```

- [ ] **Step 7: Update `OnSinkDeleted`** — `src/MultiRoomAudio/Services/TriggerService.cs` (~L746–785)

Replace its body so deletion prunes the sink from each list and only clears a channel when its list empties:

```csharp
public void OnSinkDeleted(string sinkName)
{
    lock (_configLock)
    {
        var affected = false;

        foreach (var boardConfig in _config.Boards)
        {
            foreach (var trigger in boardConfig.Triggers)
            {
                var removed = trigger.CustomSinkNames
                    .RemoveAll(s => string.Equals(s, sinkName, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                    continue;

                affected = true;
                _logger.LogInformation("Removed sink '{SinkName}' from trigger {BoardId}/{Channel} (deleted)",
                    sinkName, boardConfig.BoardId, trigger.Channel);

                // Only turn the channel off when no sinks remain on it.
                if (trigger.CustomSinkNames.Count == 0)
                {
                    CancelOffTimer(boardConfig.BoardId, trigger.Channel);
                    if (_relayBoards.TryGetValue(boardConfig.BoardId, out var board))
                    {
                        board.SetRelay(trigger.Channel, false);
                    }
                    if (_channelStates.TryGetValue((boardConfig.BoardId, trigger.Channel), out var state))
                    {
                        state.IsActive = false;
                        state.ActivePlayerCount = 0;
                    }
                }
            }
        }

        if (affected)
        {
            SaveConfiguration();
        }
    }
}
```

- [ ] **Step 8: Update the save-cleanup filter** — `src/MultiRoomAudio/Services/TriggerService.cs` (~L1355–1359)

```csharp
board.Triggers = board.Triggers
    .Where(t => (t.CustomSinkNames?.Count ?? 0) > 0 ||
                !string.IsNullOrEmpty(t.ZoneName) ||
                t.OffDelaySeconds != 60)
    .ToList();
```

- [ ] **Step 9: Update the controller call sites** — `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs`

For the three configure sites (~L404, ~L448, ~L706), replace `request.CustomSinkName` with `request.ResolveSinkNames()`:

```csharp
var success = service.ConfigureTrigger(
    boardId,
    channel,
    request.ResolveSinkNames(),
    request.OffDelaySeconds,
    request.ZoneName);
```

(For the legacy first-board site at ~L706 the first argument is `firstBoard.BoardId`.)

For the two unassign sites (~L481, ~L744), replace the `null` sink argument with an empty list:

```csharp
service.ConfigureTrigger(boardId, channel, new List<string>(), 60, null);
```

(At ~L744 the first argument is `firstBoard.BoardId`.) Ensure `using System.Collections.Generic;` is present at the top of the file (add it only if missing — do not duplicate).

- [ ] **Step 10: Update existing trigger tests to the new signatures** — `tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs`

At the `SetupAsync` helper (line ~19), change:

```csharp
svc.ConfigureTrigger(boardId, channel: 1, customSinkNames: new List<string> { "sink1" }, offDelaySeconds: 30, zoneName: "Zone 1");
```

Add `using System.Collections.Generic;` to the file's usings if not already present.

In `TriggerModelsTests.TriggerResponse_IsOverridden_DefaultsFalse` (line ~90), update the constructor call to the new record shape:

```csharp
var r = new TriggerResponse(
    Channel: 1,
    CustomSinkNames: new List<string>(),
    CustomSinkDisplayNames: new List<string>(),
    OffDelaySeconds: 60, ZoneName: null, RelayState: RelayState.Off,
    IsActive: false, LastActivated: null, ScheduledOffTime: null);
Assert.False(r.IsOverridden);
```

- [ ] **Step 11: Build and run all tests**

Run: `dotnet test tests/MultiRoomAudio.Tests/MultiRoomAudio.Tests.csproj`
Expected: PASS — all new `MultiSinkTriggerTests`, the updated `TriggerOverrideTests`, and the rest of the suite are green.

- [ ] **Step 12: Commit**

```bash
git add src/MultiRoomAudio/Models/TriggerModels.cs \
        src/MultiRoomAudio/Services/TriggerService.cs \
        src/MultiRoomAudio/Controllers/TriggersEndpoint.cs \
        tests/MultiRoomAudio.Tests/Services/MultiSinkTriggerTests.cs \
        tests/MultiRoomAudio.Tests/Services/TriggerOverrideTests.cs
git commit -m "feat: support multiple sinks per relay trigger (#250)

Replace TriggerConfiguration.CustomSinkName with a CustomSinkNames list and
match all channels containing a sink (full many-to-many). Legacy single-sink
config migrates via a write-only shim. Closes the backend half of #250."
```

---

## Task 2: Main-app chip-picker UI

**Files:**
- Modify: `src/MultiRoomAudio/wwwroot/js/app.js`

**Interfaces:**
- Consumes: `triggersData.boards[].triggers[].customSinkNames` (array, from Task 1 API), `escapeHtml`, `showAlert`.
- Produces (globals reused by Task 3): `renderSinkChips(boardId, channel, names, allSinks, disabled)`, `addTriggerSink`, `removeTriggerSink`.

> No JS unit harness exists in this repo; this task is verified by building and
> exercising the UI manually.

- [ ] **Step 1: Stash the sink list for handler reuse** — in `renderTriggers`, right after `sinkOptions` is built (~L4869), add:

```javascript
// Expose the current sink list to chip-picker handlers (and the wizard).
triggerSinksList = customSinksList;
```

At the top of `app.js` near other module-level state, add:

```javascript
let triggerSinksList = [];
```

- [ ] **Step 2: Add the chip-picker render + helpers** (place near the other trigger functions, e.g. just above `updateTriggerSink`)

```javascript
// Look up the in-memory trigger object for a board/channel.
function getTrigger(boardId, channel) {
    const board = triggersData?.boards?.find(b => b.boardId === boardId);
    return board?.triggers?.find(t => t.channel === channel);
}

// Render the selected-sink chips plus an "add" dropdown for a channel.
function renderSinkChips(boardId, channel, names, allSinks, disabled) {
    const safeNames = names || [];
    const chips = safeNames.map(name => {
        const sink = (allSinks || []).find(s => s.name === name);
        const label = sink ? (sink.description || sink.name) : name;
        const remove = disabled ? '' :
            `<button type="button" class="btn-close btn-close-white ms-1" style="font-size:.5rem"
                     aria-label="Remove" title="Remove zone"
                     onclick="removeTriggerSink('${boardId}', ${channel}, '${escapeHtml(name)}')"></button>`;
        return `<span class="badge bg-primary d-inline-flex align-items-center me-1 mb-1">${escapeHtml(label)}${remove}</span>`;
    }).join('');

    const remaining = (allSinks || []).filter(s => !safeNames.includes(s.name));
    const addControl = (disabled || remaining.length === 0) ? '' : `
        <select class="form-select form-select-sm mt-1"
                onchange="addTriggerSink('${boardId}', ${channel}, this.value); this.value='';">
            <option value="">+ Add zone…</option>
            ${remaining.map(s => `<option value="${escapeHtml(s.name)}">${escapeHtml(s.description || s.name)}</option>`).join('')}
        </select>`;

    const empty = safeNames.length === 0 ? '<span class="text-muted small">Not assigned</span>' : '';
    return `<div class="d-flex flex-wrap align-items-center">${chips}${empty}</div>${addControl}`;
}

async function addTriggerSink(boardId, channel, sinkName) {
    if (!sinkName) return;
    const t = getTrigger(boardId, channel);
    if (!t) return;
    t.customSinkNames = t.customSinkNames || [];
    if (!t.customSinkNames.includes(sinkName)) t.customSinkNames.push(sinkName);
    await saveTriggerSinks(boardId, channel);
}

async function removeTriggerSink(boardId, channel, sinkName) {
    const t = getTrigger(boardId, channel);
    if (!t) return;
    t.customSinkNames = (t.customSinkNames || []).filter(n => n !== sinkName);
    await saveTriggerSinks(boardId, channel);
}

// Persist the channel's full sink list (+ current delay) and re-render its chips.
async function saveTriggerSinks(boardId, channel) {
    const boardIdSafe = boardId.replace(/[^a-zA-Z0-9]/g, '_');
    const t = getTrigger(boardId, channel);
    const names = t ? (t.customSinkNames || []) : [];
    const delayInput = document.getElementById(`trigger-delay-${boardIdSafe}-${channel}`);
    const delay = delayInput ? parseInt(delayInput.value, 10) : 60;

    try {
        const url = boardId.includes('/')
            ? `./api/triggers/boards/channel?boardId=${encodeURIComponent(boardId)}&channel=${channel}`
            : `./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}`;
        const response = await fetch(url, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ channel, customSinkNames: names, offDelaySeconds: delay })
        });
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to update trigger');
        }
        const container = document.getElementById(`trigger-sink-${boardIdSafe}-${channel}`);
        if (container) {
            container.innerHTML = renderSinkChips(boardId, channel, names, triggerSinksList, false);
        }
        showAlert('Trigger updated', 'success', 2000);
    } catch (error) {
        console.error('Error updating trigger:', error);
        showAlert(`Failed to update trigger: ${error.message}`, 'danger');
    }
}
```

- [ ] **Step 3: Replace the `<select>` sink cell with the chip container** — in the `channelsHtml` map (~L4918–4926), replace the `<td>` containing the sink `<select>` with:

```javascript
                    <td>
                        <div id="trigger-sink-${boardIdSafe}-${trigger.channel}"
                             class="trigger-sink-picker">
                            ${renderSinkChips(boardId, trigger.channel, trigger.customSinkNames, customSinksList, controlsDisabled)}
                        </div>
                    </td>
```

The container keeps the `trigger-sink-${boardIdSafe}-${channel}` id so existing
`updateChannelState` row-lookup logic still finds the row via `closest('tr')`.

- [ ] **Step 4: Remove the now-dead single-select code**

- Delete the old `updateTriggerSink` function (~L5552–5583) — nothing references it after Step 3.
- Delete the `restoreTriggerSelections`/select-value restore block (~L5064–5071) and its call site (search for where it is invoked after `renderTriggers`). The chips render from data directly, so no post-render restore is needed.
- In `updateTriggerDelay` (~L5586–5616), replace the `<select>` read with the in-memory list:

```javascript
async function updateTriggerDelay(boardId, channel, delay) {
    const t = getTrigger(boardId, channel);
    const names = t ? (t.customSinkNames || []) : [];

    try {
        const url = boardId.includes('/')
            ? `./api/triggers/boards/channel?boardId=${encodeURIComponent(boardId)}&channel=${channel}`
            : `./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}`;
        const response = await fetch(url, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ channel, customSinkNames: names, offDelaySeconds: parseInt(delay, 10) })
        });
        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to update trigger');
        }
        showAlert('Delay updated', 'success', 2000);
    } catch (error) {
        console.error('Error updating trigger delay:', error);
        showAlert(`Failed to update trigger: ${error.message}`, 'danger');
    }
}
```

- In `updateChannelState` (~L5632), the variable named `sinkSelect` now resolves
  to the container div — rename it to `sinkCell` for clarity and keep the
  `closest('tr')` logic unchanged:

```javascript
    const sinkCell = document.getElementById(`trigger-sink-${boardIdSafe}-${channel}`);
    if (!sinkCell) return;
    const row = sinkCell.closest('tr');
    if (!row) return;
```

- [ ] **Step 5: Build the app and verify manually**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj`
Expected: build succeeds.

Then run with mock hardware and verify in a browser:

Run: `MOCK_HARDWARE=true dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj`
Verify on the Triggers panel:
1. A channel with no sinks shows "Not assigned" and a "+ Add zone…" dropdown.
2. Selecting a zone adds a chip and persists (refresh the page — chip remains).
3. Adding a second zone shows two chips; the dropdown no longer lists chosen zones.
4. The `×` on a chip removes it and persists.
5. Assigning the **same** zone to two channels is allowed (dropdown still offers it on the second channel).

- [ ] **Step 6: Commit**

```bash
git add src/MultiRoomAudio/wwwroot/js/app.js
git commit -m "feat: chip picker for multi-sink trigger assignment (#250)"
```

> **End of PR 1.** Push branch and open PR against `dev` covering Tasks 1–2.
> PR body: closes #250 (backend + main-app multi-sink support).

---

## Task 3: Wizard relay-assignment step (PR 2)

**Files:**
- Modify: `src/MultiRoomAudio/wwwroot/js/wizard.js`

**Interfaces:**
- Consumes: global `renderSinkChips` (Task 2), existing wizard helpers
  (`this.customSinks`, `renderProgress`, `renderStep`), and trigger REST APIs:
  - `GET /api/triggers` → `{ enabled, boards: [{ boardId, displayName, channelCount, triggers: [{channel, customSinkNames}] }] }`
  - `PUT /api/triggers/enabled` → `{ enabled: true }`
  - `PUT /api/triggers/boards/{boardId}/{channel}` → `{ channel, customSinkNames, offDelaySeconds }`
- Produces: a new onboarding step shown only when relay boards already exist.

> Verified manually (no JS test harness). This step is **read-mostly**: it
> assigns sinks to channels on boards the user already added in the main app /
> hardware detection. Board *creation* remains in the main app to keep onboarding
> scope bounded.

- [ ] **Step 1: Add the step to `STEPS`** — `src/MultiRoomAudio/wwwroot/js/wizard.js` (~L81)

Insert before the `complete` step:

```javascript
        { id: 'triggers', title: 'Amp Triggers' },
```

So the array becomes: welcome, cards, identify, sinks, players, **triggers**, complete.

- [ ] **Step 2: Add wizard state for trigger boards** — in the wizard state block (near `customSinks: []`, ~L75):

```javascript
    triggerBoards: [],     // boards fetched from /api/triggers
    triggersEnabled: false,
```

- [ ] **Step 3: Handle the step in `renderStep`** — in the `switch (step.id)` (~L284), add a case that self-skips when there is no relay hardware:

```javascript
            case 'triggers':
                await this.loadTriggerBoards();
                if (this.triggerBoards.length === 0) {
                    // No relay boards configured — skip this step entirely.
                    this.currentStep++;
                    this.renderProgress();
                    await this.renderStep();
                    return;
                }
                content.innerHTML = this.renderTriggers();
                break;
```

- [ ] **Step 4: Add the loader and renderer + assignment handlers** — add these methods to the `Wizard` object (near the other `render*` methods):

```javascript
    // Load configured relay boards for the triggers step.
    async loadTriggerBoards() {
        try {
            const response = await fetch('./api/triggers');
            if (!response.ok) { this.triggerBoards = []; return; }
            const data = await response.json();
            this.triggerBoards = data.boards || [];
            this.triggersEnabled = !!data.enabled;
        } catch (error) {
            console.error('Failed to load trigger boards:', error);
            this.triggerBoards = [];
        }
    },

    // Step: assign zones/sinks to relay channels using the shared chip picker.
    renderTriggers() {
        const sinkList = this.customSinks.map(s => ({ name: s.name, description: s.label || s.name }));
        const boardsHtml = this.triggerBoards.map(board => {
            const rows = (board.triggers || []).map(t => `
                <tr>
                    <td><span class="badge bg-primary">CH ${t.channel}</span></td>
                    <td>
                        <div id="wizard-trigger-sink-${board.boardId.replace(/[^a-zA-Z0-9]/g, '_')}-${t.channel}">
                            ${renderSinkChips(board.boardId, t.channel, t.customSinkNames, sinkList, false)}
                        </div>
                    </td>
                </tr>`).join('');
            return `
                <h6 class="mt-3">${escapeHtml(board.displayName || board.boardId)}</h6>
                <table class="table table-sm align-middle">
                    <thead><tr><th style="width:6rem">Channel</th><th>Zones</th></tr></thead>
                    <tbody>${rows}</tbody>
                </table>`;
        }).join('');

        return `
            <div class="py-2">
                <h4>Amplifier Triggers</h4>
                <p class="text-muted">Assign one or more zones to each relay channel. The relay
                   switches on when any assigned zone plays and off when all of them stop —
                   ideal for powering a multi-zone amplifier from a single trigger.</p>
                ${boardsHtml}
            </div>`;
    },
```

> The chip picker's `addTriggerSink`/`removeTriggerSink`/`saveTriggerSinks`
> (from app.js) operate on `triggersData`, which is the **main app's** state and
> is not populated inside the wizard. The wizard therefore defines its own
> persistence in the next step and overrides the container's handlers by passing
> wizard-scoped callbacks. To keep it simple and self-contained, the wizard uses
> a thin local save that calls the same API.

- [ ] **Step 5: Add wizard-local chip handlers** — because the wizard does not use the main app's `triggersData`, add wizard-scoped add/remove that update `this.triggerBoards` and persist immediately:

```javascript
    wizardGetTrigger(boardId, channel) {
        const board = this.triggerBoards.find(b => b.boardId === boardId);
        return board?.triggers?.find(t => t.channel === channel);
    },

    async wizardAddTriggerSink(boardId, channel, sinkName) {
        if (!sinkName) return;
        const t = this.wizardGetTrigger(boardId, channel);
        if (!t) return;
        t.customSinkNames = t.customSinkNames || [];
        if (!t.customSinkNames.includes(sinkName)) t.customSinkNames.push(sinkName);
        await this.wizardSaveTriggerSinks(boardId, channel);
    },

    async wizardRemoveTriggerSink(boardId, channel, sinkName) {
        const t = this.wizardGetTrigger(boardId, channel);
        if (!t) return;
        t.customSinkNames = (t.customSinkNames || []).filter(n => n !== sinkName);
        await this.wizardSaveTriggerSinks(boardId, channel);
    },

    async wizardSaveTriggerSinks(boardId, channel) {
        const t = this.wizardGetTrigger(boardId, channel);
        const names = t ? (t.customSinkNames || []) : [];
        // Ensure the feature is enabled before the first assignment.
        if (!this.triggersEnabled) {
            await fetch('./api/triggers/enabled', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: true })
            });
            this.triggersEnabled = true;
        }
        const url = boardId.includes('/')
            ? `./api/triggers/boards/channel?boardId=${encodeURIComponent(boardId)}&channel=${channel}`
            : `./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}`;
        await fetch(url, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ channel, customSinkNames: names, offDelaySeconds: 60 })
        });
        const safe = boardId.replace(/[^a-zA-Z0-9]/g, '_');
        const container = document.getElementById(`wizard-trigger-sink-${safe}-${channel}`);
        const sinkList = this.customSinks.map(s => ({ name: s.name, description: s.label || s.name }));
        if (container) container.innerHTML = renderSinkChips(boardId, channel, names, sinkList, false);
    },
```

- [ ] **Step 6: Point the chip picker at the wizard handlers in the triggers step**

`renderSinkChips` emits `onclick="removeTriggerSink(...)"` / `onchange="addTriggerSink(...)"` referencing the app.js globals. Inside the wizard, alias those globals to the wizard methods while the triggers step is shown, by adding this at the start of `renderTriggers()`:

```javascript
        // Route chip-picker callbacks to wizard-scoped handlers for this step.
        window.addTriggerSink = (b, c, s) => this.wizardAddTriggerSink(b, c, s);
        window.removeTriggerSink = (b, c, s) => this.wizardRemoveTriggerSink(b, c, s);
```

And restore the app.js handlers when the wizard closes — in `hide()` (or `complete()`), reset them so the main app's panel keeps working:

```javascript
        // Restore main-app chip handlers (defined in app.js).
        if (typeof appAddTriggerSink === 'function') window.addTriggerSink = appAddTriggerSink;
        if (typeof appRemoveTriggerSink === 'function') window.removeTriggerSink = appRemoveTriggerSink;
```

In `app.js`, after defining `addTriggerSink`/`removeTriggerSink`, capture the originals once so the wizard can restore them:

```javascript
// Preserve references so the onboarding wizard can restore these after overriding.
const appAddTriggerSink = addTriggerSink;
const appRemoveTriggerSink = removeTriggerSink;
```

> Design note: this global-aliasing is a small wart caused by the chip markup
> using inline `onclick`. It is acceptable here because both scripts share one
> page and the alias is scoped to the wizard's lifetime. If a cleaner approach is
> wanted later, `renderSinkChips` could take handler-name parameters — out of
> scope for this PR.

- [ ] **Step 7: Build and verify manually**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj`
Expected: build succeeds.

Run with mock hardware and a configured board, then trigger onboarding
(`POST /api/onboarding/reset`, reload):
1. With **no** relay boards: the wizard skips straight from Players to Done (no triggers step).
2. With a relay board configured: the triggers step appears, lists the board's channels, and lets you add/remove zones via chips.
3. Assignments persist (check the main-app Triggers panel after finishing).
4. After finishing onboarding, the main-app chip picker still adds/removes correctly (handlers restored).

- [ ] **Step 8: Commit**

```bash
git add src/MultiRoomAudio/wwwroot/js/wizard.js src/MultiRoomAudio/wwwroot/js/app.js
git commit -m "feat: add relay-trigger assignment step to onboarding wizard (#250)"
```

> **End of PR 2.** Push branch and open PR against `dev` for the wizard step.

---

## Self-Review Notes

- **Spec coverage:** data model + migration (Task 1 Steps 3, 1–2), many-to-many engine (Step 5), `ConfigureTrigger`/`OnSinkDeleted`/response/save-filter (Steps 4,6,7,8), API request/response + controller (Steps 3,9), main-app chip picker (Task 2), wizard step (Task 3), testing (Task 1 Steps 1,11). All spec sections map to a task.
- **Migration safety:** the write-only `CustomSinkName` shim + `IgnoreUnmatchedProperties()` + `OmitNull` is covered by the two migration tests in Task 1 Step 1.
- **Type consistency:** `CustomSinkNames` (list) and `ConfigureTrigger(..., List<string>, ...)` are used identically across service, controller, request resolver, and tests. `TriggerResponse` positional order is fixed in Step 3 and matched in the updated test (Step 10) and response builder (Step 4).
