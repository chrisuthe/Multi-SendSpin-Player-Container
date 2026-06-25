# MQTT → Home Assistant Bridge — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish Multi-Room Audio player diagnostics and container health to Home Assistant over MQTT Discovery, plus two per-player controls Music Assistant doesn't offer (restart button, delay-offset number).

**Architecture:** A new `MqttService` (started as a non-blocking `StartupOrchestrator` phase) owns the broker connection, last-will availability, retained HA Discovery configs, state publishing, and command handling. State publishing is event-driven: `PlayerManagerService` raises a `PlayersChanged` event at the same moments it already broadcasts to SignalR, and `MqttService` subscribes. Discovery/state/topic builders are pure functions (no I/O) so they are unit-tested directly. Config lives in a dedicated `mqtt.yaml` resolved with the project's standard precedence (env var → HAOS option → yaml → default).

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, MQTTnet v5, YamlDotNet, xUnit.

**Scope note:** This is Phase 1 of two. Phase 1 covers the bridge engine, the per-player device (diagnostics + restart + offset), and the container/hub device. **Phase 2 (separate plan)** covers the amp/zone entities, the manual-override switch, the `VirtualRelayBoard`, and `TriggerService` change events. The command-subscribe pipeline built here is reused by Phase 2's override switch.

## Global Constraints

Every task implicitly includes these (copied from the spec and project `CLAUDE.md`):

- **Target framework:** .NET 8.0, `Nullable` enabled, `ImplicitUsings` enabled. Follow Microsoft C# conventions. XML doc comments on public APIs.
- **Non-blocking startup (hard rule):** No MQTT failure may block the UI. The MQTT startup phase runs inside `StartupOrchestrator.RunPhaseAsync`, which already catches per-phase exceptions and marks them `Failed` so later phases and the web UI still come up.
- **Opt-in, off by default:** MQTT is disabled unless explicitly enabled.
- **Config precedence:** environment variable → HAOS option (`GetHaosOption<T>`) → `mqtt.yaml` → built-in default. Mirrors `EnvironmentService` patterns.
- **Do not** change the default web port (8096). **Do not** enable trimming. **Do not** add JS frameworks (vanilla JS only for any UI). **Do not** commit secrets — broker password comes from config/env, never hardcoded.
- **MQTTnet v5 API:** use `MqttClientFactory` (not the v4 `MqttFactory`), `MqttClientOptionsBuilder`, `MqttApplicationMessageBuilder`.
- **Tests:** xUnit, in `tests/MultiRoomAudio.Tests/`. Internals are already visible to `MultiRoomAudio.Tests` via `InternalsVisibleTo`.
- **Commits:** Conventional Commits (`feat:`, `fix:`, `test:`, `chore:`). Author as the repo owner — no AI/Claude self-reference, no `Co-Authored-By`.

## File Structure

**Create:**
- `src/MultiRoomAudio/Models/MqttSettings.cs` — config model + API request/response records.
- `src/MultiRoomAudio/Mqtt/MqttTopics.cs` — pure topic/unique-id/node-id naming helpers.
- `src/MultiRoomAudio/Mqtt/HaDiscovery.cs` — pure builders producing HA Discovery config payloads (player device + container device).
- `src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs` — pure builders mapping `PlayerResponse`/health into state-topic JSON.
- `src/MultiRoomAudio/Mqtt/MqttCommand.cs` — pure parser turning an inbound (topic, payload) into a typed command.
- `src/MultiRoomAudio/Services/MqttConfigService.cs` — `YamlFileService<MqttSettings>`; applies env/HAOS overrides.
- `src/MultiRoomAudio/Services/SupervisorMqttResolver.cs` — fetches broker host/port/creds from the HAOS Supervisor `services/mqtt` API.
- `src/MultiRoomAudio/Services/MqttService.cs` — connection lifecycle, LWT, discovery+state publish, command dispatch.
- `src/MultiRoomAudio/Controllers/MqttEndpoint.cs` — `GET/PUT /api/mqtt`, `GET /api/mqtt/status`.
- Tests: `tests/MultiRoomAudio.Tests/Mqtt/MqttTopicsTests.cs`, `HaDiscoveryTests.cs`, `MqttStatePayloadsTests.cs`, `MqttCommandTests.cs`, `MqttConfigServiceTests.cs`.

**Modify:**
- `src/MultiRoomAudio/MultiRoomAudio.csproj` — add MQTTnet package.
- `src/MultiRoomAudio/Services/EnvironmentService.cs` — add `MqttConfigPath`.
- `src/MultiRoomAudio/Services/PlayerManagerService.cs` — add `event Action PlayersChanged`; raise it where SignalR broadcasts happen.
- `src/MultiRoomAudio/Services/StartupOrchestrator.cs` — add the `mqtt` phase + shutdown.
- `src/MultiRoomAudio/Program.cs` — register services + map endpoints.

---

### Task 1: Add the MQTTnet dependency

**Files:**
- Modify: `src/MultiRoomAudio/MultiRoomAudio.csproj:18-38`

**Interfaces:**
- Consumes: nothing.
- Produces: the `MQTTnet` namespace (`MqttClientFactory`, `MqttClientOptionsBuilder`, `MqttApplicationMessageBuilder`, `MqttQualityOfServiceLevel`) available to the project.

- [ ] **Step 1: Add the package reference**

In `src/MultiRoomAudio/MultiRoomAudio.csproj`, inside the first `<ItemGroup>` (the one with `SendSpin.SDK`), add:

```xml
    <!-- MQTT client for Home Assistant bridge -->
    <PackageReference Include="MQTTnet" Version="5.0.1.1416" />
```

- [ ] **Step 2: Restore and confirm it resolves**

Run: `dotnet restore src/MultiRoomAudio/MultiRoomAudio.csproj`
Expected: restore succeeds, no NU1101/NU1102 errors. If `5.0.1.1416` is unavailable, run `dotnet add src/MultiRoomAudio/MultiRoomAudio.csproj package MQTTnet` to pin the latest 5.x and use that version string instead.

