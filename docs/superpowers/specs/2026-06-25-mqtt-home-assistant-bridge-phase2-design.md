# MQTT → Home Assistant Bridge — Phase 2 Design

**Date:** 2026-06-25
**Status:** Approved (design); pending implementation plan
**Builds on:** Phase 1 (`docs/superpowers/specs/2026-06-24-mqtt-home-assistant-bridge-design.md`), merged to `dev` via PR #235. Phase 2 branch `feat/mqtt-ha-bridge-phase2` is based off `dev` and targets `dev`.

## Summary

Phase 2 completes the MQTT bridge by exposing the **12V amp/relay zones** to Home Assistant, adding a **manual-override switch**, introducing a **virtual relay board** (a software relay whose power state is published over MQTT so an HA-controlled smart outlet can be driven by Multi-Room's playback auto-logic), and letting HAOS users **turn MQTT on from the add-on configuration**.

It reuses Phase 1's bridge engine, command pipeline, and pure builders. Player volume/mute/transport remain out of scope (Music Assistant owns those); the per-player `sync_error_ms` sensor is explicitly **dropped** (the clock-synced binary_sensor already gives HA the actionable signal, and the raw value would add MQTT traffic for little gain).

## Scope decisions

| Decision | Choice |
|---|---|
| Override switch behavior | **Sticky override**: ON forces the amp on and suspends auto-logic for that channel; OFF releases back to auto (re-evaluates current playback — stays on if playing, else normal off-delay) |
| Virtual relay → smart outlet | Decoupled (Phase 1 approach #1): the virtual amp's power state is published like any amp; the user writes one HA automation per zone to mirror it onto their outlet |
| Virtual board MQTT path | None of its own — its power state flows to HA through the normal amp-state publish driven by `TriggersChanged` |
| `sync_error_ms` sensor | Dropped (YAGNI; clock-synced binary_sensor suffices) |
| HAOS enable | Add-on `config.yaml` options (`mqtt_enabled` + broker fields); Docker unchanged (env vars) |

## Background / current state (Phase 1 + trigger layer)

- Phase 1 shipped: `MqttService` (connection, LWT, retained discovery/state, reconnect, command dispatch), pure units (`MqttTopics`, `HaDiscovery`, `MqttStatePayloads`, `MqttCommand`), `MqttConfigService` (env → HAOS → yaml → default), the per-player + container devices, and `GET/PUT /api/mqtt`.
- `TriggerService` manages multiple relay boards. Per-channel state lives in `TriggerChannelState` (`IsActive`, `LastActivated`, `OffDelayTimer`, `ActivePlayerCount`). Relays are driven by custom-sink activity → `IRelayBoard.SetRelay(channel, on)`, with off-delay timers.
- `ManualControl(boardId, channel, on)` exists but is a **one-shot test** — it sets the relay and cancels the pending off-timer; it does NOT suspend the auto-logic, so the auto-pipeline can re-assert. A true override is new behavior.
- Boards are built by `IRelayBoardFactory.CreateBoard(boardId, boardType)` (Real and Mock factories) and conform to the synchronous `IRelayBoard` interface (`SetRelay`/`GetRelay`/`CurrentState`/`IsConnected`/`Open`/`Close`/...).
- `TriggerResponse` carries per-channel `RelayState`, `IsActive`, `LastActivated`, `ScheduledOffTime`. `TriggerBoardResponse` carries `IsConnected`.
- HAOS add-on `config.yaml` currently exposes only `log_level`. `EnvironmentService.GetHaosOption<T>(key)` reads `/data/options.json` (existing keys use snake_case: `mock_hardware`, `buffer_seconds`).

## 1. TriggerService changes (core)

All additions live in `TriggerService`, above the board layer, so they work uniformly for every board type (HID/FTDI/Modbus/LCUS/Virtual).

**Sticky override.** Extend `TriggerChannelState` with `bool IsOverridden`. New method:

```csharp
public bool SetOverride(string boardId, int channel, bool on)
```

- `on` → cancel the off-delay timer, `SetRelay(channel, true)`, set `IsOverridden = true`. While `IsOverridden`, the auto-logic path skips this channel (it neither turns it off nor schedules an off-timer).
- `off` (release) → set `IsOverridden = false`, then re-evaluate auto: if `ActivePlayerCount > 0` leave the relay on; otherwise start the normal off-delay timer.
- Validation and disconnected-board handling mirror `ManualControl` (log + return false, never throw).

The auto-logic guard is a single `if (state.IsOverridden) return;` (or equivalent skip) in the existing sink-activity handler that would otherwise drive the channel.

**Change event.** `public event Action? TriggersChanged;` raised wherever a channel/board state changes: auto on/off, off-timer fire, manual control, override set/release, board connect/disconnect. Coarse-grained — `MqttService` re-reads `GetStatus()` and republishes (same pattern as Phase 1's `PlayersChanged`).

**Status surfacing.** Add `bool IsOverridden` to `TriggerResponse` so the API/UI and MQTT reflect override state.

## 2. Virtual relay board

`VirtualRelayBoard : IRelayBoard` + new `RelayBoardType.Virtual`.

- Software-only: records `SetRelay`/`GetRelay`/`CurrentState` in memory; `IsConnected` is always true (the board itself is always ready). Essentially `MockRelayBoard` semantics, but a real user-facing board type.
- Created by **both** `RealRelayBoardFactory` and `MockRelayBoardFactory` for `RelayBoardType.Virtual` (it is software, so it exists in real deployments too). `CanCreate(... Virtual)` returns true in both.
- **No MQTT dependency in the relay layer.** A zone's amp-power state reaches HA through the normal amp-state publish driven by `TriggersChanged` (Section 3). When the auto-logic drives a virtual channel on, the amp `binary_sensor` flips to ON in HA and the user's automation mirrors it onto their outlet.
- **Added manually** via the trigger API/UI (not hardware-discovered). The user supplies a display name and channel count, assigns sinks + off-delays per channel like any board. `BoardId` is a stable `VIRTUAL:<id>` generated once on creation and persisted in `triggers.yaml`.
- **MQTT-down signal.** A virtual board only does anything when MQTT is connected. This is surfaced through the **bridge LWT availability**, not the per-board sensor: when MQTT drops or is disabled, the bridge's last-will marks every entity (including the amp zone) `unavailable` in Home Assistant — the clearest "not reaching HA" signal. The amp's `board_connected` diagnostic therefore reflects the board's own `IsConnected` (always true for a software board once opened), and the web UI shows a static "requires MQTT" hint on virtual boards. (Implementation note: an earlier draft had `board_connected` itself reflect MQTT-usable state; that was dropped because, while MQTT is connected we are publishing, and while it is down LWT already makes the entity unavailable — so a per-board MQTT-usable flag would never differ observably from `IsConnected`.)

## 3. MQTT layer extension

Builds on Phase 1's tested pure units; each extension is TDD'd in the same style.

**New amp/zone device per `(boardId, channel)`** — HA device id `mra_amp_<Sanitize(boardId)>_<channel>`:

| Entity | Platform | R/W | Source |
|---|---|---|---|
| Amp power | binary_sensor (`device_class: power`) | R | `TriggerResponse.RelayState` |
| Scheduled off | sensor (`device_class: timestamp`) | R | `TriggerResponse.ScheduledOffTime` |
| Manual override | switch | R/W | `TriggerResponse.IsOverridden` ← / → `SetOverride` |
| Board connected | binary_sensor (diagnostic, `device_class: connectivity`) | R | board `IsConnected` (MQTT-down is surfaced via the bridge LWT availability — see §2) |

All share one device identifier and the same availability/device-block pattern as Phase 1's player entities.

**Pure-unit extensions:**
- `MqttTopics`: `AmpStateTopic(boardId, channel)`, `AmpCommandTopic(boardId, channel, command)`, `AmpCommandSubscription`. Zone key = `Sanitize(boardId)_<channel>`.
- `MqttStatePayloads.Amp(TriggerResponse t, bool boardConnected)` → `{ power: ON/OFF, scheduled_off: <iso or null>, override: ON/OFF, board_connected: ON/OFF }`.
- `HaDiscovery.ForAmp(string boardId, TriggerResponse t)` → the four entities above (power, scheduled-off, override switch with command_topic, board-connected diagnostic), shared device identifier, friendly names (e.g. zone name or `"<board> CH<n>"`).
- `MqttCommand`: new `MqttCommandKind.AmpOverride`; parse `{base}/amp/{zone}/override/set` → `(boardId, channel, on)`. Zone→(board,channel) resolves by matching `Sanitize` over the live board list (same round-trip approach as players).

**MqttService wiring:**
- Subscribe to `TriggerService.TriggersChanged` → republish amp discovery + state (mirrors the `PlayersChanged` handler; fire-and-forget, swallows its own exceptions).
- Add the amp command-topic subscription; dispatch `AmpOverride` → `TriggerService.SetOverride(boardId, channel, on)`.
- On connect, publish amp discovery/state alongside players + container. Constructor gains a `TriggerService` dependency.

## 4. API / UI

- `POST /api/triggers/boards` extended to accept `BoardType.Virtual` (no enumeration; generates `VIRTUAL:<id>`).
- New `PUT /api/triggers/boards/{boardId}/{channel}/override` → `SetOverride`.
- Per-channel status response (`TriggerResponse`) gains `IsOverridden`.
- **Web UI** (vanilla JS, in the existing triggers section): an "Add virtual board" affordance, a per-channel override toggle + indicator, and the "MQTT not connected" hint on virtual boards. Per the project UI guideline, confirm at implementation time whether the virtual-board option also belongs in the onboarding wizard (`wwwroot/js/wizard.js`).

## 5. HAOS add-on MQTT options

Add to `multiroom-audio/config.yaml`:

```yaml
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

- HAOS users get a toggle + broker fields in the add-on config panel; `password?` masks the secret.
- **Docker unchanged** — keeps using the `MQTT_*` env vars from Phase 1.
- **Key-casing alignment (fix from Phase 1):** `MqttConfigService` currently looks up HAOS options with env-var-style keys (`"MQTT_ENABLED"`, `"MQTT_HOST"`), which never match HAOS's snake_case option keys. Phase 2 changes the HAOS lookups to `mqtt_enabled`, `mqtt_host`, `mqtt_port`, `mqtt_username`, `mqtt_password`, `mqtt_tls`. Env-var keys (Docker) stay `MQTT_*`. Precedence is unchanged: env var → HAOS option → yaml → default. This also exercises the `"haos"` config source that Phase 1's review flagged as currently dead.

## Error handling

Consistent with Phase 1 and the non-blocking-startup rule:
- `SetOverride` on a missing/disconnected board → validated like `ManualControl`; logs and returns false, never throws.
- Amp command payloads validated like player commands; bad payloads logged and ignored.
- A virtual board with MQTT off does not error — it records state; the diagnostic shows "MQTT not connected."
- The `TriggersChanged` publish handler is fire-and-forget and swallows its own exceptions.
- No new startup phase needed — Phase 1's `mqtt` phase already runs after `triggers`, so `TriggerService` is initialized before `MqttService` subscribes.

## Testing

- Pure units: `MqttTopics` amp topics, `MqttStatePayloads.Amp`, `HaDiscovery.ForAmp`, `MqttCommand` amp parsing — unit tests, Phase 1 style.
- `VirtualRelayBoard` — unit-testable with no hardware (like `MockRelayBoard`): SetRelay/GetRelay/CurrentState round-trip, always connected.
- `SetOverride` sticky semantics — unit tests against `TriggerService` with a mock board: ON suspends auto-off; release with active playback stays on; release while idle starts the off-delay.
- `MqttConfigService` HAOS key resolution — unit test that snake_case HAOS options resolve and override yaml (the previously-dead `"haos"` source).
- `MOCK_HARDWARE=true` + a virtual board — full end-to-end path with no USB.

## Out of scope (Phase 2)

- Player volume/mute/transport (MA owns these).
- `sync_error_ms` sensor (dropped).
- HAOS Supervisor `services/mqtt` broker auto-detect (the add-on config-options path in Section 5 supersedes the need for v1; Supervisor auto-fill remains a possible future enhancement).
- Decoupling discovery republish from every state tick (Phase 1 follow-up; both player and amp discovery are currently republished on each change — idempotent but chatty).

## File touch list (informs the plan)

**Modify:**
- `src/MultiRoomAudio/Services/TriggerService.cs` — `SetOverride`, `IsOverridden` state + auto-logic guard, `TriggersChanged` event.
- `src/MultiRoomAudio/Models/TriggerModels.cs` — `RelayBoardType.Virtual`, `TriggerResponse.IsOverridden`.
- `src/MultiRoomAudio/Relay/RealRelayBoardFactory.cs`, `MockRelayBoardFactory.cs` — create `Virtual`.
- `src/MultiRoomAudio/Mqtt/MqttTopics.cs`, `MqttStatePayloads.cs`, `HaDiscovery.cs`, `MqttCommand.cs` — amp additions.
- `src/MultiRoomAudio/Services/MqttService.cs` — `TriggerService` dep, `TriggersChanged` subscription, amp publish + command dispatch.
- `src/MultiRoomAudio/Services/MqttConfigService.cs` — snake_case HAOS keys.
- `src/MultiRoomAudio/Controllers/TriggersEndpoint.cs` — override endpoint, virtual board add.
- `src/MultiRoomAudio/wwwroot/js/*` — virtual board + override UI.
- `multiroom-audio/config.yaml` — MQTT options/schema.

**Create:**
- `src/MultiRoomAudio/Relay/VirtualRelayBoard.cs`
- Tests under `tests/MultiRoomAudio.Tests/` for the pure units, `VirtualRelayBoard`, `SetOverride`, and HAOS key resolution.
