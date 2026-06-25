# MQTT → Home Assistant Bridge — Design

**Date:** 2026-06-24
**Status:** Approved (design); pending implementation plan

## Summary

Add an opt-in MQTT bridge that publishes Multi-Room Audio state and a focused set
of controls to Home Assistant via MQTT Discovery. The bridge deliberately exposes
**only what Music Assistant does not already provide**. Players already appear in
Home Assistant as `media_player` entities through the Sendspin/Music Assistant
connection, so playback, volume, mute, and transport are intentionally **out of
scope**.

The bridge focuses on three areas Music Assistant cannot surface:

1. **Per-player diagnostics + a couple of controls** (sync/connection health,
   restart, delay offset).
2. **12V amp/relay zones** — status, manual override, and a new **virtual relay**
   board type so amps controlled by an HA smart outlet can still be driven by
   Multi-Room's playback-triggered auto-logic.
3. **Container/hub health** — version, health, environment.

## Background / current state

- The app has **no MQTT today**. Live state is pushed to the web UI over SignalR
  (`Hubs/PlayerStatusHub.cs`) via `BroadcastStatusUpdateAsync`.
- State is already well-modeled in records: `PlayerResponse`, `PlayerStatsResponse`,
  `TriggerFeatureResponse`/`TriggerBoardResponse`/`TriggerResponse`.
- Relays are an abstraction: `IRelayBoard` with HID/FTDI/Modbus/LCUS/Mock
  implementations, all driven by `TriggerService` auto-logic (custom sink active →
  relay on; off-delay elapsed → relay off; manual control supported).
- `EnvironmentService` distinguishes HAOS (Supervisor present) from standalone Docker.

### Key constraint: no MQTT `media_player`

Home Assistant has **no MQTT Discovery platform for `media_player`**. The bridge can
only create `sensor`, `binary_sensor`, `switch`, `number`, `button`, `select`, and
`text` entities. This reinforces the scope decision: the bridge is *control +
diagnostics*, not a second media player. The real media player is the one Music
Assistant already exposes.

## Scope decisions

| Decision | Choice |
|---|---|
| What to expose | Only what MA does not (no volume/mute/transport per player) |
| Amp/relay interaction | Status + manual override switch + new virtual relay board type |
| Virtual relay wiring | Decoupled: Multi-Room publishes state; user bridges to the real outlet with one HA automation per amp |
| Broker connection | Auto-detect under HAOS (Supervisor `services/mqtt`); manual config (env + UI) under Docker |
| Per-player controls | Restart (button), Delay offset (number) |
| Per-player diagnostics | Player state, Connected server, Sync health (clock synced + sync error ms), Reconnection status |
| Enablement | Opt-in, off by default (mirrors the trigger feature) |

Explicitly **out of scope** for v1: per-player volume/mute/transport (MA owns these),
sound-card profile management, custom-sink CRUD, onboarding entities, deep
"stats for nerds" buffer/throughput internals, and per-player device-select.

## Architecture

A new `MqttService` registered as a hosted service, active only when MQTT is enabled.

- **Library:** MQTTnet (managed client — async, TLS, LWT, auto-reconnect).
- **Connection config:** from `EnvironmentService`.
  - HAOS: pull broker host/port/credentials from the Supervisor `services/mqtt` API.
  - Docker: read `MQTT_*` env vars + a settings UI page.
- **State output is event-driven, not polled.** Introduce small change notifiers so
  the same moments that drive SignalR also drive MQTT:
  - `PlayerManagerService` raises a "player state changed" event.
  - `TriggerService` raises a "relay/zone changed" event.
  - Both SignalR (`PlayerStatusHub`) and `MqttService` subscribe. This keeps one
    source of truth and means MQTT freshness matches the web UI, with no extra polling.
- **Command input:** writable entities' command topics map to the **same service
  methods the REST endpoints already call** — no duplicated business logic, just a
  new transport.

### Data flow

```
playback / device / relay change
        │
        ▼
PlayerManagerService / TriggerService  ── raises change event
        │                                   │
        ▼                                   ▼
  PlayerStatusHub (SignalR)            MqttService
        │                                   │
        ▼                                   ▼
     Web UI                          MQTT broker → Home Assistant

HA command (e.g. toggle override, restart, set offset)
        │
        ▼
MqttService (command topic handler)
        │
        ▼
existing service method (same one REST calls)
```

## Home Assistant Discovery

- Discovery config topics are **retained**, published under the `homeassistant/`
  prefix (configurable), so entities appear automatically.
- **Availability:** a single bridge LWT topic (`.../bridge/availability`). If
  Multi-Room dies or the broker drops the connection, every entity becomes
  `unavailable` in HA — no stale state.
- **Unique IDs** derive from existing stable IDs:
  - Player device ← `PlayerResponse.ClientId`
  - Amp/zone device ← `BoardId` + channel
  - Container device ← a stable per-instance ID

### Entities

**Per-player device** (id ← `ClientId`)

