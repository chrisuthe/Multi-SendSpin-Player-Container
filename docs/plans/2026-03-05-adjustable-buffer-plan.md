# Configurable Audio Buffer Size - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a global setting (5-30 seconds, step 5) to configure the audio buffer size, with a settings UI showing per-sample-rate memory estimates.

**Architecture:** New `BufferSeconds` property on `EnvironmentService` backed by `settings.yaml` via `ConfigurationService`. New REST endpoint `GET/PUT /api/settings/buffer`. New "System" settings modal in the web UI with a slider and memory table. `PlayerManagerService` reads buffer size once at construction.

**Tech Stack:** C# ASP.NET Core 8.0, YamlDotNet, vanilla JS

---

### Task 1: Add BufferSeconds to EnvironmentService

**Files:**
- Modify: `src/MultiRoomAudio/Services/EnvironmentService.cs`

**Step 1: Add constant, field, and property**

Add after line 30 (after `AdvancedFormatsEnv` constant):

```csharp
private const string BufferSecondsEnv = "BUFFER_SECONDS";
private const int DefaultBufferSeconds = 30;
private const int MinBufferSeconds = 5;
private const int MaxBufferSeconds = 30;
```

Add a mutable field (unlike the other readonly fields, this one can be updated at runtime via the settings API):

```csharp
private int _bufferSeconds;
```

Add public property after `EnableAdvancedFormats` (around line 119):

```csharp
/// <summary>
/// Audio buffer size in seconds (5-30, step 5). Applies to all players.
/// Changing this requires a player restart.
/// </summary>
public int BufferSeconds
{
    get => _bufferSeconds;
    set => _bufferSeconds = Math.Clamp(value, MinBufferSeconds, MaxBufferSeconds);
}
```

**Step 2: Add detection method**

Add after `DetectAdvancedFormats()` method:

```csharp
private int DetectBufferSeconds()
{
    // Check environment variable first
    var bufferSecondsValue = Environment.GetEnvironmentVariable(BufferSecondsEnv);
    if (!string.IsNullOrEmpty(bufferSecondsValue))
    {
        if (int.TryParse(bufferSecondsValue, out var parsed))
        {
            var clamped = Math.Clamp(parsed, MinBufferSeconds, MaxBufferSeconds);
            _logger.LogDebug("{EnvVar} detected: {Value} (clamped to {Clamped})",
                BufferSecondsEnv, bufferSecondsValue, clamped);
            return clamped;
        }
        else
        {
            _logger.LogWarning("{EnvVar} value '{Value}' is not a valid integer, using default {Default}",
                BufferSecondsEnv, bufferSecondsValue, DefaultBufferSeconds);
        }
    }
    else
    {
        _logger.LogDebug("{EnvVar} environment variable not set", BufferSecondsEnv);
    }

    // Check HAOS options
    if (_isHaos && _haosOptions != null)
    {
        if (_haosOptions.TryGetValue("buffer_seconds", out var element))
        {
            try
            {
                var haosValue = element.GetInt32();
                var clamped = Math.Clamp(haosValue, MinBufferSeconds, MaxBufferSeconds);
                _logger.LogDebug("Buffer seconds from HAOS options: {Value} (clamped to {Clamped})",
                    haosValue, clamped);
                return clamped;
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("HAOS option 'buffer_seconds' is not an integer value");
            }
        }
    }

    return DefaultBufferSeconds;
}
```

**Step 3: Call detection in constructor**

Add after the `_enableAdvancedFormats` detection block (after line 101), before the closing `}` of the constructor:

```csharp
// Detect buffer seconds setting
_bufferSeconds = DetectBufferSeconds();
_logger.LogInformation("Audio buffer size: {BufferSeconds} seconds", _bufferSeconds);
```

**Step 4: Add SettingsConfigPath property**

Add after `MockHardwareConfigPath` property (around line 150):

```csharp
/// <summary>
/// Full path to settings.yaml configuration file (global settings).
/// </summary>
public string SettingsConfigPath => Path.Combine(_configPath, "settings.yaml");
```

**Step 5: Build and verify**

Run:
```bash
cd /c/CodeProjects/Multi-SendSpin-Player-Container-feat-adjustable-buffer && dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj
```
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/MultiRoomAudio/Services/EnvironmentService.cs
git commit -m "feat: add BufferSeconds property to EnvironmentService

