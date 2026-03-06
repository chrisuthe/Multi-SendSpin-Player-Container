# Configurable Audio Buffer Size Design

**Date**: 2026-03-05
**Branch**: task/feat-adjustable-buffer

---

## Overview

Add a global setting to configure the audio buffer size (5-30 seconds) to accommodate
resource-constrained hardware like Raspberry Pi running many players.

## Setting

- **Scope**: Global (applies to all players)
- **Storage**: Environment variable `BUFFER_SECONDS` with config persistence
- **Range**: 5-30 seconds, step 5
- **Default**: 30 seconds
- **Restart required**: Yes, changing buffer size requires player restart since buffers are allocated at player creation

## UI - Settings Page

Slider control labeled "Audio Buffer Size" showing current value (e.g., "15 seconds").

Below the slider, a memory usage table with explanatory note:

> **Memory Usage Estimate**
>
> Audio is buffered as decoded PCM float32 regardless of the codec used
> (FLAC, Opus, PCM). Memory usage depends on sample rate, not codec.
>
> | Sample Rate | Per Player | Total (N players) |
> |-------------|-----------|-------------------|
> | 48 kHz      | X MB      | Y MB              |
> | 96 kHz      | X MB      | Y MB              |
> | 192 kHz     | X MB      | Y MB              |

- Player count (N) updates dynamically based on current player count
- Memory values recalculate as slider moves
- Formula: `buffer_seconds * sample_rate * 2 channels * 4 bytes / 1,048,576` = MB per player

## Implementation

### EnvironmentService
- Read `BUFFER_SECONDS` env var
- Default: 30
- Clamp to range 5-30
- Expose as `BufferSeconds` property (int)

### PlayerManagerService
- Replace `const int LocalBufferCapacityMs = 30_000` with value from `EnvironmentService.BufferSeconds * 1000`
- Read once at construction, used for all player creation

### Settings API
- `GET /api/settings/buffer` - Returns current buffer size and player count
- `PUT /api/settings/buffer` - Updates buffer size, persists to config, returns restart-required flag

### Config Persistence
- Store in existing config mechanism (ConfigurationService or similar)
- Load on startup, override env var if present

### Settings UI
- Slider: range 5-30, step 5, with numeric display
- Memory table: recalculates on slider change
- Save button: calls PUT, shows "restart players to apply" message

## Files to Modify

| File | Change |
|------|--------|
| `EnvironmentService.cs` | Add `BufferSeconds` property, read from env/config |
| `PlayerManagerService.cs` | Use `EnvironmentService.BufferSeconds * 1000` instead of constant |
| `ConfigurationService.cs` | Persist buffer setting |
| Settings API endpoint | GET/PUT for buffer size |
| `wwwroot/js/app.js` | Slider + memory table on settings page |

## Out of Scope

- Per-player buffer sizes
- Live buffer resize without player restart