| Entity | Platform | R/W | Maps to |
|---|---|---|---|
| Player state | sensor | R | `PlayerResponse.State` |
| Connected server (MA name + IP) | sensor | R | `ServerName` / `ConnectedAddress` |
| Clock synced | binary_sensor | R | `IsClockSynced` |
| Sync error (ms) | sensor | R | `SyncStats.SyncErrorMs` |
| Reconnect pending | binary_sensor | R | `IsPendingReconnection` |
| Reconnect attempts | sensor | R | `ReconnectionAttempts` |
| Delay offset (ms) | number | R/W | `PUT /api/players/{name}/offset` |
| Restart | button | W | `POST /api/players/{name}/restart` |

**Per-amp/zone device** (id ← `BoardId:channel`) — applies to every board type
including virtual:

| Entity | Platform | R/W | Maps to |
|---|---|---|---|
| Amp power | binary_sensor | R | `TriggerResponse.RelayState` |
| Scheduled off-time | sensor | R | `TriggerResponse.ScheduledOffTime` |
| Board connected | binary_sensor | R | `TriggerBoardResponse.IsConnected` |
| Manual override | switch | R/W | forces on/off, suspends auto-off until released |

**Container/hub device** (id ← instance)

| Entity | Platform | R/W |
|---|---|---|
| Healthy / ready | binary_sensor | R |
| Version | sensor | R |
| Player count | sensor | R |
| Audio backend | sensor | R |
| Environment (HAOS/Docker) | sensor | R |

## Virtual relay board (new)

A new `VirtualRelayBoard : IRelayBoard` alongside the existing implementations, plus
a new `RelayBoardType.Virtual`.

- **No USB hardware.** `Set(channel, on)` records state and signals `MqttService` to
  publish that channel's on/off.
- **Configured like any other board** in the trigger UI: choose "Virtual board",
  pick channel count, assign custom sinks + off-delays. The entire `TriggerService`
  auto-trigger pipeline (sink active → on, off-delay → off, manual override) drives
  it **unchanged** — virtual boards are invisible to that logic because they honor
  the same `IRelayBoard` contract.
- **In HA** it surfaces as the per-amp/zone device above. The user writes **one HA
  automation per virtual amp**: "when this amp's power → on, turn on
  `switch.<their_outlet>`" (decoupled approach — Multi-Room never needs to know the
  real device, and it works with any HA-controllable outlet regardless of underlying
  protocol).
- **MQTT dependency:** a virtual board only functions when MQTT is connected. It must
  surface a clear "MQTT not connected" state rather than silently no-op'ing.

The only new wiring is `VirtualRelayBoard` → `MqttService` for state output; no
changes to `TriggerService` mapping/timer logic.

## Configuration

- MQTT is **opt-in, off by default** — a feature toggle in settings (mirrors the
  trigger feature).
- New config persisted via the existing `ConfigurationService` / YAML pattern:
  `enabled`, broker `host`/`port`/`username`/`password`/`tls`, `discoveryPrefix`.
- Under HAOS, broker fields are auto-filled from the Supervisor but remain editable.
- **UI / onboarding:** when implementing the settings UI, ask whether the MQTT toggle
  should also be added to the onboarding wizard (`wwwroot/js/wizard.js`), per the
  project's UI-change guideline.

### Likely environment variables (Docker mode)

| Variable | Purpose |
|---|---|
| `MQTT_ENABLED` | Enable the bridge |
| `MQTT_HOST` | Broker host |
| `MQTT_PORT` | Broker port (default 1883 / 8883 TLS) |
| `MQTT_USERNAME` | Broker username |
| `MQTT_PASSWORD` | Broker password |
| `MQTT_TLS` | Use TLS |
| `MQTT_DISCOVERY_PREFIX` | HA discovery prefix (default `homeassistant`) |

(Exact names to be finalized in the implementation plan.)

## Error handling

Must follow the project's **non-blocking startup** rule — no MQTT failure may block
the UI from becoming usable.

- MQTT connection runs as a `StartupOrchestrator` phase that catches its own
  failures; an unreachable/missing broker marks the phase `Failed`, and subsequent
  phases + the web UI still come up fully.
- Auto-reconnect with backoff (MQTTnet managed client). On reconnect, re-publish
  retained discovery configs + current state so HA re-syncs.
- LWT marks all entities `unavailable` if the bridge drops.
- Command-topic payloads are validated the same way the REST layer validates input;
  bad payloads are logged and ignored, never crash a handler.

## Testing

- `VirtualRelayBoard` is unit-testable with no hardware (like `MockRelayBoard`).
- Discovery-payload builders are pure functions → unit tests assert correct topics,
  unique IDs, and availability references.
- `MqttService` tested against a mocked `IMqttClient` (or a local broker) to verify
  publish-on-state-change and command round-trips.
- `MOCK_HARDWARE=true` + a virtual board exercises a full end-to-end path with no USB
  and no DAC.

## Out of scope (v1)

- Per-player volume/mute/transport (Music Assistant owns these).
- Sound-card profile management, custom-sink CRUD, onboarding entities.
- Deep buffer/throughput "stats for nerds" internals as entities.
- Per-player output-device select.
- Multi-Room directly commanding HA entities (coupled virtual-relay approach #2).

## Open items for the implementation plan

- Final env-var names and config schema shape.
- Exact topic structure under the discovery prefix.
- Whether the MQTT toggle is added to the onboarding wizard.
- Precise device-class / unit metadata per entity (e.g. `duration` ms, diagnostic
  entity category).