Read from BUFFER_SECONDS env var or HAOS options, default 30s, clamped 5-30s.
Mutable at runtime for settings API updates."
```

---

### Task 2: Wire BufferSeconds into PlayerManagerService

**Files:**
- Modify: `src/MultiRoomAudio/Services/PlayerManagerService.cs`

**Step 1: Replace constant with readonly field**

Find:
```csharp
private const int LocalBufferCapacityMs = 30_000;
```

Replace with:
```csharp
private readonly int _localBufferCapacityMs;
```

**Step 2: Initialize in constructor**

In the constructor (which already receives `EnvironmentService environment`), add:

```csharp
_localBufferCapacityMs = environment.BufferSeconds * 1000;
_logger.LogInformation("Audio buffer capacity: {BufferMs}ms ({BufferSeconds}s)",
    _localBufferCapacityMs, environment.BufferSeconds);
```

**Step 3: Update usage**

Find all references to `LocalBufferCapacityMs` and replace with `_localBufferCapacityMs`. There should be one usage in the buffer factory lambda where the timed buffer is created.

**Step 4: Build and verify**

Run:
```bash
cd /c/CodeProjects/Multi-SendSpin-Player-Container-feat-adjustable-buffer && dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj
```
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/MultiRoomAudio/Services/PlayerManagerService.cs
git commit -m "feat: use configurable buffer size from EnvironmentService

Replace hardcoded 30_000ms constant with value from EnvironmentService.BufferSeconds."
```

---

### Task 3: Settings Persistence in ConfigurationService

**Files:**
- Modify: `src/MultiRoomAudio/Services/ConfigurationService.cs`
- Modify: `src/MultiRoomAudio/Models/` (add GlobalSettings model inline or in existing file)

**Step 1: Add GlobalSettings model**

Add a simple model class. Check if there's an existing Models file that makes sense; otherwise add it at the top of ConfigurationService.cs or in a new minimal model file:

```csharp
public class GlobalSettings
{
    public int BufferSeconds { get; set; } = 30;
}
```

**Step 2: Add settings load/save to ConfigurationService**

Add a field:
```csharp
private GlobalSettings _globalSettings = new();
```

Add a public property:
```csharp
public GlobalSettings GlobalSettings => _globalSettings;
```

Add load method:
```csharp
public GlobalSettings LoadSettings(string settingsPath)
{
    if (!File.Exists(settingsPath))
    {
        _logger.LogDebug("No settings.yaml found at {Path}, using defaults", settingsPath);
        _globalSettings = new GlobalSettings();
        return _globalSettings;
    }

    try
    {
        var yaml = File.ReadAllText(settingsPath);
        _globalSettings = _deserializer.Deserialize<GlobalSettings>(yaml) ?? new GlobalSettings();
        _logger.LogInformation("Loaded global settings from {Path}: BufferSeconds={BufferSeconds}",
            settingsPath, _globalSettings.BufferSeconds);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to load settings from {Path}, using defaults", settingsPath);
        _globalSettings = new GlobalSettings();
    }

    return _globalSettings;
}
```

Add save method:
```csharp
public void SaveSettings(string settingsPath, GlobalSettings settings)
{
    try
    {
        var yaml = _serializer.Serialize(settings);
        var dir = Path.GetDirectoryName(settingsPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(settingsPath, yaml);
        _globalSettings = settings;
        _logger.LogInformation("Saved global settings to {Path}: BufferSeconds={BufferSeconds}",
            settingsPath, settings.BufferSeconds);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to save settings to {Path}", settingsPath);
        throw;
    }
}
```

**Step 3: Load settings on startup**

In `Program.cs` or wherever `ConfigurationService` is initialized, after `EnsureDirectoriesExist()`, add:

```csharp
config.LoadSettings(environment.SettingsConfigPath);
```

Then apply the loaded value to EnvironmentService (so persisted config overrides env var):

```csharp
if (config.GlobalSettings.BufferSeconds != environment.BufferSeconds)
{
    environment.BufferSeconds = config.GlobalSettings.BufferSeconds;
}
```

**Step 4: Build and verify**

Run:
```bash
cd /c/CodeProjects/Multi-SendSpin-Player-Container-feat-adjustable-buffer && dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj
```
Expected: Build succeeded

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add GlobalSettings model and settings.yaml persistence

LoadSettings/SaveSettings on ConfigurationService, loaded on startup."
```

---

### Task 4: Settings API Endpoint

**Files:**
- Create: `src/MultiRoomAudio/Controllers/SettingsEndpoint.cs`

**Step 1: Create the endpoint file**

```csharp
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

