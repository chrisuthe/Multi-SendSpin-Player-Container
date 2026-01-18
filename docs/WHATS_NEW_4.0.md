# What's New in 4.0.0

**The biggest update yet** - Multi-Room Audio Controller 4.0 transforms how you set up and manage your whole-home audio system.

---

## Your First Five Minutes Just Got Better

Remember the first time you installed the add-on? You stared at an empty dashboard, wondering where to start. Should you add a player first? Which device do you pick? What about that multi-channel DAC - how do you split it into separate rooms?

**Not anymore.**

### The Setup Wizard

4.0 welcomes new users with a guided setup experience:

1. **Discover** - The wizard scans for audio hardware automatically
2. **Configure** - Set up sound card profiles with one click
3. **Create** - Build your first players with intelligent defaults
4. **Test** - Hear a test tone to confirm everything works

Skip the guesswork. Skip the documentation rabbit hole. Just get music playing.

For existing users: Run the wizard anytime from **Settings > Run Setup Wizard** when you add new hardware.

---

## Custom Sinks: Your Audio, Your Way

This is the feature power users have been waiting for.

### The Problem

You have a 4-channel USB DAC. You want channels 1-2 going to the living room and channels 3-4 going to the kitchen. Before 4.0, you needed to SSH in, edit PulseAudio config files, restart services, and hope you got the syntax right.

### The Solution

**Custom Sinks** let you create virtual audio outputs directly from the web interface:

#### Combine Sink
*"Play the same audio on multiple outputs simultaneously"*

Perfect for:
- Party mode (all speakers play together)
- Paired stereo speakers in large rooms
- Redundant outputs for critical zones

#### Remap Sink
*"Extract specific channels from a multi-channel device"*

Perfect for:
- Multi-channel DACs split into rooms
- Surround receivers used as multi-zone amps
- Pro audio interfaces with multiple stereo pairs

Create them in **Settings > Custom Sinks**. No command line required.

---

## Sound Card Profiles

Some USB DACs and audio interfaces support multiple modes. A USB audio interface might offer:
- Stereo output only
- Multi-channel output
- Different sample rate configurations

**Sound Card Profiles** lets you switch modes from the UI:

1. Go to **Settings > Sound Card Setup**
2. See all your cards and their available profiles
3. Click to switch - changes apply instantly
4. Profiles persist across reboots

Your audio interface's full potential, unlocked.

---

## Device Volume Limits

Protect your speakers and ears with per-device volume limits.

Configure maximum volume limits in the sound card settings:

- **Limit Max. Vol.** slider: Set the maximum volume for each sound card
- Applied at the hardware level for safety
- Persists across restarts
- Prevents accidental over-driving of speakers

Find this control in Settings > Sound Cards, alongside mute options and profile selection.

---

## Smarter Player Management

### Auto-Reconnection

Server went offline? Network hiccup? Players now automatically reconnect when the Music Assistant server becomes available again. No more manually restarting players after a server restart.

### Better Error Handling

When something goes wrong with a sink or player, you get clear error messages explaining what happened and how to fix it. No more cryptic failures.

### Editable Broken Players

Previously, if hardware changed or disconnected, players became locked and uneditable. Now you can modify or delete players even when their hardware is unavailable.

---

## For the Upgraders

### Breaking Changes

None! 4.0 is backward-compatible with your existing configuration.

### What Happens on First Launch

1. Existing players continue working unchanged
2. The onboarding wizard won't appear (you already have players)
3. New features are available in the Settings menu

### New Settings Menu

The header now has a **Settings** dropdown with:
- Sound Card Setup
- Custom Sinks
- Run Setup Wizard
- Reset First-Run State

---

## Under the Hood

For the technically curious:

- **SendSpin.SDK 5.1.0** - Latest protocol improvements
- **Improved PulseAudio integration** - Better card and sink enumeration
- **New API endpoints** - `/api/sinks`, `/api/cards`, `/api/onboarding`, `/api/devices/{id}/max-volume`
- **Device-level volume limits** - Applied to PulseAudio sinks at startup

---

## Getting Started with 4.0

| I want to... | Do this |
|--------------|---------|
| Set up from scratch | Just install - the wizard guides you |
| Split a multi-channel DAC | Settings > Custom Sinks > Add Remap Sink |
| Combine multiple outputs | Settings > Custom Sinks > Add Combine Sink |
| Change sound card mode | Settings > Sound Card Setup |
| Re-run initial setup | Settings > Run Setup Wizard |

---

## Thank You

This release represents months of work based on community feedback. Special thanks to everyone who reported issues, requested features, and tested pre-releases.

Found a bug? Have a suggestion? [Open an issue](https://github.com/chrisuthe/squeezelite-docker/issues).

---

*Multi-Room Audio Controller 4.0 - Making whole-home audio accessible to everyone.*
