# Multi-Sink Triggers — Design

**Issue:** #250 — _"allow trigger to reference more than one sink"_
**Date:** 2026-06-26
**Branch:** `feat/multi-sink-triggers` (off `dev`)

## Problem

Each relay trigger channel currently references exactly one sink/zone via
`TriggerConfiguration.CustomSinkName` (a single `string?`). For whole-amp power
control (e.g. one relay powering a 6-zone amplifier), a user wants a single
channel to switch ON when **any** of several zones starts and OFF only when
**all** of them have stopped.

The runtime engine already supports the OFF-when-last-stops behavior via the
`TriggerChannelState.ActivePlayerCount` reference counter — it is simply not
reachable today because the config models a 1:1 sink→channel mapping. This is a
**data-model change, not an engine rewrite.**

## Scope

Two parts, shipped as two PRs against `dev`:

- **Part 1 — Core multi-sink** (backend + main-app UI). Fully closes #250.
- **Part 2 — Wizard relay step** (new onboarding step). Builds on Part 1.

### Mapping model: full many-to-many

A channel may list many sinks **and** a sink may appear on many channels.
Example:

```
CH7 (master amp): [zoneA, zoneB, zoneC, zoneD, zoneE, zoneF]
CH1 (zone A amp): [zoneA]

zoneA plays  → CH1 ON + CH7 ON
zoneA stops  → CH1 OFF; CH7 stays ON until B–F stop
```

This requires removing the early `return` in the player-event handlers so that
**all** matching channels activate, not just the first.

---

## Part 1 — Core multi-sink

### 1. Data model — `Models/TriggerModels.cs`

Replace the single field with a list, plus a write-only migration shim for the
legacy property:

```csharp
public class TriggerConfiguration
{
    public int Channel { get; set; }

    /// <summary>Sinks whose playback activates this relay. Empty = unassigned.</summary>
    public List<string> CustomSinkNames { get; set; } = new();

    // Legacy single-sink property — retained ONLY so old YAML migrates cleanly.
    [Obsolete("Use CustomSinkNames. Retained for config migration.")]
    public string? CustomSinkName
    {
        get => null;  // null + serializer OmitNull ⇒ never written back to disk
        set
        {
            if (!string.IsNullOrEmpty(value) && !CustomSinkNames.Contains(value))
                CustomSinkNames.Add(value);
        }
    }

    public int OffDelaySeconds { get; set; } = 60;
    public string? ZoneName { get; set; }
}
```

**Why the shim is mandatory:** the deserializer is configured with
`UnderscoredNamingConvention` + `IgnoreUnmatchedProperties()`
(`TriggerService` ctor). Without a settable `CustomSinkName`, an old
`custom_sink_name:` line would be silently ignored and users would lose their
trigger assignments on upgrade. The null getter combined with the serializer's
existing `DefaultValuesHandling.OmitNull` means the legacy key vanishes from
disk on the next save — a clean one-way migration with no version flag.

### 2. Engine — many-to-many matching — `Services/TriggerService.cs`

`OnPlayerStarted` / `OnPlayerStopped` (~L701–741): remove the
`FirstOrDefault(...) + return` and instead iterate **all** boards and channels,
activating/deactivating **every** channel whose `CustomSinkNames` contains the
device id:

```csharp
foreach (var boardConfig in _config.Boards)
{
    if (!_boardStates.ContainsKey(boardConfig.BoardId)) continue;
    foreach (var trigger in boardConfig.Triggers)
    {
        if (trigger.CustomSinkNames.Any(s =>
                string.Equals(s, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            ActivateTrigger(boardConfig.BoardId, trigger.Channel, playerName);
        }
    }
}
```

`ActivateTrigger` / `DeactivateTrigger` and the `ActivePlayerCount`
reference-counting logic are **unchanged** — they already keep a channel ON
until its last contributing sink stops.

### 3. Supporting backend changes — `Services/TriggerService.cs`

- **`ConfigureTrigger`** (~L510): signature takes `List<string> customSinkNames`;
  validate each sink (warn if missing, non-fatal); empty list = unassign
  (turn relay off, cancel timer, reset channel state).