public static class SettingsEndpoint
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings");

        group.MapGet("/buffer", (EnvironmentService env, PlayerManagerService players) =>
        {
            var bufferSeconds = env.BufferSeconds;
            var playerCount = players.GetAllPlayers().Count();

            return Results.Ok(new
            {
                bufferSeconds,
                playerCount,
                memoryEstimates = new[]
                {
                    new { sampleRate = 48000, label = "48 kHz", perPlayerMb = CalcMb(bufferSeconds, 48000), totalMb = CalcMb(bufferSeconds, 48000) * playerCount },
                    new { sampleRate = 96000, label = "96 kHz", perPlayerMb = CalcMb(bufferSeconds, 96000), totalMb = CalcMb(bufferSeconds, 96000) * playerCount },
                    new { sampleRate = 192000, label = "192 kHz", perPlayerMb = CalcMb(bufferSeconds, 192000), totalMb = CalcMb(bufferSeconds, 192000) * playerCount }
                }
            });
        });

        group.MapPut("/buffer", (BufferUpdateRequest request,
            EnvironmentService env,
            ConfigurationService config,
            PlayerManagerService players) =>
        {
            if (request.BufferSeconds < 5 || request.BufferSeconds > 30 || request.BufferSeconds % 5 != 0)
            {
                return Results.BadRequest(new { success = false, message = "Buffer size must be 5, 10, 15, 20, 25, or 30 seconds." });
            }

            env.BufferSeconds = request.BufferSeconds;
            config.SaveSettings(env.SettingsConfigPath, new GlobalSettings { BufferSeconds = request.BufferSeconds });

            var playerCount = players.GetAllPlayers().Count();

            return Results.Ok(new
            {
                success = true,
                message = "Buffer size updated. Restart players to apply the new buffer size.",
                bufferSeconds = env.BufferSeconds,
                playerCount,
                memoryEstimates = new[]
                {
                    new { sampleRate = 48000, label = "48 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 48000), totalMb = CalcMb(env.BufferSeconds, 48000) * playerCount },
                    new { sampleRate = 96000, label = "96 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 96000), totalMb = CalcMb(env.BufferSeconds, 96000) * playerCount },
                    new { sampleRate = 192000, label = "192 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 192000), totalMb = CalcMb(env.BufferSeconds, 192000) * playerCount }
                }
            });
        });
    }

    private static double CalcMb(int bufferSeconds, int sampleRate)
    {
        // buffer_seconds * sample_rate * 2 channels * 4 bytes (float32) / 1,048,576
        return Math.Round((double)bufferSeconds * sampleRate * 2 * 4 / 1_048_576, 1);
    }
}

public record BufferUpdateRequest(int BufferSeconds);
```

**Step 2: Register the endpoint in Program.cs**

Find where other endpoints are mapped (e.g., `app.MapPlayersEndpoints()`) and add:

```csharp
app.MapSettingsEndpoints();
```

**Step 3: Build and verify**

Run:
```bash
cd /c/CodeProjects/Multi-SendSpin-Player-Container-feat-adjustable-buffer && dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj
```
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/MultiRoomAudio/Controllers/SettingsEndpoint.cs src/MultiRoomAudio/Program.cs
git commit -m "feat: add GET/PUT /api/settings/buffer endpoint

Returns buffer size, player count, and memory estimates per sample rate."
```

---

### Task 5: Settings UI - Menu Item and Modal

**Files:**
- Modify: `src/MultiRoomAudio/wwwroot/index.html`
- Modify: `src/MultiRoomAudio/wwwroot/js/app.js`

**Step 1: Add "System" menu item to settings dropdown in index.html**

Find the settings dropdown items (around lines 50-72). Before the `<div class="dropdown-divider">` that precedes the wizard link, add:

```html
<a class="dropdown-item" href="#" onclick="openSystemSettingsModal(); return false;">
    <i class="bi bi-gear"></i> System
</a>
```

**Step 2: Add systemSettingsModal to index.html**

Add a new modal before the closing `</body>` tag (or alongside other modals):

```html
<!-- System Settings Modal -->
<div class="modal fade" id="systemSettingsModal" tabindex="-1" aria-labelledby="systemSettingsModalLabel" aria-hidden="true">
    <div class="modal-dialog">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title" id="systemSettingsModalLabel">System Settings</h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
            </div>
            <div class="modal-body">
                <h6>Audio Buffer Size</h6>
                <div class="mb-3">
                    <label for="bufferSlider" class="form-label">
                        Buffer: <span id="bufferSliderValue">30</span> seconds
                    </label>
                    <input type="range" class="form-range" id="bufferSlider"
                           min="5" max="30" step="5" value="30"
                           oninput="updateBufferMemoryTable()">
                </div>
                <div class="small text-muted mb-2">
                    Audio is buffered as decoded PCM float32 regardless of the codec used
                    (FLAC, Opus, PCM). Memory usage depends on sample rate, not codec.
                </div>
                <table class="table table-sm table-bordered" id="bufferMemoryTable">
                    <thead>
                        <tr>
                            <th>Sample Rate</th>
                            <th>Per Player</th>
                            <th id="bufferMemoryTotalHeader">Total (0 players)</th>
                        </tr>
                    </thead>
                    <tbody id="bufferMemoryTableBody">
                    </tbody>
                </table>
                <div id="bufferSaveMessage" class="small text-warning" style="display:none;"></div>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
                <button type="button" class="btn btn-primary" onclick="saveBufferSettings()">Save</button>
            </div>
        </div>
    </div>
</div>
```

**Step 3: Add JavaScript functions in app.js**

Add to app.js:

```javascript
// ── System Settings ──────────────────────────────────────────────

let _bufferPlayerCount = 0;

async function openSystemSettingsModal() {
    try {
        const resp = await fetch('/api/settings/buffer');
        if (!resp.ok) throw new Error('Failed to load buffer settings');
        const data = await resp.json();

        _bufferPlayerCount = data.playerCount;
        document.getElementById('bufferSlider').value = data.bufferSeconds;
        updateBufferMemoryTable();

        const modal = new bootstrap.Modal(document.getElementById('systemSettingsModal'));
        modal.show();
    } catch (err) {
        console.error('Failed to open system settings:', err);
    }
}

function updateBufferMemoryTable() {
    const slider = document.getElementById('bufferSlider');
    const seconds = parseInt(slider.value, 10);
    document.getElementById('bufferSliderValue').textContent = seconds;

    const rates = [
        { rate: 48000, label: '48 kHz' },
        { rate: 96000, label: '96 kHz' },
        { rate: 192000, label: '192 kHz' }
    ];

    const tbody = document.getElementById('bufferMemoryTableBody');
    // Clear existing rows
    while (tbody.firstChild) {
        tbody.removeChild(tbody.firstChild);
    }

    for (const { rate, label } of rates) {
        const perPlayer = (seconds * rate * 2 * 4 / 1048576).toFixed(1);
        const total = (perPlayer * _bufferPlayerCount).toFixed(1);

        const tr = document.createElement('tr');

        const tdRate = document.createElement('td');
        tdRate.textContent = label;
        tr.appendChild(tdRate);

        const tdPer = document.createElement('td');
        tdPer.textContent = perPlayer + ' MB';
        tr.appendChild(tdPer);

        const tdTotal = document.createElement('td');
        tdTotal.textContent = total + ' MB';
        tr.appendChild(tdTotal);

        tbody.appendChild(tr);
    }

    const header = document.getElementById('bufferMemoryTotalHeader');
    header.textContent = 'Total (' + _bufferPlayerCount + ' player' + (_bufferPlayerCount !== 1 ? 's' : '') + ')';

    // Hide save message when slider changes
    document.getElementById('bufferSaveMessage').style.display = 'none';
}

async function saveBufferSettings() {
    const seconds = parseInt(document.getElementById('bufferSlider').value, 10);
    const msgEl = document.getElementById('bufferSaveMessage');

    try {
        const resp = await fetch('/api/settings/buffer', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ bufferSeconds: seconds })
        });
        const data = await resp.json();

        if (resp.ok && data.success) {
            msgEl.className = 'small text-success';
            msgEl.textContent = data.message;
        } else {
            msgEl.className = 'small text-danger';
            msgEl.textContent = data.message || 'Failed to save.';
        }
    } catch (err) {
        msgEl.className = 'small text-danger';
        msgEl.textContent = 'Error saving settings: ' + err.message;
    }

    msgEl.style.display = 'block';
}
```

**Step 4: Build and verify**

Run:
```bash
cd /c/CodeProjects/Multi-SendSpin-Player-Container-feat-adjustable-buffer && dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj
```
Expected: Build succeeded

**Step 5: Manual test**

Run the app with `MOCK_HARDWARE=true` and open http://localhost:8096. Verify:
- "System" appears in the settings dropdown
- Clicking it opens the modal with slider and memory table
- Moving the slider updates the table values
- Saving shows the restart message

**Step 6: Commit**

```bash
git add src/MultiRoomAudio/wwwroot/index.html src/MultiRoomAudio/wwwroot/js/app.js
git commit -m "feat: add System Settings modal with buffer size slider and memory table

Slider range 5-30s (step 5). Memory table shows per-player and total
estimates at 48/96/192 kHz sample rates."
```

---

### Task 6: Update CLAUDE.md Environment Variables

**Files:**
- Modify: `CLAUDE.md`

**Step 1: Add BUFFER_SECONDS to environment variables table**

Find the Environment Variables table and add a new row:

```markdown
| `BUFFER_SECONDS` | `30` | Audio buffer size in seconds (5-30, step 5). Lower values reduce RAM on constrained hardware. |
```

**Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add BUFFER_SECONDS to environment variables table"
```
