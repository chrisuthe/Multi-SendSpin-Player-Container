# Getting Started with Multi-Room Audio

**Time to first audio**: 5 minutes | **Difficulty**: Beginner

---

## The Problem

You want whole-home audio but:

- **Commercial systems are expensive** - Sonos, HEOS, and similar cost $200-500 per room
- **You have unused audio gear** - DACs, amplifiers, and speakers sitting idle
- **You want software control** - Integration with Music Assistant, LMS, or Home Assistant
- **You need flexibility** - Different rooms, different requirements

## The Solution

Multi-Room Audio Docker Controller turns any Docker host into a multi-zone audio server:

1. Connect USB DACs or use built-in audio outputs
2. Run one container that manages all players
3. Each player appears as a controllable zone in your audio software
4. Control everything from a web interface

```
Your Server (NAS, Pi, etc.)
         |
    [Container]
    /    |    \
 DAC1  DAC2  DAC3
   |     |     |
Kitchen Bedroom Patio
```

---

## Choose Your Path

| Your Situation | Start Here |
|----------------|------------|
| I run Docker on a NAS/server/Pi | [Quick Start (Docker)](#quick-start-docker) |
| I run Home Assistant OS | [Quick Start (HAOS)](#quick-start-haos) |
| I want to understand my options first | [Understanding the Backends](#understanding-the-backends) |

---

## Quick Start (Docker)

### Prerequisites

- Docker installed and running
- An audio server: Music Assistant, LMS, or Snapcast Server
- (Optional) USB DAC connected

### Step 1: Deploy the Container

```bash
docker run -d \
  --name multiroom-audio \
  -p 8080:8080 \
  --device /dev/snd:/dev/snd \
  chrisuthe/squeezelitemultiroom:latest
```

### Step 2: Open the Web Interface

Navigate to: `http://YOUR-SERVER-IP:8080`

You should see the Multi-Room Audio Controller dashboard.

### Step 3: Create Your First Player

1. Click **"Add Player"**
2. Fill in the form:
   - **Name**: `Test Player` (or your room name)
   - **Player Type**:
     - Choose **Sendspin** for Music Assistant
     - Choose **Squeezelite** for LMS
     - Choose **Snapcast** for Snapcast Server
   - **Audio Device**: Select your device, or `null` for testing
3. Click **"Create Player"**
4. Click **"Start"** on your new player

### Step 4: Verify It Works

**Music Assistant users:**
1. Open Music Assistant
2. Go to Settings > Players
3. Your player should appear within 30 seconds

**LMS users:**
1. Open LMS web interface
2. Check the player dropdown
3. Your player should be listed

**Snapcast users:**
1. Open Snapcast server web interface
2. Your client should appear in the list

### Success! What's Next?

- Add persistent storage so config survives restarts
- Create more players for each room
- Check Troubleshooting if something isn't working

---

## Quick Start (HAOS)

### Prerequisites

- Home Assistant OS or Supervised installation
- Music Assistant add-on (recommended) or external LMS/Snapcast

### Step 1: Add the Repository

1. Go to **Settings** > **Add-ons** > **Add-on Store**
2. Click the three-dot menu (top right)
3. Select **Repositories**
4. Add: `https://github.com/chrisuthe/squeezelite-docker`
5. Click **Add**, then **Close**

### Step 2: Install the Add-on

1. Refresh the add-on store page
2. Find **"Multi-Room Audio Controller"**
3. Click **Install**
4. Wait for installation to complete

### Step 3: Start and Access

1. Go to the add-on's **Info** tab
2. Click **Start**
3. Enable **Show in sidebar** (optional but recommended)
4. Click **Open Web UI**

### Step 4: Create Your First Player

1. Click **"Add Player"**
2. Fill in the form:
   - **Name**: Your room name (e.g., "Kitchen")
   - **Player Type**: Choose based on your audio server
   - **Audio Device**: Select from available PulseAudio sinks
3. Click **"Create Player"**
4. Click **"Start"**

### Step 5: Verify in Music Assistant

1. Open Music Assistant
2. Go to Settings > Players
3. Your new player should appear

### Success! What's Next?

- Connect USB DACs for more zones
- Understand HAOS-specific differences
- Check Troubleshooting if needed

---

## Understanding the Backends

This project supports three audio player backends. Choose based on your setup:

### Sendspin (Recommended for Music Assistant)

| Aspect | Details |
|--------|---------|
| **Best for** | Music Assistant users |
| **Protocol** | Native Music Assistant |
| **Discovery** | Automatic (mDNS) |
| **Sync quality** | Excellent |
| **Server IP needed** | No (auto-discovers) |

**Use Sendspin if**: You run Music Assistant and want the best integration.

### Squeezelite (Universal)

| Aspect | Details |
|--------|---------|
| **Best for** | LMS users, mixed environments |
| **Protocol** | SlimProto |
| **Discovery** | Automatic or manual IP |
| **Sync quality** | Good |
| **Server IP needed** | Optional |

**Use Squeezelite if**: You run Logitech Media Server, or want compatibility with both LMS and Music Assistant.

### Snapcast (Bit-Perfect Sync)

| Aspect | Details |
|--------|---------|
| **Best for** | Audiophiles, perfect sync requirements |
| **Protocol** | Snapcast |
| **Discovery** | Manual IP required |
| **Sync quality** | Excellent (sub-millisecond) |
| **Server IP needed** | Yes |

**Use Snapcast if**: You run a Snapcast server and need bit-perfect synchronized playback.

### Quick Comparison

| Feature | Sendspin | Squeezelite | Snapcast |
|---------|----------|-------------|----------|
| Music Assistant native | Yes | Via SlimProto | No |
| LMS compatible | No | Yes | No |
| Auto-discovery | Yes | Yes | No |
| Requires server IP | No | Optional | Yes |
| Best sync quality | Good | Good | Excellent |

---

## Common First-Time Issues

### "No audio devices found"

**Docker**: Ensure you passed `--device /dev/snd:/dev/snd`

**HAOS**: USB devices appear after add-on restart. Try:
1. Restart the add-on
2. Check Settings > System > Hardware for your device

### "Player won't start"

1. Try creating a player with `null` device first (tests without audio)
2. Check the player logs in the web interface
3. Verify your audio server is running and reachable

### "Player not appearing in Music Assistant"

1. Wait 30-60 seconds for discovery
2. Restart Music Assistant
3. For Squeezelite: try setting the server IP explicitly

---

## Next Steps

| Goal | Documentation |
|------|---------------|
| Production Docker deployment | [Docker Deployment Guide](Docker-Deployment) |
| Full HAOS setup | [HAOS Add-on Guide](HAOS_ADDON_GUIDE.md) |
| All configuration options | Configuration Reference |
| Something not working | Troubleshooting |
| Platform-specific (TrueNAS, Synology) | Platform Guides |
