# GitHub Issues Analysis & Sprint Planning

> Generated: 2026-01-12
> Repository: chrisuthe/Multi-SendSpin-Player-Container

## Summary

- **Total Open Issues**: 15
- **Bugs**: 10
- **Enhancements**: 3
- **Features**: 2

---

## Sprint 1 - Critical UX Fixes (Quick Wins)

| # | Title | Priority | Complexity | Agent | Status |
|---|-------|----------|------------|-------|--------|
| #51 | Hiding master sinks in wizard not working | High | Low | fullstack-developer | 🔄 Planning |
| #50 | Import sink dialog pops behind | High | Low | ui-ux-designer | 🔄 Planning |
| #44 | Player name doesn't support apostrophe | Medium | Low | fullstack-developer | 🔄 Planning |
| #46 | Sink descriptions don't support spaces or & | Medium | Low | fullstack-developer | 🔄 Planning |
| #34 | Volume 100% formatting issue | Low | Low | ui-ux-designer | 🔄 Planning |

---

## Sprint 2 - Integration & State Bugs

| # | Title | Priority | Complexity | Agent | Status |
|---|-------|----------|------------|-------|--------|
| #48 | Player rename not reflected in MA | High | Medium | fullstack-developer | ⏳ Pending |
| #52 | Unmute channels after profile selected | Medium | Medium | fullstack-developer | ⏳ Pending |
| #33 | Players show playing when streams ended | Low | Medium | fullstack-developer | ⏳ Pending |
| #45 | Inconsistent device names | Medium | Medium | fullstack-developer | ⏳ Pending |

---

## Sprint 3 - Features & Enhancements

| # | Title | Priority | Complexity | Agent | Status |
|---|-------|----------|------------|-------|--------|
| #47 | Wizard player name choice from description | Medium | Low | fullstack-developer | ⏳ Pending |
| #49 | Improve sink card UI | Medium | Medium | ui-ux-designer | ⏳ Pending |
| #28 | Add device icons to player cards | Low | Low | ui-ux-designer | ⏳ Pending |
| #38 | Configurable hardware volume | Medium | High | fullstack-developer | ⏳ Pending |

---

## Backlog - Requires Investigation

| # | Title | Priority | Complexity | Agent | Status |
|---|-------|----------|------------|-------|--------|
| #37 | Delay at start of playback | Medium | High | debugger-Agent | ⏳ Pending |
| #30 | Support 192kHz output | Low | High | fullstack-developer | ⏳ Pending |

---

## Detailed Issue Notes

### #51 - Hiding master sinks in wizard not working
- **Location**: `wizard.js` - sink filtering logic
- **Problem**: Master sinks should be hidden but still appear
- **Files to check**: `wizard.js`, device enumeration API

### #50 - Import sink dialog pops behind
- **Location**: Modal z-index/stacking
- **Problem**: Import dialog appears behind other modals
- **Files to check**: `app.js`, CSS modal styles
- **Related**: Similar to modal caching fixes already made

### #44 - Player name doesn't support apostrophe
- **Location**: Validation regex in frontend and backend
- **Problem**: Apostrophe (') rejected in player names
- **Files to check**:
  - `wizard.js` - `sanitizePlayerName()`
  - `PlayerManagerService.cs` - `ValidatePlayerName()`
  - `PlayerConfig.cs` - validation attributes

### #46 - Sink descriptions don't support spaces or &
- **Location**: `CustomSinksService.cs`
- **Problem**: Special chars in sink descriptions cause issues
- **Files to check**: Sink creation/validation, PulseAudio escaping

### #34 - Volume 100% formatting issue
- **Location**: UI display
- **Problem**: Volume shows incorrectly at 100%
- **Files to check**: `app.js` - volume display formatting

### #48 - Player rename not reflected in MA
- **Location**: SDK/API integration
- **Problem**: Renamed players don't sync to Music Assistant
- **Files to check**: Player rename API, SDK client ID handling

### #52 - Unmute channels after profile selected
- **Location**: `CardProfileService.cs`
- **Problem**: Channels muted after profile switch
- **Fix**: Run `pactl set-sink-mute` after profile change

### #33 - Players show playing when streams ended
- **Location**: Player state management
- **Problem**: Status doesn't update when stream ends
- **Files to check**: SDK callbacks, status update logic

### #45 - Inconsistent device names
- **Location**: Device enumeration
- **Problem**: Same device shows different names in different contexts
- **Files to check**: `BackendFactory`, device display logic

### #47 - Wizard player name choice from description
- **Location**: `wizard.js`
- **Feature**: Auto-suggest player names from device description
- **Files to check**: `Wizard.suggestName()`, device metadata

### #49 - Improve sink card UI
- **Location**: `index.html`, CSS
- **Feature**: Visual improvements to sink cards
- **Files to check**: Sink card rendering in `app.js`

### #28 - Add device icons to player cards
- **Location**: UI
- **Feature**: Icon mapping based on device type
- **Files to check**: Player card rendering

### #38 - Configurable hardware volume
- **Location**: Audio backend
- **Feature**: Direct ALSA mixer control
- **Files to check**: `VolumeCommandRunner`, player volume API

### #37 - Delay at start of playback
- **Location**: SDK/audio pipeline
- **Problem**: 5-second buffer delay
- **Requires**: SDK investigation

### #30 - Support 192kHz output
- **Location**: SDK + PulseAudio
- **Feature**: High sample rate passthrough
- **Requires**: SDK and PA config changes

---

## Agent Assignment Summary

| Agent Type | Issues |
|------------|--------|
| fullstack-developer | #51, #48, #52, #46, #45, #44, #33, #47, #38, #30 |
| ui-ux-designer | #50, #49, #34, #28 |
| debugger-Agent | #37 |