- [ ] **Step 3: Build to confirm nothing broke**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/MultiRoomAudio/MultiRoomAudio.csproj
git commit -m "chore: add MQTTnet dependency for Home Assistant bridge"
```

---

### Task 2: MQTT settings model

**Files:**
- Create: `src/MultiRoomAudio/Models/MqttSettings.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs` (asserts defaults here; service added in Task 3)

**Interfaces:**
- Produces:
  - `class MqttSettings` with `bool Enabled`, `string? Host`, `int Port`, `string? Username`, `string? Password`, `bool UseTls`, `string DiscoveryPrefix`, `string BaseTopic`.
  - `record MqttSettingsResponse(bool Enabled, string? Host, int Port, string? Username, bool HasPassword, bool UseTls, string DiscoveryPrefix, string BaseTopic, bool Connected, string? LastError, string Source)`.
  - `record MqttSettingsUpdateRequest(bool? Enabled, string? Host, int? Port, string? Username, string? Password, bool? UseTls, string? DiscoveryPrefix, string? BaseTopic)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs`:

```csharp
using MultiRoomAudio.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttSettingsTests
{
    [Fact]
    public void Defaults_AreSafeAndDisabled()
    {
        var s = new MqttSettings();

        Assert.False(s.Enabled);
        Assert.Equal(1883, s.Port);
        Assert.False(s.UseTls);
        Assert.Equal("homeassistant", s.DiscoveryPrefix);
        Assert.Equal("multiroom-audio", s.BaseTopic);
        Assert.Null(s.Host);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttSettingsTests`
Expected: FAIL — `MqttSettings` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/MultiRoomAudio/Models/MqttSettings.cs`:

```csharp
namespace MultiRoomAudio.Models;

/// <summary>
/// Persisted MQTT bridge configuration (mqtt.yaml). Environment variables and
/// HAOS options override these values at load time.
/// </summary>
public class MqttSettings
{
    /// <summary>Whether the MQTT bridge is enabled. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Broker hostname or IP. Null when unset.</summary>
    public string? Host { get; set; }

    /// <summary>Broker port. 1883 plain, 8883 TLS.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Broker username, or null for anonymous.</summary>
    public string? Username { get; set; }

    /// <summary>Broker password, or null for anonymous.</summary>
    public string? Password { get; set; }

    /// <summary>Whether to connect over TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>Home Assistant MQTT Discovery prefix.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Root topic for this bridge's state/command topics.</summary>
    public string BaseTopic { get; set; } = "multiroom-audio";
}

/// <summary>
/// MQTT settings returned by the API. Never includes the password itself.
/// </summary>
public record MqttSettingsResponse(
    bool Enabled,
    string? Host,
    int Port,
    string? Username,
    bool HasPassword,
    bool UseTls,
    string DiscoveryPrefix,
    string BaseTopic,
    bool Connected,
    string? LastError,
    string Source);

/// <summary>
/// Partial update to MQTT settings. Only non-null fields are applied.
/// A null Password leaves the stored password unchanged; an empty string clears it.
/// </summary>
public record MqttSettingsUpdateRequest(
    bool? Enabled,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    bool? UseTls,
    string? DiscoveryPrefix,
    string? BaseTopic);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttSettingsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Models/MqttSettings.cs tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs
git commit -m "feat: add MQTT settings model"
```

---

### Task 3: MQTT config service (load + override precedence)

**Files:**
- Create: `src/MultiRoomAudio/Services/MqttConfigService.cs`
- Modify: `src/MultiRoomAudio/Services/EnvironmentService.cs` (add `MqttConfigPath`)
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs` (add cases)

**Interfaces:**
- Consumes: `MqttSettings` (Task 2), `EnvironmentService.MqttConfigPath`.
- Produces:
  - `class MqttConfigService : YamlFileService<MqttSettings>` with:
    - `MqttConfigService(EnvironmentService env, ILogger<MqttConfigService> logger)`
    - `MqttSettings Current` — the effective settings after overrides.
    - `string Source` — `"yaml"`, `"env"`, `"haos"`, or `"default"` (highest-priority source that set the host).
    - `void Update(MqttSettingsUpdateRequest request)` — applies, saves yaml.
    - `static MqttSettings ApplyOverrides(MqttSettings fromYaml, IReadOnlyDictionary<string,string?> env, Func<string, bool?> haosBool, Func<string, string?> haosString, Func<string,int?> haosInt)` — pure, testable.

- [ ] **Step 1: Write the failing test**

Add to `tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs`:

```csharp
public class MqttConfigOverrideTests
{
    private static MqttSettings Apply(Dictionary<string, string?> env) =>
        MultiRoomAudio.Services.MqttConfigService.ApplyOverrides(
            new MqttSettings { Host = "yaml-host", Port = 1883 },
            env,
            _ => null, _ => null, _ => null);

    [Fact]
    public void EnvVar_OverridesYamlHost()
    {
        var result = Apply(new() { ["MQTT_HOST"] = "env-host" });
        Assert.Equal("env-host", result.Host);
    }

    [Fact]
    public void EnvEnabled_ParsesTruthyValues()
    {
        var result = Apply(new() { ["MQTT_ENABLED"] = "true" });
        Assert.True(result.Enabled);
    }

    [Fact]
    public void NoOverrides_KeepsYamlValues()
    {
        var result = Apply(new());
        Assert.Equal("yaml-host", result.Host);
        Assert.Equal(1883, result.Port);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttConfigOverrideTests`
Expected: FAIL — `MqttConfigService` does not exist.

- [ ] **Step 3: Add `MqttConfigPath` to `EnvironmentService`**

In `src/MultiRoomAudio/Services/EnvironmentService.cs`, after the `SettingsConfigPath` property (around line 174), add:

```csharp
    /// <summary>
    /// Full path to mqtt.yaml configuration file (MQTT bridge settings).
    /// </summary>
    public string MqttConfigPath => Path.Combine(_configPath, "mqtt.yaml");
```

- [ ] **Step 4: Write minimal implementation**

Create `src/MultiRoomAudio/Services/MqttConfigService.cs`:

```csharp
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Services;

/// <summary>
/// Loads and persists MQTT bridge settings (mqtt.yaml), applying environment
/// variable and HAOS option overrides with standard precedence:
/// env var → HAOS option → yaml → default.
/// </summary>
public class MqttConfigService : YamlFileService<MqttSettings>
{
    private readonly EnvironmentService _env;
    private MqttSettings _current = new();

    public MqttConfigService(EnvironmentService env, ILogger<MqttConfigService> logger)
        : base(env.MqttConfigPath, logger)
    {
        _env = env;
    }

    /// <summary>Effective settings after overrides. Call <see cref="Reload"/> to refresh.</summary>
    public MqttSettings Current => _current;

    /// <summary>Highest-priority source that supplied the broker host.</summary>
    public string Source { get; private set; } = "default";

    /// <summary>Load yaml and apply env/HAOS overrides.</summary>
    public void Reload()
    {
        Load();
        var env = new Dictionary<string, string?>
        {
            ["MQTT_ENABLED"] = Environment.GetEnvironmentVariable("MQTT_ENABLED"),
            ["MQTT_HOST"] = Environment.GetEnvironmentVariable("MQTT_HOST"),
            ["MQTT_PORT"] = Environment.GetEnvironmentVariable("MQTT_PORT"),
            ["MQTT_USERNAME"] = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            ["MQTT_PASSWORD"] = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
            ["MQTT_TLS"] = Environment.GetEnvironmentVariable("MQTT_TLS"),
            ["MQTT_DISCOVERY_PREFIX"] = Environment.GetEnvironmentVariable("MQTT_DISCOVERY_PREFIX"),
            ["MQTT_BASE_TOPIC"] = Environment.GetEnvironmentVariable("MQTT_BASE_TOPIC"),
        };

        _current = ApplyOverrides(
            Data,
            env,
            key => _env.IsHaos ? _env.GetHaosOption<bool?>(key) : null,
            key => _env.IsHaos ? _env.GetHaosOption<string?>(key) : null,
            key => _env.IsHaos ? _env.GetHaosOption<int?>(key) : null);

        Source = ResolveSource(env);
    }

    /// <summary>Apply a partial update and persist to yaml, then reload effective settings.</summary>
    public void Update(MqttSettingsUpdateRequest request)
    {
        Lock.EnterWriteLock();
        try
        {
            if (request.Enabled is { } enabled) Data.Enabled = enabled;
            if (request.Host is not null) Data.Host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host;
            if (request.Port is { } port) Data.Port = port;
            if (request.Username is not null) Data.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username;
            if (request.Password is not null) Data.Password = request.Password.Length == 0 ? null : request.Password;
            if (request.UseTls is { } tls) Data.UseTls = tls;
            if (!string.IsNullOrWhiteSpace(request.DiscoveryPrefix)) Data.DiscoveryPrefix = request.DiscoveryPrefix;
            if (!string.IsNullOrWhiteSpace(request.BaseTopic)) Data.BaseTopic = request.BaseTopic;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
        Save();
        Reload();
    }

    private string ResolveSource(IReadOnlyDictionary<string, string?> env)
    {
        if (!string.IsNullOrWhiteSpace(env["MQTT_HOST"])) return "env";
        if (_env.IsHaos && !string.IsNullOrWhiteSpace(_env.GetHaosOption<string?>("MQTT_HOST"))) return "haos";
        if (!string.IsNullOrWhiteSpace(Data.Host)) return "yaml";
        return "default";
    }

    /// <summary>
    /// Pure override application: env var wins, then HAOS option, then the yaml value.
    /// </summary>
    public static MqttSettings ApplyOverrides(
        MqttSettings fromYaml,
        IReadOnlyDictionary<string, string?> env,
        Func<string, bool?> haosBool,
        Func<string, string?> haosString,
        Func<string, int?> haosInt)
    {
        static bool IsTruthy(string? v) =>
            v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
                          v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        string? EnvStr(string k) => string.IsNullOrWhiteSpace(env.GetValueOrDefault(k)) ? null : env[k];

        return new MqttSettings
        {
            Enabled = EnvStr("MQTT_ENABLED") is { } en ? IsTruthy(en)
                      : haosBool("MQTT_ENABLED") ?? fromYaml.Enabled,
            Host = EnvStr("MQTT_HOST") ?? haosString("MQTT_HOST") ?? fromYaml.Host,
            Port = (EnvStr("MQTT_PORT") is { } p && int.TryParse(p, out var pv)) ? pv
                   : haosInt("MQTT_PORT") ?? fromYaml.Port,
            Username = EnvStr("MQTT_USERNAME") ?? haosString("MQTT_USERNAME") ?? fromYaml.Username,
            Password = EnvStr("MQTT_PASSWORD") ?? haosString("MQTT_PASSWORD") ?? fromYaml.Password,
            UseTls = EnvStr("MQTT_TLS") is { } tls ? IsTruthy(tls)
                     : haosBool("MQTT_TLS") ?? fromYaml.UseTls,
            DiscoveryPrefix = EnvStr("MQTT_DISCOVERY_PREFIX") ?? haosString("MQTT_DISCOVERY_PREFIX") ?? fromYaml.DiscoveryPrefix,
            BaseTopic = EnvStr("MQTT_BASE_TOPIC") ?? haosString("MQTT_BASE_TOPIC") ?? fromYaml.BaseTopic,
        };
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttConfigOverrideTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MultiRoomAudio/Services/MqttConfigService.cs src/MultiRoomAudio/Services/EnvironmentService.cs tests/MultiRoomAudio.Tests/Mqtt/MqttConfigServiceTests.cs
git commit -m "feat: add MQTT config service with env/HAOS override precedence"
```

---

### Task 4: Topic and identifier helpers

**Files:**
- Create: `src/MultiRoomAudio/Mqtt/MqttTopics.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttTopicsTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces a `class MqttTopics` constructed with `(string baseTopic, string discoveryPrefix)` exposing:
  - `string BridgeAvailabilityTopic` → `"{baseTopic}/bridge/availability"`
  - `string PlayerStateTopic(string clientId)`
  - `string PlayerCommandTopic(string clientId, string command)` (e.g. command `"offset"` → `".../offset/set"`)
  - `string PlayerCommandSubscription` → `"{baseTopic}/player/+/+/set"`
  - `string ContainerStateTopic` → `"{baseTopic}/bridge/state"`
  - `string DiscoveryTopic(string component, string objectId)` → `"{prefix}/{component}/{objectId}/config"`
  - `static string Sanitize(string raw)` — lowercases, replaces any char outside `[a-z0-9_-]` with `_`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/MqttTopicsTests.cs`:

```csharp
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttTopicsTests
{
    private readonly MqttTopics _t = new("multiroom-audio", "homeassistant");

    [Fact]
    public void BridgeAvailability_IsStable()
        => Assert.Equal("multiroom-audio/bridge/availability", _t.BridgeAvailabilityTopic);

    [Fact]
    public void PlayerState_UsesSanitizedClientId()
        => Assert.Equal("multiroom-audio/player/abc123/state", _t.PlayerStateTopic("ABC123"));

    [Fact]
    public void PlayerCommand_AppendsSetSuffix()
        => Assert.Equal("multiroom-audio/player/abc123/offset/set", _t.PlayerCommandTopic("abc123", "offset"));

    [Fact]
    public void Discovery_FollowsHaConvention()
        => Assert.Equal("homeassistant/sensor/mra_x/config", _t.DiscoveryTopic("sensor", "mra_x"));

    [Fact]
    public void Sanitize_ReplacesIllegalChars()
        => Assert.Equal("living_room_2", MqttTopics.Sanitize("Living Room #2"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttTopicsTests`
Expected: FAIL — `MqttTopics` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/MultiRoomAudio/Mqtt/MqttTopics.cs`:

```csharp
using System.Text;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds MQTT topic strings and Home Assistant discovery topics.
/// Pure string construction — no I/O.
/// </summary>
public class MqttTopics
{
    private readonly string _base;
    private readonly string _prefix;

    public MqttTopics(string baseTopic, string discoveryPrefix)
    {
        _base = baseTopic.TrimEnd('/');
        _prefix = discoveryPrefix.TrimEnd('/');
    }

    public string BridgeAvailabilityTopic => $"{_base}/bridge/availability";

    public string ContainerStateTopic => $"{_base}/bridge/state";

    public string PlayerStateTopic(string clientId) => $"{_base}/player/{Sanitize(clientId)}/state";

    public string PlayerCommandTopic(string clientId, string command) =>
        $"{_base}/player/{Sanitize(clientId)}/{command}/set";

    /// <summary>Single wildcard subscription covering all player command topics.</summary>
    public string PlayerCommandSubscription => $"{_base}/player/+/+/set";

    public string DiscoveryTopic(string component, string objectId) =>
        $"{_prefix}/{component}/{objectId}/config";

    /// <summary>Lowercase and replace any character outside [a-z0-9_-] with '_'.</summary>
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? c : '_');
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttTopicsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttTopics.cs tests/MultiRoomAudio.Tests/Mqtt/MqttTopicsTests.cs
git commit -m "feat: add MQTT topic and identifier helpers"
```

---

### Task 5: State payload builders

**Files:**
- Create: `src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttStatePayloadsTests.cs`

**Interfaces:**
- Consumes: `PlayerResponse` (`Models/PlayerStatus.cs`).
- Produces a `static class MqttStatePayloads` with:
  - `static string Player(PlayerResponse p)` → JSON string with keys: `state` (lowercased enum), `server_name`, `server_address`, `clock_synced` (`"ON"`/`"OFF"`), `sync_error_ms` (number from `p.Metrics`? no — sync error lives in stats; use `0` placeholder is NOT allowed, so derive from available field — see note), `reconnect_pending` (`"ON"`/`"OFF"`), `reconnect_attempts` (number), `volume`/excluded. Concretely, the payload is built from `PlayerResponse` only.
  - `static string Container(bool ready, string version, int playerCount, string audioBackend, string environment)` → JSON.

  Note on sync error: `PlayerResponse` does not carry sync-error-ms (that's in `PlayerStatsResponse`). Phase 1 publishes the fields present on `PlayerResponse`: `IsClockSynced`, `IsPendingReconnection`, `ReconnectionAttempts`. The "sync error (ms)" sensor is **deferred to Phase 2** where stats are wired in. Update the spec's per-player table accordingly (see Self-Review note).

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/MqttStatePayloadsTests.cs`:

```csharp
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttStatePayloadsTests
{
    private static PlayerResponse SamplePlayer() => new(
        Name: "Living Room",
        State: PlayerState.Playing,
        Device: "dac0",
        ClientId: "abc123",
        ServerUrl: "http://ma",
        ServerName: "Music Assistant",
        ConnectedAddress: "192.168.1.50:8095",
        Volume: 50,
        StartupVolume: 40,
        IsMuted: false,
        DelayMs: 0,
        OutputLatencyMs: 10,
        CreatedAt: DateTime.UnixEpoch,
        ConnectedAt: DateTime.UnixEpoch,
        ErrorMessage: null,
        IsClockSynced: true,
        Metrics: null,
        IsPendingReconnection: false,
        ReconnectionAttempts: 0);

    [Fact]
    public void Player_SerializesExpectedFields()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Player(SamplePlayer()));
        var root = doc.RootElement;

        Assert.Equal("playing", root.GetProperty("state").GetString());
        Assert.Equal("Music Assistant", root.GetProperty("server_name").GetString());
        Assert.Equal("192.168.1.50:8095", root.GetProperty("server_address").GetString());
        Assert.Equal("ON", root.GetProperty("clock_synced").GetString());
        Assert.Equal("OFF", root.GetProperty("reconnect_pending").GetString());
        Assert.Equal(0, root.GetProperty("reconnect_attempts").GetInt32());
    }

    [Fact]
    public void Container_SerializesHealth()
    {
        using var doc = JsonDocument.Parse(
            MqttStatePayloads.Container(ready: true, version: "1.2.3", playerCount: 4,
                audioBackend: "pulse", environment: "haos"));
        var root = doc.RootElement;

        Assert.Equal("ON", root.GetProperty("ready").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal(4, root.GetProperty("player_count").GetInt32());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttStatePayloadsTests`
Expected: FAIL — `MqttStatePayloads` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs`:

```csharp
using System.Text.Json;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds the JSON state payloads published to MQTT. Each device publishes a
/// single JSON document; entity discovery configs reference fields via value_template.
/// </summary>
public static class MqttStatePayloads
{
    private static string OnOff(bool on) => on ? "ON" : "OFF";

    /// <summary>Per-player state document.</summary>
    public static string Player(PlayerResponse p) => JsonSerializer.Serialize(new
    {
        state = p.State.ToString().ToLowerInvariant(),
        server_name = p.ServerName,
        server_address = p.ConnectedAddress,
        clock_synced = OnOff(p.IsClockSynced),
        reconnect_pending = OnOff(p.IsPendingReconnection),
        reconnect_attempts = p.ReconnectionAttempts ?? 0,
        delay_ms = p.DelayMs,
    });

    /// <summary>Container/hub health document.</summary>
    public static string Container(bool ready, string version, int playerCount,
        string audioBackend, string environment) => JsonSerializer.Serialize(new
    {
        ready = OnOff(ready),
        version,
        player_count = playerCount,
        audio_backend = audioBackend,
        environment,
    });
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttStatePayloadsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttStatePayloads.cs tests/MultiRoomAudio.Tests/Mqtt/MqttStatePayloadsTests.cs
git commit -m "feat: add MQTT state payload builders"
```

---

### Task 6: HA Discovery config builders

**Files:**
- Create: `src/MultiRoomAudio/Mqtt/HaDiscovery.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/HaDiscoveryTests.cs`

**Interfaces:**
- Consumes: `MqttTopics` (Task 4), `PlayerResponse`.
- Produces a `class HaDiscovery(MqttTopics topics, string bridgeVersion)` with:
  - `record DiscoveryMessage(string Topic, string Payload)`
  - `IReadOnlyList<DiscoveryMessage> ForPlayer(PlayerResponse p)` — one message per player entity (state sensor, server sensor, clock_synced binary_sensor, reconnect_pending binary_sensor, reconnect_attempts sensor, delay offset number, restart button).
  - `IReadOnlyList<DiscoveryMessage> ForContainer(string instanceId)` — ready binary_sensor, version/player_count/audio_backend/environment sensors.

  Each payload includes `unique_id`, `state_topic`, `value_template`, `availability_topic`, `device` block (`identifiers`, `name`, `manufacturer="Multi-Room Audio"`, `model`, `sw_version`), and for writable entities a `command_topic`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/HaDiscoveryTests.cs`:

```csharp
using System.Linq;
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class HaDiscoveryTests
{
    private readonly HaDiscovery _d = new(new MqttTopics("multiroom-audio", "homeassistant"), "1.2.3");

    private static PlayerResponse Player() => new(
        "Living Room", PlayerState.Playing, "dac0", "abc123", "http://ma",
        "Music Assistant", "192.168.1.50:8095", 50, 40, false, 0, 10,
        DateTime.UnixEpoch, DateTime.UnixEpoch, null, true, null,
        IsPendingReconnection: false, ReconnectionAttempts: 0);

    [Fact]
    public void Player_EmitsRestartButtonWithCommandTopic()
    {
        var msgs = _d.ForPlayer(Player());
        var button = msgs.Single(m => m.Topic.Contains("/button/"));

        using var doc = JsonDocument.Parse(button.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/player/abc123/restart/set", root.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/bridge/availability", root.GetProperty("availability_topic").GetString());
        Assert.StartsWith("mra_abc123_", root.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void Player_OffsetNumberHasCommandTopicAndRange()
    {
        var number = _d.ForPlayer(Player()).Single(m => m.Topic.Contains("/number/"));
        using var doc = JsonDocument.Parse(number.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/player/abc123/offset/set", root.GetProperty("command_topic").GetString());
        Assert.Equal(-5000, root.GetProperty("min").GetInt32());
        Assert.Equal(5000, root.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Player_AllEntitiesShareDeviceIdentifier()
    {
        var ids = _d.ForPlayer(Player()).Select(m =>
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.GetProperty("device").GetProperty("identifiers")[0].GetString();
        }).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal("mra_player_abc123", ids[0]);
    }

    [Fact]
    public void Container_EmitsReadyBinarySensor()
    {
        var msgs = _d.ForContainer("instance1");
        Assert.Contains(msgs, m => m.Topic.Contains("/binary_sensor/") && m.Payload.Contains("\"ready\""));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter HaDiscoveryTests`
Expected: FAIL — `HaDiscovery` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/MultiRoomAudio/Mqtt/HaDiscovery.cs`:

```csharp
using System.Text.Json;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds Home Assistant MQTT Discovery config messages for the bridge's devices.
/// All payloads are retained config JSON; state comes from the shared per-device
/// state topic referenced by each entity's value_template.
/// </summary>
public class HaDiscovery
{
    private readonly MqttTopics _topics;
    private readonly string _version;

    public HaDiscovery(MqttTopics topics, string bridgeVersion)
    {
        _topics = topics;
        _version = bridgeVersion;
    }

    public record DiscoveryMessage(string Topic, string Payload);

    public IReadOnlyList<DiscoveryMessage> ForPlayer(PlayerResponse p)
    {
        var id = MqttTopics.Sanitize(p.ClientId);
        var stateTopic = _topics.PlayerStateTopic(p.ClientId);
        var deviceId = $"mra_player_{id}";
        var device = Device(deviceId, p.Name, "Audio Player");

        DiscoveryMessage Entity(string component, string key, string name,
            Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_{id}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("sensor", "state", $"{p.Name} State", w =>
                w.WriteString("value_template", "{{ value_json.state }}")),
            Entity("sensor", "server", $"{p.Name} Server", w =>
            {
                w.WriteString("value_template", "{{ value_json.server_name }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("binary_sensor", "clock_synced", $"{p.Name} Clock Synced", w =>
            {
                w.WriteString("value_template", "{{ value_json.clock_synced }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("binary_sensor", "reconnect_pending", $"{p.Name} Reconnecting", w =>
            {
                w.WriteString("value_template", "{{ value_json.reconnect_pending }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "reconnect_attempts", $"{p.Name} Reconnect Attempts", w =>
            {
                w.WriteString("value_template", "{{ value_json.reconnect_attempts }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("number", "offset", $"{p.Name} Delay Offset", w =>
            {
                w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "offset"));
                w.WriteString("value_template", "{{ value_json.delay_ms }}");
                w.WriteNumber("min", -5000);
                w.WriteNumber("max", 5000);
                w.WriteString("unit_of_measurement", "ms");
                w.WriteString("mode", "box");
                w.WriteString("entity_category", "config");
            }),
            Entity("button", "restart", $"{p.Name} Restart", w =>
            {
                w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "restart"));
                w.WriteString("payload_press", "PRESS");
                w.WriteString("entity_category", "config");
            }),
        };
    }

    public IReadOnlyList<DiscoveryMessage> ForContainer(string instanceId)
    {
        var id = MqttTopics.Sanitize(instanceId);
        var stateTopic = _topics.ContainerStateTopic;
        var deviceId = $"mra_bridge_{id}";
        var device = Device(deviceId, "Multi-Room Audio", "Controller");

        DiscoveryMessage Entity(string component, string key, string name,
            Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_bridge_{id}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("binary_sensor", "ready", "Multi-Room Audio Ready", w =>
            {
                w.WriteString("value_template", "{{ value_json.ready }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "running");
            }),
            Entity("sensor", "version", "Multi-Room Audio Version", w =>
            {
                w.WriteString("value_template", "{{ value_json.version }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "player_count", "Multi-Room Audio Players", w =>
                w.WriteString("value_template", "{{ value_json.player_count }}")),
            Entity("sensor", "audio_backend", "Multi-Room Audio Backend", w =>
            {
                w.WriteString("value_template", "{{ value_json.audio_backend }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "environment", "Multi-Room Audio Environment", w =>
            {
                w.WriteString("value_template", "{{ value_json.environment }}");
                w.WriteString("entity_category", "diagnostic");
            }),
        };
    }

    private DiscoveryMessage Build(string component, string uniqueId, string name,
        string deviceId, string stateTopic, Action<Utf8JsonWriter> deviceBlock,
        Action<Utf8JsonWriter> extra)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("unique_id", uniqueId);
            w.WriteString("object_id", uniqueId);
            w.WriteString("state_topic", stateTopic);
            w.WriteString("availability_topic", _topics.BridgeAvailabilityTopic);
            w.WriteString("payload_available", "online");
            w.WriteString("payload_not_available", "offline");
            extra(w);
            w.WritePropertyName("device");
            deviceBlock(w);
            w.WriteEndObject();
        }
        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return new DiscoveryMessage(_topics.DiscoveryTopic(component, uniqueId), payload);
    }

    private Action<Utf8JsonWriter> Device(string identifier, string name, string model) => w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("identifiers");
        w.WriteStartArray();
        w.WriteStringValue(identifier);
        w.WriteEndArray();
        w.WriteString("name", name);
        w.WriteString("manufacturer", "Multi-Room Audio");
        w.WriteString("model", model);
        w.WriteString("sw_version", _version);
        w.WriteEndObject();
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter HaDiscoveryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/HaDiscovery.cs tests/MultiRoomAudio.Tests/Mqtt/HaDiscoveryTests.cs
git commit -m "feat: add Home Assistant MQTT discovery config builders"
```

---

### Task 7: Command parser

**Files:**
- Create: `src/MultiRoomAudio/Mqtt/MqttCommand.cs`
- Test: `tests/MultiRoomAudio.Tests/Mqtt/MqttCommandTests.cs`

**Interfaces:**
- Consumes: `MqttTopics.Sanitize`.
- Produces a `static class MqttCommand` with:
  - `enum MqttCommandKind { Unknown, PlayerOffset, PlayerRestart }`
  - `record ParsedCommand(MqttCommandKind Kind, string PlayerClientId, int? IntValue)`
  - `static ParsedCommand Parse(string baseTopic, string topic, string payload)` — returns `Unknown` for anything unrecognized. For `offset`, parses payload as int (null `IntValue` if unparseable).

  Note: player command topics carry the *sanitized* client id; dispatch (Task 9) maps it back to a player by comparing `MqttTopics.Sanitize(player.ClientId)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiRoomAudio.Tests/Mqtt/MqttCommandTests.cs`:

```csharp
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttCommandTests
{
    [Fact]
    public void Parse_OffsetCommand()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "250");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Equal("abc123", c.PlayerClientId);
        Assert.Equal(250, c.IntValue);
    }

    [Fact]
    public void Parse_RestartCommand()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/restart/set", "PRESS");
        Assert.Equal(MqttCommandKind.PlayerRestart, c.Kind);
        Assert.Equal("abc123", c.PlayerClientId);
    }

    [Fact]
    public void Parse_UnknownTopic_ReturnsUnknown()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/other/thing", "x");
        Assert.Equal(MqttCommandKind.Unknown, c.Kind);
    }

    [Fact]
    public void Parse_OffsetWithBadPayload_HasNullValue()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "abc");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Null(c.IntValue);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttCommandTests`
Expected: FAIL — `MqttCommand` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/MultiRoomAudio/Mqtt/MqttCommand.cs`:

```csharp
using System.Globalization;

namespace MultiRoomAudio.Mqtt;

public enum MqttCommandKind { Unknown, PlayerOffset, PlayerRestart }

public record ParsedCommand(MqttCommandKind Kind, string PlayerClientId, int? IntValue);

/// <summary>
/// Parses inbound MQTT command topics into typed commands. Pure — no dispatch.
/// Topic shape: {base}/player/{sanitizedClientId}/{command}/set
/// </summary>
public static class MqttCommand
{
    public static ParsedCommand Parse(string baseTopic, string topic, string payload)
    {
        var prefix = baseTopic.TrimEnd('/') + "/player/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal))
            return new ParsedCommand(MqttCommandKind.Unknown, "", null);

        var rest = topic[prefix.Length..];           // {id}/{command}/set
        var parts = rest.Split('/');
        if (parts.Length != 3 || parts[2] != "set")
            return new ParsedCommand(MqttCommandKind.Unknown, "", null);

        var id = parts[0];
        return parts[1] switch
        {
            "offset" => new ParsedCommand(MqttCommandKind.PlayerOffset, id,
                int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null),
            "restart" => new ParsedCommand(MqttCommandKind.PlayerRestart, id, null),
            _ => new ParsedCommand(MqttCommandKind.Unknown, id, null),
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MultiRoomAudio.Tests --filter MqttCommandTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Mqtt/MqttCommand.cs tests/MultiRoomAudio.Tests/Mqtt/MqttCommandTests.cs
git commit -m "feat: add MQTT command parser"
```

---

### Task 8: `PlayersChanged` event on `PlayerManagerService`

**Files:**
- Modify: `src/MultiRoomAudio/Services/PlayerManagerService.cs` (declare event; raise next to the two `BroadcastStatusUpdateAsync` calls at lines ~1500 and ~3420)

**Interfaces:**
- Consumes: nothing.
- Produces: `public event Action? PlayersChanged;` on `PlayerManagerService`, raised whenever player status is broadcast to SignalR.

- [ ] **Step 1: Declare the event**

In `PlayerManagerService` (near the top with the other fields, after `_hubContext` at line 33), add:

```csharp
    /// <summary>
    /// Raised whenever player status changes (same moments as the SignalR broadcast).
    /// The MQTT bridge subscribes to mirror state without polling.
    /// </summary>
    public event Action? PlayersChanged;
```

- [ ] **Step 2: Raise it at both broadcast sites**

At each location that calls `await _hubContext.BroadcastStatusUpdateAsync(players);` (around lines 1500 and 3420), add immediately after the broadcast line:

```csharp
            PlayersChanged?.Invoke();
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/MultiRoomAudio/Services/PlayerManagerService.cs
git commit -m "feat: raise PlayersChanged event alongside SignalR broadcasts"
```

---

### Task 9: `MqttService` — connection, availability, publish, command dispatch

**Files:**
- Create: `src/MultiRoomAudio/Services/MqttService.cs`

**Interfaces:**
- Consumes: `MqttConfigService` (Task 3), `MqttTopics`/`HaDiscovery`/`MqttStatePayloads`/`MqttCommand` (Tasks 4-7), `PlayerManagerService` (`GetAllPlayers`, `SetDelayOffset`, `RestartPlayerAsync`, `PlayersChanged` from Task 8), `VersionService`, `EnvironmentService`, `StartupProgressService` (for `IsStartupComplete`/ready).
- Produces a `class MqttService` with:
  - `Task InitializeAsync(CancellationToken ct)` — connect (if enabled), subscribe to `PlayersChanged`, publish discovery + initial state. Throws on hard connect failure (the orchestrator marks the phase failed; UI is unaffected).
  - `Task ShutdownAsync(CancellationToken ct)` — publish `offline`, disconnect.
  - `bool IsConnected { get; }` and `string? LastError { get; }` for the status endpoint.

- [ ] **Step 1: Write the implementation**

Create `src/MultiRoomAudio/Services/MqttService.cs`:

```csharp
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MultiRoomAudio.Mqtt;

namespace MultiRoomAudio.Services;

/// <summary>
/// Owns the MQTT broker connection and bridges Multi-Room Audio state and a
/// focused set of controls to Home Assistant via MQTT Discovery.
/// Runs as a StartupOrchestrator phase so any failure is non-blocking.
/// </summary>
public class MqttService
{
    private readonly MqttConfigService _config;
    private readonly PlayerManagerService _players;
    private readonly VersionService _version;
    private readonly EnvironmentService _env;
    private readonly StartupProgressService _startup;
    private readonly ILogger<MqttService> _logger;

    private IMqttClient? _client;
    private MqttTopics? _topics;
    private HaDiscovery? _discovery;
    private string _baseTopic = "multiroom-audio";
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private CancellationTokenSource? _reconnectCts;

    public MqttService(
        MqttConfigService config,
        PlayerManagerService players,
        VersionService version,
        EnvironmentService env,
        StartupProgressService startup,
        ILogger<MqttService> logger)
    {
        _config = config;
        _players = players;
        _version = version;
        _env = env;
        _startup = startup;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _config.Reload();
        var settings = _config.Current;

        if (!settings.Enabled)
        {
            _logger.LogInformation("MQTT bridge disabled; skipping");
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            _logger.LogWarning("MQTT bridge enabled but no broker host configured; skipping");
            LastError = "No broker host configured";
            return;
        }

        _baseTopic = settings.BaseTopic;
        _topics = new MqttTopics(settings.BaseTopic, settings.DiscoveryPrefix);
        _discovery = new HaDiscovery(_topics, _version.GetVersion());

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId($"multiroom-audio-{Environment.MachineName}")
            .WithTcpServer(settings.Host, settings.Port)
            .WithWillTopic(_topics.BridgeAvailabilityTopic)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain()
            .WithCleanSession();

        if (!string.IsNullOrEmpty(settings.Username))
            optionsBuilder.WithCredentials(settings.Username, settings.Password ?? string.Empty);
        if (settings.UseTls)
            optionsBuilder.WithTlsOptions(o => { });

        var options = optionsBuilder.Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        _options = options;
        await ConnectAndAnnounceAsync(ct);

        _players.PlayersChanged += OnPlayersChanged;
    }

    private MqttClientOptions? _options;

    private async Task ConnectAndAnnounceAsync(CancellationToken ct)
    {
        await _client!.ConnectAsync(_options!, ct);
        LastError = null;
        _logger.LogInformation("MQTT bridge connected to broker");

        await _client.SubscribeAsync(_topics!.PlayerCommandSubscription, MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await PublishAvailabilityAsync("online", ct);
        await PublishDiscoveryAsync(ct);
        await PublishAllStateAsync(ct);
    }

    private async Task PublishAvailabilityAsync(string value, CancellationToken ct)
        => await PublishAsync(_topics!.BridgeAvailabilityTopic, value, retain: true, ct);

    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        foreach (var p in _players.GetAllPlayers().Players)
            foreach (var m in _discovery!.ForPlayer(p))
                await PublishAsync(m.Topic, m.Payload, retain: true, ct);

        foreach (var m in _discovery!.ForContainer(_env.EnvironmentName))
            await PublishAsync(m.Topic, m.Payload, retain: true, ct);
    }

    private async Task PublishAllStateAsync(CancellationToken ct)
    {
        foreach (var p in _players.GetAllPlayers().Players)
            await PublishAsync(_topics!.PlayerStateTopic(p.ClientId), MqttStatePayloads.Player(p), retain: true, ct);

        var players = _players.GetAllPlayers().Players;
        await PublishAsync(_topics!.ContainerStateTopic,
            MqttStatePayloads.Container(_startup.IsStartupComplete, _version.GetVersion(),
                players.Count, _env.AudioBackend, _env.EnvironmentName),
            retain: true, ct);
    }

    private void OnPlayersChanged()
    {
        // Fire-and-forget; serialize via the publish lock. Never throw into the caller.
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
                _logger.LogWarning(ex, "Failed to publish MQTT state on player change");
            }
        });
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            var cmd = MqttCommand.Parse(_baseTopic, topic, payload);
            if (cmd.Kind == MqttCommandKind.Unknown) return;

            var player = _players.GetAllPlayers().Players
                .FirstOrDefault(p => MqttTopics.Sanitize(p.ClientId) == cmd.PlayerClientId);
            if (player == null)
            {
                _logger.LogWarning("MQTT command for unknown player id {Id}", cmd.PlayerClientId);
                return;
            }

            switch (cmd.Kind)
            {
                case MqttCommandKind.PlayerOffset when cmd.IntValue is { } ms:
                    _players.SetDelayOffset(player.Name, ms);
                    break;
                case MqttCommandKind.PlayerRestart:
                    await _players.RestartPlayerAsync(player.Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle MQTT command");
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        LastError = e.Exception?.Message ?? e.ReasonString;
        _logger.LogWarning("MQTT disconnected: {Reason}. Reconnecting...", LastError);

        var ct = (_reconnectCts ??= new CancellationTokenSource()).Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (_client != null && _options != null && !_client.IsConnected)
                await ConnectAndAnnounceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT reconnect attempt failed; will retry on next disconnect");
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (_client is null) return;
        await _publishLock.WaitAsync(ct);
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(message, ct);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        _players.PlayersChanged -= OnPlayersChanged;
        _reconnectCts?.Cancel();
        if (_client is { IsConnected: true })
        {
            try
            {
                await PublishAvailabilityAsync("offline", ct);
                await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during MQTT shutdown");
            }
        }
        _client?.Dispose();
    }
}
```

- [ ] **Step 2: Confirm `VersionService.GetVersion()` exists**

Run: `grep -n "public string GetVersion" src/MultiRoomAudio/Services/VersionService.cs`
Expected: a match. If the method has a different name (e.g. `Version` property), adjust the two `_version.GetVersion()` calls to match.

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: Build succeeded, 0 errors. Fix any MQTTnet API mismatches (e.g. `Payload` accessor) revealed here.

- [ ] **Step 4: Commit**

```bash
git add src/MultiRoomAudio/Services/MqttService.cs
git commit -m "feat: add MqttService connection, discovery, state, and command handling"
```

---

### Task 10: Wire into startup and DI

**Files:**
- Modify: `src/MultiRoomAudio/Services/StartupOrchestrator.cs`
- Modify: `src/MultiRoomAudio/Program.cs:166-185` (DI registration)

**Interfaces:**
- Consumes: `MqttService` (Task 9), `MqttConfigService` (Task 3).
- Produces: an `mqtt` startup phase (runs after `triggers`) and DI registrations.

- [ ] **Step 1: Register services in `Program.cs`**

In `src/MultiRoomAudio/Program.cs`, in the singletons block (after `builder.Services.AddSingleton<TriggerService>();` at line 170), add:

```csharp
builder.Services.AddSingleton<MqttConfigService>();
builder.Services.AddSingleton<MqttService>();
```

- [ ] **Step 2: Add the startup phase**

In `StartupOrchestrator.cs`, add a field next to the others (after line 27):

```csharp
    private MqttService _mqtt = null!;
```

In `ExecuteAsync`, after `_hidButtons = _services.GetRequiredService<HidButtonService>();` (line 54), add:

```csharp
        _mqtt = _services.GetRequiredService<MqttService>();
```

After the `hidbuttons` phase (line 76), add:

```csharp
            // Phase 7: Connect MQTT bridge (publishes to Home Assistant). Non-blocking.
            await RunPhaseAsync("mqtt", () => _mqtt.InitializeAsync(stoppingToken), stoppingToken);
```

In `StopAsync`, before `if (_triggers != null)` (line 117), add:

```csharp
        if (_mqtt != null)
            await _mqtt.ShutdownAsync(cancellationToken);
```

- [ ] **Step 3: Build**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/MultiRoomAudio/Services/StartupOrchestrator.cs src/MultiRoomAudio/Program.cs
git commit -m "feat: wire MQTT bridge into startup orchestrator and DI"
```

---

### Task 11: Config API endpoint

**Files:**
- Create: `src/MultiRoomAudio/Controllers/MqttEndpoint.cs`
- Modify: `src/MultiRoomAudio/Program.cs:294` (map endpoints)

**Interfaces:**
- Consumes: `MqttConfigService`, `MqttService`.
- Produces: `MapMqttEndpoints` extension; `GET /api/mqtt`, `PUT /api/mqtt`, `GET /api/mqtt/status`.

- [ ] **Step 1: Write the endpoint**

Create `src/MultiRoomAudio/Controllers/MqttEndpoint.cs`:

```csharp
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

public static class MqttEndpoint
{
    public static void MapMqttEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mqtt");

        group.MapGet("", (MqttConfigService config, MqttService mqtt) =>
            Results.Ok(ToResponse(config, mqtt)))
            .WithTags("MQTT").WithName("GetMqttSettings");

        group.MapPut("", (MqttSettingsUpdateRequest request, MqttConfigService config, MqttService mqtt) =>
        {
            config.Update(request);
            return Results.Ok(new
            {
                success = true,
                message = "MQTT settings saved. Restart the add-on/container to apply.",
                settings = ToResponse(config, mqtt)
            });
        })
            .WithTags("MQTT").WithName("UpdateMqttSettings");

        group.MapGet("/status", (MqttConfigService config, MqttService mqtt) =>
            Results.Ok(new { connected = mqtt.IsConnected, lastError = mqtt.LastError, source = config.Source }))
            .WithTags("MQTT").WithName("GetMqttStatus");
    }

    private static MqttSettingsResponse ToResponse(MqttConfigService config, MqttService mqtt)
    {
        var s = config.Current;
        return new MqttSettingsResponse(
            s.Enabled, s.Host, s.Port, s.Username, !string.IsNullOrEmpty(s.Password),
            s.UseTls, s.DiscoveryPrefix, s.BaseTopic, mqtt.IsConnected, mqtt.LastError, config.Source);
    }
}
```

- [ ] **Step 2: Map it in `Program.cs`**

After `app.MapSettingsEndpoints();` (line 294), add:

```csharp
app.MapMqttEndpoints();
```

- [ ] **Step 3: Build**

Run: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Smoke-test the endpoint**

Run (in one shell): `MOCK_HARDWARE=true dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj`
Then (another shell): `curl -s http://localhost:8096/api/mqtt | jq`
Expected: JSON with `"enabled": false` and `"connected": false`. Stop the app afterward.

- [ ] **Step 5: Commit**

```bash
git add src/MultiRoomAudio/Controllers/MqttEndpoint.cs src/MultiRoomAudio/Program.cs
git commit -m "feat: add MQTT settings API endpoint"
```

---

### Task 12: Full verification pass

**Files:** none (verification only)

- [ ] **Step 1: Run the whole test suite**

Run: `dotnet test tests/MultiRoomAudio.Tests`
Expected: all tests pass, including the five new MQTT test classes.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build squeezelite-docker.sln -c Debug`
Expected: Build succeeded, 0 errors, 0 new warnings from the new files.

- [ ] **Step 3: End-to-end against a local broker (manual, optional but recommended)**

If a broker is available (e.g. `docker run -it -p 1883:1883 eclipse-mosquitto`), set
`MQTT_ENABLED=true MQTT_HOST=localhost`, run the app with `MOCK_HARDWARE=true`, and
subscribe: `mosquitto_sub -t 'homeassistant/#' -v`.
Expected: retained discovery configs for the container device appear; `multiroom-audio/bridge/availability` shows `online`.

- [ ] **Step 4: Commit (if any fixups were needed)**

```bash
git add -A
git commit -m "test: verify MQTT bridge phase 1 end to end"
```

---

## Self-Review

**1. Spec coverage:**
- Broker auto-detect under HAOS / manual under Docker → Tasks 2, 3 (env/HAOS/yaml precedence). **Gap:** The HAOS Supervisor `services/mqtt` *auto-fetch* (`SupervisorMqttResolver`) listed in the spec is **not yet implemented** — Task 3 covers HAOS *options* overrides but not the Supervisor service-discovery API. This is intentionally deferred: it's an enhancement on top of a working manual/env path and depends on Supervisor API specifics best verified live. **Tracked as a Phase 1 follow-up below.** The `SupervisorMqttResolver` file is removed from this plan's scope.
- Per-player diagnostics (state, server, clock synced, reconnect) → Tasks 5, 6. Sync-error-ms sensor **deferred to Phase 2** (needs `PlayerStatsResponse`, not on `PlayerResponse`).
- Per-player controls (restart, offset) → Tasks 6, 7, 9.
- Container/hub device → Tasks 5, 6, 9.
- LWT availability, retained discovery, reconnect → Task 9.
- Non-blocking startup → Task 10 (orchestrator phase).
- Opt-in/off by default → Task 2 defaults, Task 9 guard.
- Config API → Task 11.
- Amp/zone entities, override switch, virtual relay, trigger events → **Phase 2 (separate plan)**, as scoped.

**2. Placeholder scan:** No TBD/TODO left in task steps. The two deferrals (Supervisor auto-fetch, sync-error sensor) are explicitly scoped out with rationale, not left as silent gaps.

**3. Type consistency:** `PlayersChanged` (Task 8) is consumed in Task 9. `MqttConfigService.Current/Source/Update/ApplyOverrides` (Task 3) are consumed in Tasks 9 and 11. `MqttTopics`, `HaDiscovery.DiscoveryMessage`, `MqttStatePayloads`, `MqttCommand`/`ParsedCommand`/`MqttCommandKind` signatures match across tasks. `PlayerResponse` field names used in Tasks 5/6 match `Models/PlayerStatus.cs`. Player-manager method names (`GetAllPlayers`, `SetDelayOffset`, `RestartPlayerAsync`) match the grep'd signatures.

## Phase 1 Follow-ups (after merge, before or during Phase 2)

- **HAOS Supervisor MQTT auto-discovery:** add `SupervisorMqttResolver` that calls the Supervisor `services/mqtt` API (using `SUPERVISOR_TOKEN`) to auto-fill broker host/port/credentials when running as an add-on and no manual host is set. Verify the API shape against a live HAOS instance first.
- **Settings UI + onboarding wizard:** add an MQTT settings panel to the web UI (vanilla JS). Per project guideline, decide whether the MQTT toggle also belongs in `wwwroot/js/wizard.js`.
- **HAOS add-on config schema:** expose `MQTT_*` / option keys in `multiroom-audio/config.yaml` so the add-on UI can set them.

## Phase 2 (separate plan, after Phase 1 merges)

Amp/zone discovery (status binary_sensors + scheduled-off sensor + board-connected binary_sensor), the manual-override `switch` (reusing this plan's command pipeline), the `VirtualRelayBoard : IRelayBoard` + `RelayBoardType.Virtual`, `TriggerService` change events, and the per-amp HA bridging documentation.