- **`OnSinkDeleted`** (~L746): remove the deleted sink from each trigger's list;
  only clear the channel (relay off, cancel timer, reset state) when a list
  becomes empty.
- **Response builder** (~L223): build `CustomSinkNames` plus a parallel
  `CustomSinkDisplayNames` list (resolve each via `_sinksService.GetSink`).
- **`SaveConfigurationInternal` cleanup filter** (~L1356): keep a trigger when
  `CustomSinkNames.Count > 0 || !IsNullOrEmpty(ZoneName) || OffDelaySeconds != 60`.

### 4. API — `Models/TriggerModels.cs` + `Controllers/TriggersEndpoint.cs`

- **`TriggerResponse`**: replace `CustomSinkName` / `CustomSinkDisplayName` with
  `CustomSinkNames` / `CustomSinkDisplayNames` (lists).
- **`TriggerConfigureRequest`**: add `List<string> CustomSinkNames`. Keep the
  singular `CustomSinkName` accepted for back-compat — if the list is empty and
  the singular is present, fold it in. (The web UI is the only known consumer,
  but this keeps any external automation working.)
- **Controller**: the three `ConfigureTrigger` call sites (main + 2 legacy)
  pass the list through.

### 5. Main-app UI — `wwwroot/js/app.js`

Replace the single `<select>` sink cell (~L4919) with a **reusable chip
picker**:

- Selected sinks render as removable pills (chip text = display name, `×` removes).
- A dropdown lists the remaining (not-yet-selected) sinks; choosing one adds a chip.
- Any add/remove issues a `PUT /api/triggers/boards/{boardId}/{channel}` with the
  full `customSinkNames` array (plus existing `offDelaySeconds` / `zoneName`).
- Selection-restore logic (~L5064) and the delay/override handlers that currently
  read the `<select>` value (~L5588, ~L5632) updated to read the chip state.

Implemented as a single function (e.g. `renderSinkChipPicker(containerId, selected, available, onChange)`)
so Part 2 reuses it verbatim.

---

## Part 2 — Wizard relay step (new onboarding step)

### Overview

The onboarding wizard (`wwwroot/js/wizard.js`) currently has no relay/trigger
step (`STEPS`: Welcome → Devices → Identify → Sinks → Players → Done). Add a new
step that lets users assign zones/sinks to relay channels during setup, using
the same chip picker.

### Changes

- **New step** `{ id: 'triggers', title: 'Amp Triggers' }` inserted before
  `complete` in the `STEPS` array (~L81).
- **Self-skip when no relay hardware:** on entering the step, fetch relay boards
  (`GET /api/triggers/boards`, or device detection). If none are present, advance
  past the step automatically — mirrors the existing `cards`-step skip pattern
  (~L294) so hardware-less users never see it.
- **Render** the shared chip-picker component per detected channel.
- **Persist** via the existing `PUT /api/triggers/boards/{boardId}/{channel}`
  API. No new onboarding endpoint is required: triggers reference
  devices/sinks (hardware that already exists at this point), so assignment works
  even though players are not created until `complete()`.

### Open implementation detail (resolve during planning)

Whether trigger assignments are written immediately on each chip change (simplest,
consistent with the main app) or batched and applied in `complete()`. Default:
write immediately via the existing API, matching main-app behavior.

---

## Testing

Extend the existing `tests/MultiRoomAudio.Tests` project:

- **Migration:** YAML containing `custom_sink_name: X` deserializes to
  `CustomSinkNames == [X]`; re-serialization emits only `custom_sink_names`
  (no legacy key).
- **Engine many-to-many:**
  - One sink on two channels → both channels activate on start, both deactivate
    on stop.
  - Two sinks on one channel → channel stays ON until both stop (ref-count
    correctness).
- **Sink deletion:** removing a sink prunes it from every trigger list; a
  channel left with an empty list is cleared.

## Sequencing

1. **PR 1 — Part 1** (backend + main-app UI). Low-risk, self-contained, closes #250.
2. **PR 2 — Part 2** (wizard relay step). Builds on the shipped chip-picker.

Both target `dev`.

## Non-goals

- No change to the relay reference-counting engine internals.
- No new persistence format beyond the field rename + migration shim.
- No unrelated refactoring of the trigger panel or wizard.
